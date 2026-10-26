namespace ndx;

/// <summary>
/// Runs a floating-version tool and restarts it when a newer matching version
/// appears. Stages the update on disk before nice-stopping the current child.
/// </summary>
public static class Evergreen
{
    public static async Task<int> RunAsync(
        Invocation invocation,
        NdxHost host,
        ToolPackageStore store,
        IReadOnlyList<string> sources,
        ToolCommand command,
        string? muxer,
        CancellationToken cancellationToken = default)
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
                try
                {
                    update = await TryFindUpdateAsync(
                            store, invocation, sources, range, command.Version, token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (detailed)
                        host.Error.WriteLine($"Update check failed: {ex.Message}");
                }

                if (update is null)
                    continue;

                if (child.HasExited)
                    return child.ExitCode;

                if (!quiet)
                    host.Out.WriteLine($"Updating {invocation.PackageId} {command.Version} → {update.Version}");

                // StopAsync returns once this child and the processes it started have exited.
                using (shutdown.StoppingChild())
                    await child.StopAsync(host.StopTimeout, CancellationToken.None).ConfigureAwait(false);

                await child.DisposeAsync().ConfigureAwait(false);
                command = update;
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
