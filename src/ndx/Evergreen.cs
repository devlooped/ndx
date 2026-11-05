namespace ndx;

/// <summary>
/// Runs a floating-version tool and restarts it when a newer matching version
/// appears. Cached updates are staged before the current child is stopped.
/// </summary>
public static class Evergreen
{
    public static Task<int> RunAsync(
        Invocation invocation,
        NdxHost host,
        ToolPackageStore store,
        IReadOnlyList<string> sources,
        ToolCommand command,
        string? muxer,
        CancellationToken cancellationToken = default)
        => RunAsync(invocation, host, store, sources, command, muxer, cancellationToken, globalStore: null);

    internal static async Task<int> RunAsync(
        Invocation invocation,
        NdxHost host,
        ToolPackageStore store,
        IReadOnlyList<string> sources,
        ToolCommand command,
        string? muxer,
        CancellationToken cancellationToken,
        GlobalToolStore? globalStore)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(command);

        var interval = host.UpdateInterval ?? NetConfig.ReadUpdateInterval(host.WorkingDirectory);
        var range = VersionRange.FromInvocation(invocation);
        var quiet = IsQuiet(invocation.Verbosity);
        var detailed = IsDetailed(invocation.Verbosity);

        using var shutdown = new ShutdownScope();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        var token = linked.Token;

        var child = Start(command);
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (!token.IsCancellationRequested)
            {
                var waitChild = child.WaitForExitAsync(token);
                var waitTick = timer.WaitForNextTickAsync(token).AsTask();
                var completed = await Task.WhenAny(waitChild, waitTick).ConfigureAwait(false);

                if (completed == waitChild)
                    return await waitChild.ConfigureAwait(false);

                try
                {
                    await waitTick.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }

                ToolCommand? update = null;
                PackageVersion? globalVersion = null;
                try
                {
                    if (globalStore is null)
                    {
                        update = await TryFindUpdateAsync(
                                store, invocation, sources, range, command.Version, token)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        var latest = await globalStore.GetLatestVersionAsync(invocation, sources, token)
                            .ConfigureAwait(false);
                        if (latest.CompareTo(command.Version) > 0 && range.Matches(latest))
                            globalVersion = latest;
                    }
                }
                catch (Exception ex)
                {
                    if (detailed)
                        host.Error.WriteLine($"Update check failed: {ex.Message}");
                }

                if (update is null && globalVersion is null)
                    continue;

                if (child.HasExited)
                    return child.ExitCode;

                var nextVersion = update?.Version ?? globalVersion
                    ?? throw new InvalidOperationException("A package update was not resolved.");
                if (!quiet)
                    host.Out.WriteLine($"Updating {invocation.PackageId} {command.Version} → {nextVersion}");

                // StopAsync returns once this child and the processes it started have exited.
                using (shutdown.StoppingChild())
                    await child.StopAsync(host.StopTimeout, CancellationToken.None).ConfigureAwait(false);

                await child.DisposeAsync().ConfigureAwait(false);
                command = globalStore is null
                    ? update ?? throw new InvalidOperationException("A package update was not resolved.")
                    : await globalStore.GetAsync(invocation, sources, globalVersion, token).ConfigureAwait(false);
                child = Start(command);
            }

            if (!child.HasExited)
            {
                using (shutdown.StoppingChild())
                    await child.StopAsync(host.StopTimeout, CancellationToken.None).ConfigureAwait(false);
            }

            return child.HasExited ? child.ExitCode : 130;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!child.HasExited)
            {
                using (shutdown.StoppingChild())
                    await child.StopAsync(host.StopTimeout, CancellationToken.None).ConfigureAwait(false);
            }

            return child.HasExited ? child.ExitCode : 130;
        }
        finally
        {
            await child.DisposeAsync().ConfigureAwait(false);
        }

        IChildProcess Start(ToolCommand cmd)
        {
            var settings = ToolLauncher.CreateStartSettings(
                cmd,
                invocation.ForwardedArguments,
                invocation.AllowRollForward,
                host.WorkingDirectory,
                muxer);
            if (detailed)
                host.Out.WriteLine($"Starting {settings.FileName} {string.Join(' ', settings.Arguments)}");
            return host.ProcessRunner.Start(settings);
        }
    }

    static async Task<ToolCommand?> TryFindUpdateAsync(
        ToolPackageStore store,
        Invocation invocation,
        IReadOnlyList<string> sources,
        VersionRange range,
        PackageVersion current,
        CancellationToken cancellationToken)
    {
        var next = await store.GetAsync(invocation, sources, cancellationToken).ConfigureAwait(false);
        if (next.Version.CompareTo(current) <= 0)
            return null;

        if (!range.Matches(next.Version))
            return null;

        return next;
    }

    static bool IsQuiet(string? verbosity)
        => verbosity?.ToLowerInvariant() is "quiet" or "q";

    static bool IsDetailed(string? verbosity)
        => verbosity?.ToLowerInvariant() is "detailed" or "diagnostic" or "d" or "diag";
}
