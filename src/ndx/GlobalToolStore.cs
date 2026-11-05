using System.Text.Json;

namespace ndx;

/// <summary>
/// Resolves and invokes a tool from the user's global .NET tool installation.
/// </summary>
public sealed class GlobalToolStore
{
    readonly PackageFeed feed;
    readonly string toolsDirectory;
    readonly IProcessRunner processRunner;
    readonly IProcessOutputRunner outputRunner;
    readonly TextWriter? log;
    readonly string? muxerPath;
    readonly string hostRid;
    readonly string workingDirectory;

    public GlobalToolStore(
        PackageFeed feed,
        string toolsDirectory,
        IProcessRunner processRunner,
        TextWriter? log = null,
        string? muxerPath = null,
        string? hostRid = null,
        string? workingDirectory = null)
    {
        this.feed = feed;
        this.toolsDirectory = toolsDirectory;
        this.processRunner = processRunner;
        outputRunner = processRunner as IProcessOutputRunner ?? new ProcessRunner();
        this.log = log;
        this.muxerPath = muxerPath ?? DotnetMuxer.Resolve();
        this.hostRid = hostRid ?? System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        this.workingDirectory = workingDirectory ?? Directory.GetCurrentDirectory();
    }

    public static string DefaultToolsDirectory()
    {
        var cliHome = Environment.GetEnvironmentVariable("DOTNET_CLI_HOME");
        if (string.IsNullOrEmpty(cliHome))
            cliHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");

        return Path.Combine(cliHome, "tools");
    }

    public async Task<ToolCommand> GetAsync(
        Invocation invocation,
        IReadOnlyList<string> sources,
        CancellationToken cancellationToken = default)
        => await GetAsync(invocation, sources, selectedVersion: null, cancellationToken).ConfigureAwait(false);

    internal async Task<ToolCommand> GetAsync(
        Invocation invocation,
        IReadOnlyList<string> sources,
        PackageVersion? selectedVersion,
        CancellationToken cancellationToken)
    {
        var packageId = invocation.PackageId!;
        var installed = ListInstalled(packageId);
        var version = selectedVersion ?? await GetLatestVersionAsync(invocation, sources, cancellationToken)
            .ConfigureAwait(false);

        if (installed is null || installed.Version != version)
            InstallOrUpdate(invocation, version, installed is not null);
        else
            log?.WriteLine($"Using globally installed {installed.PackageId} {installed.Version}");

        var globalStore = new ToolPackageStore(
            feed,
            Path.Combine(toolsDirectory, ".store"),
            log,
            muxerPath,
            hostRid);
        return globalStore.GetGlobalCommand(packageId, version);
    }

    public async Task<PackageVersion> GetLatestVersionAsync(
        Invocation invocation,
        IReadOnlyList<string> sources,
        CancellationToken cancellationToken = default)
    {
        var range = VersionRange.FromInvocation(invocation);
        if (range.IsExact && range.Min is { } exact)
            return exact;

        var candidates = await feed.ListAsync(sources, invocation.PackageId!, range, cancellationToken)
            .ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"Package '{invocation.PackageId}' was not found on the configured sources.");
        }

        return candidates.MaxBy(candidate => candidate.Version)!.Version;
    }

    InstalledTool? ListInstalled(string packageId)
    {
        var result = outputRunner.RunWithOutput(CreateSettings(
            ["tool", "list", packageId, "--global", "--format", "json"]));
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(result.StandardOutput);
        }
        catch (JsonException ex) when (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not list global .NET tools. {result.StandardError}".Trim(), ex);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("The .NET SDK returned an invalid global tool list.");

            if (result.ExitCode != 0 && data.GetArrayLength() > 0)
                throw new InvalidOperationException(
                    $"Could not list global .NET tools. {result.StandardError}".Trim());

            foreach (var item in data.EnumerateArray())
            {
                var id = item.GetProperty("packageId").GetString();
                var value = item.GetProperty("version").GetString();
                if (id is not null && id.Equals(packageId, StringComparison.OrdinalIgnoreCase) &&
                    PackageVersion.TryParse(value, out var version))
                {
                    return new InstalledTool(id, version);
                }
            }
        }

        return null;
    }

    void InstallOrUpdate(Invocation invocation, PackageVersion version, bool installed)
    {
        var arguments = new List<string>
        {
            "tool",
            installed ? "update" : "install",
            "--global",
            invocation.PackageId!,
            "--version",
            version.ToString(),
        };
        if (installed)
            arguments.Add("--allow-downgrade");
        if (invocation.Prerelease)
            arguments.Add("--prerelease");
        if (invocation.Sources.Count > 0)
        {
            foreach (var source in invocation.Sources)
            {
                arguments.Add("--source");
                arguments.Add(source);
            }
        }
        foreach (var source in invocation.AddSources)
        {
            arguments.Add("--add-source");
            arguments.Add(source);
        }
        if (invocation.ConfigFile is { } configFile)
        {
            arguments.Add("--configfile");
            arguments.Add(configFile);
        }
        if (invocation.DisableParallel)
            arguments.Add("--disable-parallel");
        if (invocation.IgnoreFailedSources)
            arguments.Add("--ignore-failed-sources");
        if (invocation.NoHttpCache)
            arguments.Add("--no-cache");
        if (invocation.Interactive)
            arguments.Add("--interactive");
        if (invocation.Verbosity is { } verbosity)
        {
            arguments.Add("--verbosity");
            arguments.Add(verbosity);
        }

        var result = processRunner.Run(CreateSettings(arguments));
        if (result != 0)
            throw new InvalidOperationException(
                $"dotnet tool {(installed ? "update" : "install")} failed for {invocation.PackageId} {version} (exit code {result}).");
    }

    ProcessStartSettings CreateSettings(IReadOnlyList<string> arguments) => new()
    {
        FileName = muxerPath ?? DotnetMuxer.FileName,
        Arguments = arguments,
        UseShellExecute = false,
        RedirectStandardInput = false,
        RedirectStandardOutput = false,
        RedirectStandardError = false,
        WorkingDirectory = workingDirectory,
    };

    sealed record InstalledTool(string PackageId, PackageVersion Version);
}
