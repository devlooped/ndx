using System.Diagnostics;
using ndx;

namespace Tests;

public class ToolInvokeTests : IClassFixture<HelloToolFeed>
{
    readonly HelloToolFeed feed;

    public ToolInvokeTests(HelloToolFeed feed) => this.feed = feed;

    [Fact]
    public async Task Version_alone_prints_ndx_version_and_does_not_launch()
    {
        var runner = new RecordingProcessRunner();
        var host = new NdxHost
        {
            WorkingDirectory = feed.Root,
            StoreDirectory = NewStore(),
            ProcessRunner = runner,
            Out = new StringWriter(),
            Error = new StringWriter(),
            CurrentVersion = "0.2.0",
        };

        var code = await App.RunAsync(["--version"], host);

        Assert.Equal(0, code);
        Assert.Equal(0, runner.Calls);
        Assert.Equal("ndx 0.2.0" + Environment.NewLine, host.Out.ToString());
        Assert.Equal("", host.Error.ToString());
    }

    [Fact]
    public async Task Version_alone_includes_the_short_sha_from_informational_version()
    {
        var runner = new RecordingProcessRunner();
        var host = new NdxHost
        {
            WorkingDirectory = feed.Root,
            StoreDirectory = NewStore(),
            ProcessRunner = runner,
            Out = new StringWriter(),
            Error = new StringWriter(),
            CurrentVersion = "0.2.0+abcdef1234567890",
        };

        var code = await App.RunAsync(["--version"], host);

        Assert.Equal(0, code);
        Assert.Equal(0, runner.Calls);
        Assert.Equal("ndx 0.2.0 (abcdef123)" + Environment.NewLine, host.Out.ToString());
    }

    [Fact]
    public async Task Missing_package_operand_does_not_launch_a_child()
    {
        var runner = new RecordingProcessRunner();
        var host = NewHost(runner);

        var code = await App.RunAsync(["--yes", "--source", feed.FeedDirectory], host);

        Assert.Equal(1, code);
        Assert.Equal(0, runner.Calls);
        Assert.Contains("PACKAGE_NAME", host.Error.ToString());
    }

    [Fact]
    public async Task First_run_downloads_and_second_run_uses_cache()
    {
        var store = NewStore();
        var host = NewHost(new ProcessRunner(), store);

        Assert.False(Cached(store, HelloToolFeed.PackageId));

        var first = await App.RunAsync(InvokeArgs(HelloToolFeed.PackageId), host);
        Assert.Equal(0, first);
        Assert.True(Cached(store, HelloToolFeed.PackageId));
        var markerTime = File.GetLastWriteTimeUtc(Marker(store, HelloToolFeed.PackageId));

        var second = await App.RunAsync(InvokeArgs(HelloToolFeed.PackageId), host);
        Assert.Equal(0, second);
        Assert.True(Cached(store, HelloToolFeed.PackageId));
        Assert.Equal(markerTime, File.GetLastWriteTimeUtc(Marker(store, HelloToolFeed.PackageId)));
    }

    [Fact]
    public async Task Install_switch_runs_matching_global_tool_without_reinstalling()
    {
        var toolsDirectory = NewStore();
        var entryPoint = CreateGlobalTool(toolsDirectory, HelloToolFeed.PackageId, HelloToolFeed.PackageVersion);
        var runner = new RecordingProcessRunner
        {
            OutputResult = InstalledToolList(HelloToolFeed.PackageId, HelloToolFeed.PackageVersion),
        };
        var host = NewHost(runner, globalTools: toolsDirectory);

        var code = await App.RunAsync(["-i", HelloToolFeed.PackageId, "--source", feed.FeedDirectory], host);

        Assert.Equal(0, code);
        Assert.Equal(1, runner.RunWithOutputCalls);
        Assert.Equal(1, runner.StartCalls);
        Assert.Equal(entryPoint, runner.Last!.FileName);
        Assert.Equal(
            ["tool", "list", HelloToolFeed.PackageId, "--global", "--format", "json"],
            runner.Starts[0].Arguments);
    }

