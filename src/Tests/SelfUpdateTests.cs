using System.Net;
using System.Text;
using ndx;

namespace Tests;

public class SelfUpdateTests
{
    const string Repo = "devlooped/ndx";
    const string Rid = "win-x64";

    [Theory]
    [InlineData("0.2.0", "ndx 0.2.0")]
    [InlineData("v0.2.0", "ndx 0.2.0")]
    [InlineData("0.2.0+abcdef123", "ndx 0.2.0 (abcdef123)")]
    [InlineData("0.2.0+abcdef1234567890", "ndx 0.2.0 (abcdef123)")]
    [InlineData("v0.2.0+abcdef123.dirty", "ndx 0.2.0 (abcdef123)")]
    public void FormatVersion_renders_ndx_version_and_short_sha(string informational, string expected)
        => Assert.Equal(expected, SelfUpdate.FormatVersion(informational));

    [Fact]
    public void HasMuslLoader_recognizes_alpine_release_or_the_musl_loader()
    {
        using var dir = new TempDir();
        Assert.False(SelfUpdate.HasMuslLoader(dir.Root));

        Directory.CreateDirectory(Path.Combine(dir.Root, "etc"));
        File.WriteAllText(Path.Combine(dir.Root, "etc", "alpine-release"), "3.22.0\n");
        Assert.True(SelfUpdate.HasMuslLoader(dir.Root));
        File.Delete(Path.Combine(dir.Root, "etc", "alpine-release"));
        Assert.False(SelfUpdate.HasMuslLoader(dir.Root));

        var lib = Path.Combine(dir.Root, "lib");
        Directory.CreateDirectory(lib);
        File.WriteAllBytes(Path.Combine(lib, "ld-musl-x86_64.so.1"), [0]);
        Assert.True(SelfUpdate.HasMuslLoader(dir.Root));
    }

    [Fact]
    public void FormatVersion_reads_assembly_informational_version()
    {
        var formatted = SelfUpdate.FormatVersion();
        Assert.StartsWith("ndx ", formatted);
        Assert.Contains(SelfUpdate.ReadCurrentVersion(), formatted);
    }

