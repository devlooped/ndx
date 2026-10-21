namespace ndx;

/// <summary>
/// Host overrides used by tests. Production uses <see cref="NdxHost.CreateDefault"/>.
/// </summary>
public sealed class NdxHost
{
    public string WorkingDirectory { get; init; } = Directory.GetCurrentDirectory();
    public string StoreDirectory { get; init; } = DefaultStoreDirectory();
    public IProcessRunner ProcessRunner { get; init; } = new ProcessRunner();
    public TextWriter Out { get; init; } = Console.Out;
    public TextWriter Error { get; init; } = Console.Error;
    public HttpMessageHandler? HttpHandler { get; init; }
    public string? ExecutablePath { get; init; }
    public string? CurrentVersion { get; init; }
    public string? UpdateRepository { get; init; }
    public string? RuntimeIdentifier { get; init; }
    public string? DotnetMuxer { get; init; }
    public TimeSpan? UpdateInterval { get; init; }
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public bool ShowProgress { get; init; }
    public TextWriter? Progress { get; init; }

    public static NdxHost CreateDefault()
    {
        var progress = ConsoleProgress.TryOpen();
        return new()
        {
            Progress = progress,
            ShowProgress = progress is not null,
        };
    }

    public static string DefaultStoreDirectory(string? workingDirectory = null)
        => Environment.GetEnvironmentVariable("NDX_STORE")
           ?? GlobalPackagesFolder.Resolve(workingDirectory);
}

/// <summary>
/// ndx entry orchestration: parse, download-to-cache, start child, relay exit code.
/// </summary>
public static class App
{
    const string Usage = """
        Usage: ndx <PACKAGE_NAME[@VERSION]> [options] [--] [tool arguments]
               ndx --update [VERSION|ci]
               ndx --version

        Unlike dnx, a bare package name is @*. A floating version stays current:
        ndx watches the feed and restarts the tool when a newer match appears.

        Options:
          --source <SOURCE>          Override package sources
          --add-source <SOURCE>      Add a package source
          --configfile <FILE>        NuGet configuration file
          --version <VERSION>        Package version or version range
          --prerelease               Include prerelease versions
          --yes, -y                  Do not prompt
          --allow-roll-forward       Allow major roll-forward for framework-dependent tools
          --verbosity, -v <LEVEL>    quiet|minimal|normal|detailed|diagnostic
          --disable-parallel         Disable parallel restore
          --ignore-failed-sources    Treat source failures as warnings
          --no-http-cache            Do not use an HTTP cache
          --interactive              Allow interactive restore prompts
          --update [VERSION]         Self-update to latest, a version, or ci (rolling prerelease)
          --version                  Print the ndx version
        """;

    public static int Run(string[] args) => RunAsync(args).GetAwaiter().GetResult();

    public static async Task<int> RunAsync(string[] args, NdxHost? host = null, CancellationToken cancellationToken = default)
    {
        host ??= NdxHost.CreateDefault();
        var invocation = ArgParser.Parse(args);
        if (invocation.ShowHelp)
        {
            host.Out.WriteLine(Usage);
            return 0;
        }

        if (invocation.ShowVersion)
        {
            host.Out.WriteLine(SelfUpdate.FormatVersion(host.CurrentVersion));
            return 0;
        }

        if (!invocation.Success)
        {
            host.Error.WriteLine(invocation.Error);
            return 1;
        }

        try
        {
            var (sources, credentials) = PackageSources.Resolve(invocation, host.WorkingDirectory);
            using var http = CreateHttpClient(host, credentials);

            if (invocation.Update)
                return await SelfUpdate.RunAsync(invocation, host, http, cancellationToken).ConfigureAwait(false);

            var log = IsDetailed(invocation.Verbosity) ? host.Out : null;
            var progress = host.Progress ?? (host.ShowProgress ? host.Error : null);
            var feed = new PackageFeed(http, invocation.IgnoreFailedSources, log, progress);
            var muxer = host.DotnetMuxer ?? DotnetMuxer.Resolve();
            var store = new ToolPackageStore(feed, host.StoreDirectory, log, muxer, host.RuntimeIdentifier);
            var command = await store.GetAsync(invocation, sources, cancellationToken).ConfigureAwait(false);
            var range = VersionRange.FromInvocation(invocation);
            // Bare, @*, @1.*, and ranges stay current. Exact pins are one-shot (like dnx).
            if (!range.IsExact)
            {
                return await Evergreen.RunAsync(
                    invocation, host, store, sources, command, muxer, cancellationToken)
                    .ConfigureAwait(false);
            }

            var settings = ToolLauncher.CreateStartSettings(
                command,
                invocation.ForwardedArguments,
                invocation.AllowRollForward,
                host.WorkingDirectory,
                muxer);

            if (log is not null)
                log.WriteLine($"Starting {settings.FileName} {string.Join(' ', settings.Arguments)}");

            return host.ProcessRunner.Run(settings);
        }
        catch (Exception ex)
        {
            host.Error.WriteLine(ex.Message);
            if (IsDetailed(invocation.Verbosity))
                host.Error.WriteLine(ex);
            return 1;
        }
    }

    static HttpClient CreateHttpClient(NdxHost host, IReadOnlyList<SourceCredential> credentials)
    {
        var handler = host.HttpHandler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };

        if (credentials.Count > 0)
            handler = new SourceCredentialsHandler(credentials) { InnerHandler = handler };

        return new HttpClient(handler, disposeHandler: host.HttpHandler is null);
    }

    static bool IsDetailed(string? verbosity)
        => verbosity?.ToLowerInvariant() is "detailed" or "diagnostic" or "d" or "diag";
}