    [Fact]
    public async Task Install_switch_installs_missing_global_tool()
    {
        var toolsDirectory = NewStore();
        var entryPoint = CreateGlobalTool(toolsDirectory, HelloToolFeed.PackageId, HelloToolFeed.PackageVersion);
        var runner = new RecordingProcessRunner
        {
            OutputResult = new(1, """{"version":1,"data":[]}""", "Tool not found."),
        };
        var host = NewHost(runner, globalTools: toolsDirectory);

        var code = await App.RunAsync(
            ["-i", HelloToolFeed.PackageId + "@" + HelloToolFeed.PackageVersion, "--source", feed.FeedDirectory],
            host);

        Assert.Equal(0, code);
        Assert.Equal(2, runner.RunCalls);
        Assert.Equal(
            [
                "tool", "install", "--global", HelloToolFeed.PackageId, "--version",
                HelloToolFeed.PackageVersion, "--source", feed.FeedDirectory,
            ],
            runner.Starts[1].Arguments);
        Assert.Equal(entryPoint, runner.Last!.FileName);
    }

    [Fact]
    public async Task Install_switch_downgrades_global_tool_and_runs_installed_entry_point()
    {
        var toolsDirectory = NewStore();
        var entryPoint = CreateGlobalTool(toolsDirectory, HelloToolFeed.PackageId, HelloToolFeed.PackageVersion);
        var runner = new RecordingProcessRunner
        {
            OutputResult = InstalledToolList(HelloToolFeed.PackageId, "2.0.0"),
        };
        var host = NewHost(runner, globalTools: toolsDirectory);

        var code = await App.RunAsync(
            ["--install", HelloToolFeed.PackageId + "@" + HelloToolFeed.PackageVersion, "--source", feed.FeedDirectory],
            host);

        Assert.Equal(0, code);
        Assert.Equal(2, runner.RunCalls);
        Assert.Equal(
            [
                "tool", "update", "--global", HelloToolFeed.PackageId, "--version",
                HelloToolFeed.PackageVersion, "--allow-downgrade", "--source", feed.FeedDirectory,
            ],
            runner.Starts[1].Arguments);
        Assert.Equal(entryPoint, runner.Last!.FileName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task Relays_child_exit_code(int expected)
    {
        var host = NewHost(new ProcessRunner());
        var code = await App.RunAsync(InvokeArgs(HelloToolFeed.PackageId, expected.ToString()), host);
        Assert.Equal(expected, code);
    }

    [Fact]
    public async Task Non_aot_framework_dependent_fixture_runs_on_the_same_path()
    {
        var host = NewHost(new ProcessRunner());
        var code = await App.RunAsync(InvokeArgs(HelloToolFeed.PackageId, "0", "from-fdd"), host);
        Assert.Equal(0, code);
        Assert.True(Cached(host.StoreDirectory, HelloToolFeed.PackageId));
    }

    [Fact]
    public async Task Executable_runner_fixture_runs_the_same_way()
    {
        var host = NewHost(new ProcessRunner());
        var code = await App.RunAsync(InvokeArgs(HelloToolFeed.ExePackageId, "0", "from-exe"), host);
        Assert.Equal(0, code);
        Assert.True(Cached(host.StoreDirectory, HelloToolFeed.ExePackageId));
    }

    [Fact]
    public void Real_entry_point_writes_fixture_stdout_on_ndx_stdout()
    {
        var store = NewStore();
        var result = LaunchNdx(
            store,
            [HelloToolFeed.PackageId + "@" + HelloToolFeed.PackageVersion, "--yes", "--source", feed.FeedDirectory, "--", "0", "via-entry"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(HelloToolFeed.Phrase, result.Stdout);
        Assert.Contains("arg:0", result.Stdout);
        Assert.Contains("arg:via-entry", result.Stdout);
    }

    [Fact]
    public void Real_entry_point_relays_nonzero_exit_and_stdout()
    {
        var store = NewStore();
        var result = LaunchNdx(
            store,
            [HelloToolFeed.PackageId + "@" + HelloToolFeed.PackageVersion, "--yes", "--source", feed.FeedDirectory, "--", "11"]);

        Assert.Equal(11, result.ExitCode);
        Assert.Contains(HelloToolFeed.Phrase, result.Stdout);
    }

    [Fact]
    public async Task Multi_rid_store_get_uses_rid_package_entry_point()
    {
        var storeDir = NewStore();
        var command = await GetShippedAsync(storeDir, HelloToolFeed.RidWrapperId);

        var ridDir = Path.GetFullPath(new ToolPackageStore(NullFeed(), storeDir)
            .GetPackageDirectory(HelloToolFeed.RidImplId, ParsedFixtureVersion()));
        Assert.StartsWith(ridDir, Path.GetFullPath(command.EntryPointPath), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(command.EntryPointPath));
        Assert.Equal("dotnet", command.Runner);
        Assert.Equal("hello-rid", command.Name);

        var host = NewHost(new ProcessRunner(), storeDir);
        var code = await App.RunAsync(InvokeArgs(HelloToolFeed.RidWrapperId, "0", "from-rid"), host);
        Assert.Equal(0, code);
        Assert.True(Cached(storeDir, HelloToolFeed.RidImplId));
    }

    [Fact]
    public async Task Multi_rid_any_fallback_uses_any_package_not_wrapper()
    {
        var storeDir = NewStore();
        var command = await GetShippedAsync(storeDir, HelloToolFeed.AnyWrapperId);

        var anyDir = Path.GetFullPath(new ToolPackageStore(NullFeed(), storeDir)
            .GetPackageDirectory(HelloToolFeed.AnyImplId, ParsedFixtureVersion()));
        var wrapperDir = Path.GetFullPath(new ToolPackageStore(NullFeed(), storeDir)
            .GetPackageDirectory(HelloToolFeed.AnyWrapperId, ParsedFixtureVersion()));
        Assert.StartsWith(anyDir, Path.GetFullPath(command.EntryPointPath), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            Path.DirectorySeparatorChar + HelloToolFeed.AnyWrapperId + Path.DirectorySeparatorChar,
            command.EntryPointPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(wrapperDir, "tools", "any", "any", "DotnetToolSettings.xml")));

        var result = LaunchNdx(
            storeDir,
            [HelloToolFeed.AnyWrapperId + "@" + HelloToolFeed.PackageVersion, "--yes", "--source", feed.FeedDirectory, "--", "0", "from-any"]);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(HelloToolFeed.AnyPhrase, result.Stdout);
        Assert.DoesNotContain("declares RID-specific packages", result.Stdout);
        Assert.DoesNotContain("declares RID-specific packages", result.Stderr);
        Assert.DoesNotContain("not resolved by this runner", result.Stdout + result.Stderr);
    }

    [Fact]
    public async Task Multi_rid_no_match_names_host_and_declared_rids()
    {
        var storeDir = NewStore();
        using var http = new HttpClient();
        var store = new ToolPackageStore(new PackageFeed(http, ignoreFailedSources: false), storeDir);
        var invocation = ArgParser.Parse(
            HelloToolFeed.NoMatchWrapperId + "@" + HelloToolFeed.PackageVersion,
            "--yes",
            "--source",
            feed.FeedDirectory);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetAsync(invocation, [feed.FeedDirectory]));

        Assert.Contains(HelloToolFeed.HostRid, ex.Message);
        Assert.Contains(HelloToolFeed.UnusedRid, ex.Message);
        Assert.Contains("Declared RIDs", ex.Message);
        Assert.DoesNotContain("not resolved by this runner", ex.Message);
    }

    [Fact]
    public void Multi_rid_real_entry_prints_rid_phrase_and_caches()
    {
        var store = NewStore();
        var args = new[]
        {
            HelloToolFeed.RidWrapperId + "@" + HelloToolFeed.PackageVersion,
            "--yes",
            "--source",
            feed.FeedDirectory,
            "--",
            "0",
            "via-multirid",
        };

        var first = LaunchNdx(store, args);
        Assert.Equal(0, first.ExitCode);
        Assert.Contains(HelloToolFeed.Phrase, first.Stdout);
        Assert.DoesNotContain("declares RID-specific packages", first.Stdout + first.Stderr);
        Assert.True(Cached(store, HelloToolFeed.RidImplId));
        var markerTime = File.GetLastWriteTimeUtc(Marker(store, HelloToolFeed.RidImplId));

        var second = LaunchNdx(store, args);
        Assert.Equal(0, second.ExitCode);
        Assert.Contains(HelloToolFeed.Phrase, second.Stdout);
        Assert.Equal(markerTime, File.GetLastWriteTimeUtc(Marker(store, HelloToolFeed.RidImplId)));
    }

    [Theory]
    [InlineData("hello-tool@1.0.0")]
    [InlineData("hello-tool", "--version", "1.0.0")]
    public async Task Exact_cached_version_skips_the_feed(params string[] identity)
    {
        var storeDir = NewStore();
        await GetShippedAsync(storeDir, HelloToolFeed.PackageId);

        var command = await GetWithThrowingFeedAsync(storeDir, [.. identity, "--yes", "--source", HttpFeed]);
        Assert.True(File.Exists(command.EntryPointPath), command.EntryPointPath);
    }

    [Fact]
    public async Task Exact_cached_rid_package_skips_the_feed()
    {
        var storeDir = NewStore();
        await GetShippedAsync(storeDir, HelloToolFeed.RidWrapperId);

        var command = await GetWithThrowingFeedAsync(
            storeDir,
            [HelloToolFeed.RidWrapperId + "@" + HelloToolFeed.PackageVersion, "--yes", "--source", HttpFeed]);
        Assert.True(File.Exists(command.EntryPointPath), command.EntryPointPath);
        Assert.Contains(
            HelloToolFeed.RidImplId,
            command.EntryPointPath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("hello-tool")]
    [InlineData("hello-tool@*")]
    public async Task Latest_or_star_queries_the_feed_even_when_a_version_is_cached(string identity)
    {
        var storeDir = NewStore();
        await GetShippedAsync(storeDir, HelloToolFeed.PackageId);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetWithThrowingFeedAsync(storeDir, [identity, "--yes", "--source", HttpFeed]));
        Assert.Contains("Unexpected HTTP", error.Message);
    }

    [Fact]
    public async Task Exact_version_that_is_not_installed_queries_the_feed()
    {
        var storeDir = NewStore();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => GetWithThrowingFeedAsync(
                storeDir,
                [HelloToolFeed.PackageId + "@" + HelloToolFeed.PackageVersion, "--yes", "--source", HttpFeed]));
        Assert.Contains("Unexpected HTTP", error.Message);
    }

    NdxHost NewHost(IProcessRunner runner, string? store = null, string? globalTools = null)
    {
        store ??= NewStore();
        return new NdxHost
        {
            WorkingDirectory = feed.Root,
            StoreDirectory = store,
            GlobalToolsDirectory = globalTools,
            ProcessRunner = runner,
            Out = new StringWriter(),
            Error = new StringWriter(),
        };
    }

    static ProcessResult InstalledToolList(string packageId, string version)
        => new(
            0,
            $$"""{"version":1,"data":[{"packageId":"{{packageId}}","version":"{{version}}","commands":["tool"]}]}""",
            "");

    static string CreateGlobalTool(string toolsDirectory, string packageId, string version)
    {
        var packageVersion = PackageVersion.TryParse(version, out var parsed)
            ? parsed
            : throw new InvalidOperationException("bad fixture version");
        var commandDirectory = Path.Combine(
            toolsDirectory,
            ".store",
            packageId.ToLowerInvariant(),
            packageVersion.ToString().ToLowerInvariant(),
            packageId.ToLowerInvariant(),
            packageVersion.ToString().ToLowerInvariant(),
            "tools",
            "net10.0",
            "any");
        Directory.CreateDirectory(commandDirectory);
        var executable = OperatingSystem.IsWindows() ? "tool.exe" : "tool";
        var entryPoint = Path.Combine(commandDirectory, executable);
        File.WriteAllText(entryPoint, "");
        File.WriteAllText(
            Path.Combine(commandDirectory, "DotnetToolSettings.xml"),
            $"""
            <DotNetCliTool Version="1">
              <Commands>
                <Command Name="tool" EntryPoint="{executable}" Runner="executable" />
              </Commands>
            </DotNetCliTool>
            """);
        return entryPoint;
    }

    string[] InvokeArgs(string packageId, params string[] forwarded)
    {
        var args = new List<string>
        {
            $"{packageId}@{HelloToolFeed.PackageVersion}",
            "--yes",
            "--source",
            feed.FeedDirectory,
        };
        if (forwarded.Length > 0)
        {
            args.Add("--");
            args.AddRange(forwarded);
        }

        return [.. args];
    }

    const string HttpFeed = "https://example.invalid/index.json";

    async Task<ToolCommand> GetShippedAsync(string storeDir, string packageId)
    {
        using var http = new HttpClient();
        var store = new ToolPackageStore(new PackageFeed(http, ignoreFailedSources: false), storeDir);
        var invocation = ArgParser.Parse(
            packageId + "@" + HelloToolFeed.PackageVersion,
            "--yes",
            "--source",
            feed.FeedDirectory);
        return await store.GetAsync(invocation, [feed.FeedDirectory]);
    }

    static async Task<ToolCommand> GetWithThrowingFeedAsync(string storeDir, string[] args)
    {
        using var http = new HttpClient(new ThrowingHandler());
        var store = new ToolPackageStore(new PackageFeed(http, ignoreFailedSources: false), storeDir);
        var invocation = ArgParser.Parse(args);
        Assert.True(invocation.Success, invocation.Error);
        return await store.GetAsync(invocation, [HttpFeed]);
    }

    static PackageFeed NullFeed() => new(new HttpClient(), ignoreFailedSources: false);

    sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException($"Unexpected HTTP {request.Method} {request.RequestUri}");
    }