    [Fact]
    public async Task Update_to_latest_replaces_the_binary_and_prints_the_target_version()
    {
        using var dir = new TempDir();
        var current = Path.Combine(dir.Prefix, "ndx.exe");
        File.WriteAllBytes(current, "old-binary"u8.ToArray());

        using var handler = Feed(dir, latest: "0.2.0", payload: "new-binary"u8.ToArray());
        var host = NewHost(dir, current, "0.1.0", handler);

        var code = await App.RunAsync(["--update"], host);

        Assert.Equal(0, code);
        Assert.Equal("new-binary"u8.ToArray(), File.ReadAllBytes(current));
        Assert.Contains("Updating to 0.2.0", host.Out.ToString());
        Assert.Contains("updated", host.Out.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(current, host.Out.ToString());
    }

    [Fact]
    public async Task Update_skips_download_when_already_on_latest()
    {
        using var dir = new TempDir();
        var current = Path.Combine(dir.Prefix, "ndx.exe");
        var payload = "same-binary"u8.ToArray();
        File.WriteAllBytes(current, payload);

        using var handler = Feed(dir, latest: "0.2.0", payload: "unused"u8.ToArray());
        var host = NewHost(dir, current, "0.2.0", handler);

        var code = await App.RunAsync(["--update"], host);

        Assert.Equal(0, code);
        Assert.Equal(payload, File.ReadAllBytes(current));
        Assert.Contains("already 0.2.0", host.Out.ToString());
        Assert.Equal(1, handler.Hits.Count(u => u.Contains("/releases/latest", StringComparison.Ordinal)));
        Assert.DoesNotContain(handler.Hits, u => u.Contains("/releases/download/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_to_ci_downloads_the_rolling_prerelease_tag()
    {
        using var dir = new TempDir();
        var current = Path.Combine(dir.Prefix, "ndx.exe");
        File.WriteAllBytes(current, "old-binary"u8.ToArray());

        using var handler = new MapHandler();
        AddRelease(handler, dir, SelfUpdate.CiChannel, "ci-binary"u8.ToArray(), tag: SelfUpdate.CiChannel);
        var host = NewHost(dir, current, "0.1.0", handler);

        var code = await App.RunAsync(["--update", "ci"], host);

        Assert.Equal(0, code);
        Assert.Equal("ci-binary"u8.ToArray(), File.ReadAllBytes(current));
        Assert.Contains("Updating to ci", host.Out.ToString());
        Assert.Contains(SelfUpdate.AssetUrl(Repo, SelfUpdate.CiChannel, SelfUpdate.ArchiveFileName(Rid, SelfUpdate.CiChannel)), handler.Hits);
        Assert.DoesNotContain(handler.Hits, u => u.Contains("/releases/latest", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Hits, u => u.Contains("/vci/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_to_ci_redownloads_even_when_already_labeled_ci()
    {
        using var dir = new TempDir();
        var current = Path.Combine(dir.Prefix, "ndx.exe");
        File.WriteAllBytes(current, "stale-ci"u8.ToArray());

        using var handler = new MapHandler();
        AddRelease(handler, dir, SelfUpdate.CiChannel, "fresh-ci"u8.ToArray(), tag: SelfUpdate.CiChannel);
        var host = NewHost(dir, current, SelfUpdate.CiChannel, handler);

        var code = await App.RunAsync(["--update", "ci"], host);

        Assert.Equal(0, code);
        Assert.Equal("fresh-ci"u8.ToArray(), File.ReadAllBytes(current));
        Assert.Contains(handler.Hits, u => u.Contains("/releases/download/ci/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_to_an_older_version_is_allowed()
    {
        using var dir = new TempDir();
        var current = Path.Combine(dir.Prefix, "ndx.exe");
        File.WriteAllBytes(current, "newer-binary"u8.ToArray());

        using var handler = Feed(dir, latest: "0.2.0", payload: "older-binary"u8.ToArray(), extraVersion: "0.1.0");
        var host = NewHost(dir, current, "0.2.0", handler);

        var code = await App.RunAsync(["--update", "0.1.0"], host);

        Assert.Equal(0, code);
        Assert.Equal("older-binary"u8.ToArray(), File.ReadAllBytes(current));
        Assert.Contains("Updating to 0.1.0", host.Out.ToString());
        Assert.DoesNotContain(handler.Hits, u => u.Contains("/releases/latest", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_to_a_missing_version_fails()
    {
        using var dir = new TempDir();
        var current = Path.Combine(dir.Prefix, "ndx.exe");
        File.WriteAllBytes(current, "old-binary"u8.ToArray());

        using var handler = Feed(dir, latest: "0.2.0", payload: "new-binary"u8.ToArray());
        var host = NewHost(dir, current, "0.1.0", handler);

        var code = await App.RunAsync(["--update", "9.9.9"], host);

        Assert.Equal(1, code);
        Assert.Equal("old-binary"u8.ToArray(), File.ReadAllBytes(current));
        Assert.Contains("9.9.9", host.Error.ToString());
        Assert.Contains("404", host.Error.ToString());
    }

    [Fact]
    public async Task Update_extracts_a_unix_targz_archive()
    {
        using var dir = new TempDir();
        var current = Path.Combine(dir.Prefix, "ndx");
        File.WriteAllBytes(current, "old-unix"u8.ToArray());

        const string unixRid = "linux-x64";
        using var handler = new MapHandler();
        var packed = NativePacker.Pack(
            RidNupkg.Write(Path.Combine(dir.Root, "nupkg-unix"), unixRid, "new-unix"u8.ToArray()),
            unixRid,
            Path.Combine(dir.Root, "out-unix"),
            "0.3.0");
        var name = Path.GetFileName(packed.ArchivePath);
        handler.Map[SelfUpdate.AssetUrl(Repo, "v0.3.0", name)] =
            (HttpStatusCode.OK, File.ReadAllBytes(packed.ArchivePath), "application/octet-stream");
        handler.Map[SelfUpdate.AssetUrl(Repo, "v0.3.0", name) + ".sha256"] =
            (HttpStatusCode.OK, File.ReadAllBytes(packed.Sha256Path), "text/plain");

        var host = new NdxHost
        {
            WorkingDirectory = dir.Root,
            StoreDirectory = dir.Store,
            ProcessRunner = new RecordingProcessRunner(),
            Out = new StringWriter(),
            Error = new StringWriter(),
            HttpHandler = handler,
            ExecutablePath = current,
            CurrentVersion = "0.1.0",
            UpdateRepository = Repo,
            RuntimeIdentifier = unixRid,
        };
        var code = await App.RunAsync(["--update", "0.3.0"], host);

        Assert.Equal(0, code);
        Assert.Equal("new-unix"u8.ToArray(), File.ReadAllBytes(current));
        Assert.Contains("Updating to 0.3.0", host.Out.ToString());
    }

    [Fact]
    public async Task Update_does_not_launch_a_child()
    {
        using var dir = new TempDir();
        var current = Path.Combine(dir.Prefix, "ndx.exe");
        File.WriteAllBytes(current, "old-binary"u8.ToArray());

        using var handler = Feed(dir, latest: "0.2.0", payload: "new-binary"u8.ToArray());
        var runner = new RecordingProcessRunner();
        var host = NewHost(dir, current, "0.1.0", handler, runner);

        var code = await App.RunAsync(["--update"], host);

        Assert.Equal(0, code);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task Sha256_mismatch_leaves_the_current_binary()
    {
        using var dir = new TempDir();
        var current = Path.Combine(dir.Prefix, "ndx.exe");
        File.WriteAllBytes(current, "old-binary"u8.ToArray());

        using var handler = Feed(dir, latest: "0.2.0", payload: "new-binary"u8.ToArray());
        var archive = SelfUpdate.ArchiveFileName(Rid, "0.2.0");
        handler.Map[SelfUpdate.AssetUrl(Repo, "v0.2.0", archive) + ".sha256"] =
            (HttpStatusCode.OK, "0"u8.ToArray(), "text/plain");
        var host = NewHost(dir, current, "0.1.0", handler);

        var code = await App.RunAsync(["--update"], host);

        Assert.Equal(1, code);
        Assert.Equal("old-binary"u8.ToArray(), File.ReadAllBytes(current));
        Assert.Contains("SHA256", host.Error.ToString());
    }

    static NdxHost NewHost(TempDir dir, string executable, string currentVersion, MapHandler handler, IProcessRunner? runner = null)
        => new()
        {
            WorkingDirectory = dir.Root,
            StoreDirectory = dir.Store,
            ProcessRunner = runner ?? new RecordingProcessRunner(),
            Out = new StringWriter(),
            Error = new StringWriter(),
            HttpHandler = handler,
            ExecutablePath = executable,
            CurrentVersion = currentVersion,
            UpdateRepository = Repo,
            RuntimeIdentifier = Rid,
        };

    static MapHandler Feed(
        TempDir dir,
        string latest,
        byte[] payload,
        string? extraVersion = null)
    {
        var handler = new MapHandler();
        handler.Map[SelfUpdate.LatestReleaseUrl(Repo)] =
            (HttpStatusCode.OK, Encoding.UTF8.GetBytes($$"""{"tag_name":"v{{latest}}"}"""), "application/json");

        AddRelease(handler, dir, latest, payload);
        if (extraVersion is not null)
            AddRelease(handler, dir, extraVersion, payload);

        return handler;
    }

    static void AddRelease(MapHandler handler, TempDir dir, string version, byte[] payload, string? tag = null)
    {
        var packed = NativePacker.Pack(
            RidNupkg.Write(Path.Combine(dir.Root, "nupkg-" + version), Rid, payload),
            Rid,
            Path.Combine(dir.Root, "out-" + version),
            version);
        var archive = File.ReadAllBytes(packed.ArchivePath);
        var sha = File.ReadAllBytes(packed.Sha256Path);
        var name = Path.GetFileName(packed.ArchivePath);
        var releaseTag = tag ?? SelfUpdate.ReleaseTag(version);
        handler.Map[SelfUpdate.AssetUrl(Repo, releaseTag, name)] = (HttpStatusCode.OK, archive, "application/octet-stream");
        handler.Map[SelfUpdate.AssetUrl(Repo, releaseTag, name) + ".sha256"] = (HttpStatusCode.OK, sha, "text/plain");
    }

    sealed class MapHandler : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, byte[] Body, string Media)> Map { get; } = new(StringComparer.Ordinal);
        public List<string> Hits { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? "";
            Hits.Add(url);
            if (!Map.TryGetValue(url, out var mapped))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("not found"),
                    RequestMessage = request,
                });
            }

            var response = new HttpResponseMessage(mapped.Status)
            {
                Content = new ByteArrayContent(mapped.Body),
                RequestMessage = request,
            };
            response.Content.Headers.ContentType = new(mapped.Media);
            return Task.FromResult(response);
        }
    }

    sealed class TempDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ndx-update-tests", Guid.NewGuid().ToString("n"));
        public string Prefix => Path.Combine(Root, "prefix");
        public string Store => Path.Combine(Root, "store");

        public TempDir()
        {
            Directory.CreateDirectory(Prefix);
            Directory.CreateDirectory(Store);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }
}