    static PackageVersion ParsedFixtureVersion()
        => PackageVersion.TryParse(HelloToolFeed.PackageVersion, out var parsed)
            ? parsed
            : throw new InvalidOperationException("bad fixture version");

    static string NewStore()
    {
        var store = Path.Combine(Path.GetTempPath(), "ndx-test-store", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(store);
        return store;
    }

    static bool Cached(string store, string packageId)
    {
        var version = PackageVersion.TryParse(HelloToolFeed.PackageVersion, out var parsed)
            ? parsed
            : throw new InvalidOperationException("bad fixture version");
        return ToolPackageStore.IsCached(Path.Combine(store, packageId.ToLowerInvariant(), version.ToString().ToLowerInvariant()));
    }

    static string Marker(string store, string packageId)
    {
        var version = HelloToolFeed.PackageVersion.ToLowerInvariant();
        return Path.Combine(store, packageId.ToLowerInvariant(), version, ToolPackageStore.ReadyMarker);
    }

    static (int ExitCode, string Stdout, string Stderr) LaunchNdx(string store, string[] args)
    {
        var ndxDll = Path.Combine(AppContext.BaseDirectory, "ndx.dll");
        Assert.True(File.Exists(ndxDll), $"Expected shipped ndx.dll at {ndxDll}");

        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(ndxDll);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        start.Environment["NDX_STORE"] = store;
        return ProcessCapture.Run(start);
    }
}
