using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using ndx;

namespace Tests;

public class PackageFeedTests
{
    const string Source = "https://feed.test/index.json";
    const string Flat = "https://feed.test/flat/";
    const string PackageId = "hello-tool";
    const string RidWrapperId = "hello-rid";
    const string VersionText = "1.0.0";

    static readonly string HostRid = RuntimeInformation.RuntimeIdentifier;
    static readonly string RidImplId = $"{RidWrapperId}.{HostRid}";
    static readonly PackageVersion Version = ParseVersion();

    [Fact]
    public async Task Service_index_is_fetched_once_per_source()
    {
        using var packages = new TempNupkgs();
        var handler = MapFeed(packages);
        using var http = new HttpClient(handler);
        var packageFeed = new PackageFeed(http, ignoreFailedSources: false);
        var dest1 = Path.Combine(packages.Root, "a.nupkg");
        var dest2 = Path.Combine(packages.Root, "b.nupkg");
        var identity = new PackageIdentity(PackageId, Version, Source);

        Assert.True(await packageFeed.TryDownloadAsync(identity, dest1));
        Assert.True(await packageFeed.TryDownloadAsync(identity, dest2));
        await packageFeed.ListAsync([Source], PackageId, VersionRange.Exact(Version));

        Assert.Equal(1, handler.Hits.Count(url => url == Source));
        Assert.Equal(2, handler.Hits.Count(url => url == NupkgUrl(PackageId)));
        Assert.Equal(1, handler.Hits.Count(url => url == VersionsUrl(PackageId)));
    }

    [Fact]
    public async Task Exact_version_downloads_nupkg_without_listing()
    {
        using var packages = new TempNupkgs();
        var handler = MapFeed(packages);
        var command = await GetExactAsync(handler, PackageId);

        Assert.True(File.Exists(command.EntryPointPath), command.EntryPointPath);
        Assert.Equal(1, handler.Hits.Count(url => url == Source));
        Assert.Contains(NupkgUrl(PackageId), handler.Hits);
        Assert.DoesNotContain(VersionsUrl(PackageId), handler.Hits);
    }

    [Fact]
    public async Task Exact_rid_hop_downloads_without_listing_either_package()
    {
        using var packages = new TempNupkgs();
        var handler = MapFeed(packages);
        handler.Map[NupkgUrl(RidWrapperId)] = File.ReadAllBytes(packages.RidWrapper);
        handler.Map[NupkgUrl(RidImplId)] = File.ReadAllBytes(packages.RidImpl);
        handler.Map[VersionsUrl(RidWrapperId)] = VersionsJson();
        handler.Map[VersionsUrl(RidImplId)] = VersionsJson();

        var command = await GetExactAsync(handler, RidWrapperId);

        Assert.Contains(RidImplId, command.EntryPointPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.Hits.Count(url => url == Source));
        Assert.Contains(NupkgUrl(RidWrapperId), handler.Hits);
        Assert.Contains(NupkgUrl(RidImplId), handler.Hits);
        Assert.DoesNotContain(VersionsUrl(RidWrapperId), handler.Hits);
        Assert.DoesNotContain(VersionsUrl(RidImplId), handler.Hits);
    }

    [Fact]
    public async Task Exact_nupkg_404_falls_back_to_list()
    {
        using var packages = new TempNupkgs();
        var handler = MapFeed(packages);
        var nupkg = NupkgUrl(PackageId);
        var remaining404 = 1;
        handler.Override[nupkg] = () =>
        {
            if (remaining404-- > 0)
                return (HttpStatusCode.NotFound, "not found"u8.ToArray());
            return (HttpStatusCode.OK, File.ReadAllBytes(packages.Plain));
        };

        var command = await GetExactAsync(handler, PackageId);

        Assert.True(File.Exists(command.EntryPointPath), command.EntryPointPath);
        Assert.Equal(2, handler.Hits.Count(url => url == nupkg));
        Assert.Contains(VersionsUrl(PackageId), handler.Hits);
    }

    [Fact]
    public async Task Exact_nupkg_404_and_empty_list_is_not_found()
    {
        using var packages = new TempNupkgs();
        var handler = MapFeed(packages);
        handler.Map[NupkgUrl(PackageId)] = [];
        handler.Status[NupkgUrl(PackageId)] = HttpStatusCode.NotFound;
        handler.Map[VersionsUrl(PackageId)] = """{"versions":[]}"""u8.ToArray();

        using var http = new HttpClient(handler);
        var store = new ToolPackageStore(new PackageFeed(http, ignoreFailedSources: false), NewStore());
        var invocation = ArgParser.Parse(PackageId + "@" + VersionText, "--yes", "--source", Source);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetAsync(invocation, [Source]));
        Assert.Equal(
            $"Version {VersionText} of package {PackageId} is not found in NuGet feeds {Source}.",
            error.Message);
        Assert.Contains(VersionsUrl(PackageId), handler.Hits);
    }

    [Fact]
    public async Task Exact_nupkg_404_and_missing_index_is_not_found()
    {
        using var packages = new TempNupkgs();
        var handler = MapFeed(packages);
        handler.Map.Remove(VersionsUrl(PackageId));
        handler.Status[NupkgUrl(PackageId)] = HttpStatusCode.NotFound;

        using var http = new HttpClient(handler);
        var store = new ToolPackageStore(new PackageFeed(http, ignoreFailedSources: false), NewStore());
        var invocation = ArgParser.Parse(PackageId + "@" + VersionText, "--yes", "--source", Source);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetAsync(invocation, [Source]));
        Assert.Equal(
            $"Version {VersionText} of package {PackageId} is not found in NuGet feeds {Source}.",
            error.Message);
        Assert.Contains(VersionsUrl(PackageId), handler.Hits);
        Assert.DoesNotContain("404", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_rid_package_reports_dnx_style_not_found()
    {
        using var packages = new TempNupkgs();
        var handler = MapFeed(packages);
        handler.Map[NupkgUrl(RidWrapperId)] = File.ReadAllBytes(packages.RidWrapper);

        using var http = new HttpClient(handler);
        var store = new ToolPackageStore(new PackageFeed(http, ignoreFailedSources: false), NewStore());
        var invocation = ArgParser.Parse(RidWrapperId + "@" + VersionText, "--yes", "--source", Source);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetAsync(invocation, [Source]));
        Assert.Equal(
            $"Version {VersionText} of package {RidImplId} is not found in NuGet feeds {Source}.",
            error.Message);
        Assert.Contains(NupkgUrl(RidImplId), handler.Hits);
        Assert.Contains(VersionsUrl(RidImplId), handler.Hits);
    }

    [Fact]
    public async Task List_treats_missing_package_index_as_empty()
    {
        using var packages = new TempNupkgs();
        var handler = MapFeed(packages);
        using var http = new HttpClient(handler);
        var feed = new PackageFeed(http, ignoreFailedSources: false);

        var listed = await feed.ListAsync([Source], "no-such-package", VersionRange.Any(includePrerelease: false));

        Assert.Empty(listed);
        Assert.Contains(VersionsUrl("no-such-package"), handler.Hits);
    }

    [Fact]
    public async Task Http_download_with_known_size_renders_byte_progress_bar()
    {
        using var packages = new TempNupkgs();
        var body = SizedBody(200);
        var handler = MapFeed(packages);
        handler.Map[NupkgUrl(PackageId)] = body;
        handler.OmitContentLength.Add(NupkgUrl(PackageId));
        MapCatalog(handler, PackageId, body.Length);
        using var http = new HttpClient(handler);
        using var progress = new StringWriter();
        var feed = new PackageFeed(http, ignoreFailedSources: false, progress: progress);
        var dest = Path.Combine(packages.Root, "known.nupkg");

        Assert.True(await feed.TryDownloadAsync(new PackageIdentity(PackageId, Version, Source), dest));

        var ui = progress.ToString();
        SaveEvidence("progress-known.log", ui);
        Assert.Contains("[", ui, StringComparison.Ordinal);
        Assert.Contains("]", ui, StringComparison.Ordinal);
        Assert.Contains("200 B", ui, StringComparison.Ordinal);
        Assert.Contains(" / ", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("downloading", ui, StringComparison.Ordinal);
        Assert.Equal(body, File.ReadAllBytes(dest));
        Assert.Contains(RegistrationUrl(PackageId), handler.Hits);
        Assert.Contains(CatalogUrl(PackageId), handler.Hits);
    }

    [Fact]
    public async Task Http_download_with_unknown_size_renders_spinner_and_downloading()
    {
        using var packages = new TempNupkgs();
        var body = SizedBody(200);
        var handler = MapFeed(packages);
        handler.Map[NupkgUrl(PackageId)] = body;
        handler.OmitContentLength.Add(NupkgUrl(PackageId));
        using var http = new HttpClient(handler);
        using var progress = new StringWriter();
        var feed = new PackageFeed(http, ignoreFailedSources: false, progress: progress);
        var dest = Path.Combine(packages.Root, "unknown.nupkg");

        Assert.True(await feed.TryDownloadAsync(new PackageIdentity(PackageId, Version, Source), dest));

        var ui = progress.ToString();
        SaveEvidence("progress-unknown.log", ui);
        Assert.Contains("downloading", ui, StringComparison.Ordinal);
        Assert.Matches(@"[|/\-\\] downloading", ui);
        Assert.Equal(body, File.ReadAllBytes(dest));
    }

    [Fact]
    public async Task Http_download_with_progress_disabled_writes_no_progress_ui()
    {
        using var packages = new TempNupkgs();
        var body = File.ReadAllBytes(packages.Plain);
        var handler = MapFeed(packages);
        MapCatalog(handler, PackageId, body.Length);
        using var error = new StringWriter();
        using var output = new StringWriter();
        var host = new NdxHost
        {
            HttpHandler = handler,
            Error = error,
            Out = output,
            StoreDirectory = NewStore(),
            ShowProgress = false,
            ProcessRunner = new RecordingProcessRunner { ExitCode = 0 },
            WorkingDirectory = packages.Root,
        };

        var code = await App.RunAsync([PackageId + "@" + VersionText, "--yes", "--source", Source], host);

        var ui = error.ToString();
        SaveEvidence("progress-redirected.log", ui);
        Assert.Equal(0, code);
        Assert.Equal("", ui);
        Assert.DoesNotContain(handler.Hits, url => url.Contains("/reg/", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Hits, url => url.Contains("/catalog/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task App_writes_download_progress_to_host_Progress()
    {
        using var packages = new TempNupkgs();
        var body = File.ReadAllBytes(packages.Plain);
        var handler = MapFeed(packages);
        MapCatalog(handler, PackageId, body.Length);
        using var error = new StringWriter();
        using var progress = new StringWriter();
        using var output = new StringWriter();
        var host = new NdxHost
        {
            HttpHandler = handler,
            Error = error,
            Out = output,
            Progress = progress,
            StoreDirectory = NewStore(),
            ShowProgress = true,
            ProcessRunner = new RecordingProcessRunner { ExitCode = 0 },
            WorkingDirectory = packages.Root,
        };

        var code = await App.RunAsync([PackageId + "@" + VersionText, "--yes", "--source", Source], host);

        Assert.Equal(0, code);
        Assert.Equal("", error.ToString());
        var ui = progress.ToString();
        Assert.Contains("[", ui, StringComparison.Ordinal);
        Assert.Contains(" / ", ui, StringComparison.Ordinal);
        Assert.Contains(" B", ui, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Config_credentials_authenticate_feed_requests_with_source_override()
    {
        using var packages = new TempNupkgs();
        var handler = MapFeed(packages);
        var config = Path.Combine(packages.Root, "auth.config");
        File.WriteAllText(config, $"""
            <configuration>
              <packageSources>
                <add key="Private Feed" value="{Source}" />
              </packageSources>
              <packageSourceCredentials>
                <Private_x0020_Feed>
                  <add key="Username" value="user" />
                  <add key="ClearTextPassword" value="%NDX_TEST_FEED_PAT%" />
                </Private_x0020_Feed>
              </packageSourceCredentials>
            </configuration>
            """);
        Environment.SetEnvironmentVariable("NDX_TEST_FEED_PAT", "s3cret");
        using var error = new StringWriter();
        var host = new NdxHost
        {
            HttpHandler = handler,
            Error = error,
            Out = new StringWriter(),
            StoreDirectory = NewStore(),
            ProcessRunner = new RecordingProcessRunner { ExitCode = 0 },
            WorkingDirectory = packages.Root,
        };

        try
        {
            var code = await App.RunAsync(
                [PackageId + "@" + VersionText, "--yes", "--source", Source, "--configfile", config], host);

            Assert.Equal(0, code);
            Assert.Equal("", error.ToString());
            var expected = "Basic " + Convert.ToBase64String("user:s3cret"u8.ToArray());
            Assert.Equal(expected, handler.Authorizations[Source]);
            Assert.Equal(expected, handler.Authorizations[NupkgUrl(PackageId)]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NDX_TEST_FEED_PAT", null);
        }
    }

    [Fact]
    public async Task Http_download_succeeds_when_catalog_size_lookup_fails()
    {
        using var packages = new TempNupkgs();
        var body = SizedBody(200);
        var handler = MapFeed(packages);
        handler.Map[NupkgUrl(PackageId)] = body;
        MapCatalog(handler, PackageId, body.Length);
        handler.Status[CatalogUrl(PackageId)] = HttpStatusCode.NotFound;
        using var http = new HttpClient(handler);
        using var progress = new StringWriter();
        var feed = new PackageFeed(http, ignoreFailedSources: false, progress: progress);
        var dest = Path.Combine(packages.Root, "catalog-404.nupkg");

        Assert.True(await feed.TryDownloadAsync(new PackageIdentity(PackageId, Version, Source), dest));

        Assert.Equal(body, File.ReadAllBytes(dest));
        var ui = progress.ToString();
        Assert.Contains("[", ui, StringComparison.Ordinal);
        Assert.Contains("200 B", ui, StringComparison.Ordinal);
    }

    static async Task<ToolCommand> GetExactAsync(RecordingHandler handler, string packageId)
    {
        using var http = new HttpClient(handler);
        var store = new ToolPackageStore(new PackageFeed(http, ignoreFailedSources: false), NewStore());
        var invocation = ArgParser.Parse(packageId + "@" + VersionText, "--yes", "--source", Source);
        return await store.GetAsync(invocation, [Source]);
    }

    static RecordingHandler MapFeed(TempNupkgs packages)
    {
        var handler = new RecordingHandler();
        handler.Map[Source] = """
            {"resources":[{"@id":"https://feed.test/flat/","@type":"PackageBaseAddress/3.0.0"}]}
            """u8.ToArray();
        handler.Map[NupkgUrl(PackageId)] = File.ReadAllBytes(packages.Plain);
        handler.Map[VersionsUrl(PackageId)] = VersionsJson();
        return handler;
    }

    static void MapCatalog(RecordingHandler handler, string packageId, long packageSize)
    {
        handler.Map[Source] = """
            {"resources":[
              {"@id":"https://feed.test/flat/","@type":"PackageBaseAddress/3.0.0"},
              {"@id":"https://feed.test/reg/","@type":"RegistrationsBaseUrl/3.6.0"}
            ]}
            """u8.ToArray();
        handler.Map[RegistrationUrl(packageId)] = Encoding.UTF8.GetBytes(
            $$"""{"catalogEntry":"{{CatalogUrl(packageId)}}"}""");
        handler.Map[CatalogUrl(packageId)] = Encoding.UTF8.GetBytes(
            $$"""{"packageSize":{{packageSize}}}""");
    }

    static byte[] SizedBody(int length)
    {
        var body = new byte[length];
        for (var i = 0; i < body.Length; i++)
            body[i] = (byte)i;
        return body;
    }

    static void SaveEvidence(string fileName, string content)
    {
        var dir = Environment.GetEnvironmentVariable("NDX_PROGRESS_EVIDENCE");
        if (string.IsNullOrWhiteSpace(dir))
            return;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
    }

    static byte[] VersionsJson() => """{"versions":["1.0.0"]}"""u8.ToArray();

    static string NupkgUrl(string packageId)
    {
        var id = packageId.ToLowerInvariant();
        return $"{Flat}{id}/{VersionText}/{id}.{VersionText}.nupkg";
    }

    static string VersionsUrl(string packageId)
        => $"{Flat}{packageId.ToLowerInvariant()}/index.json";

    static string RegistrationUrl(string packageId)
        => $"https://feed.test/reg/{packageId.ToLowerInvariant()}/{VersionText}.json";

    static string CatalogUrl(string packageId)
        => $"https://feed.test/catalog/{packageId.ToLowerInvariant()}.{VersionText}.json";

    static PackageVersion ParseVersion()
        => PackageVersion.TryParse(VersionText, out var parsed)
            ? parsed
            : throw new InvalidOperationException("bad version");

    static string NewStore()
    {
        var store = Path.Combine(Path.GetTempPath(), "ndx-feed-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(store);
        return store;
    }

    sealed class TempNupkgs : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ndx-feed-nupkgs", Guid.NewGuid().ToString("n"));
        public string Plain { get; }
        public string RidWrapper { get; }
        public string RidImpl { get; }

        public TempNupkgs()
        {
            Directory.CreateDirectory(Root);
            Plain = WriteTool(PackageId, "hello-tool", ridPackages: null);
            RidWrapper = WriteTool(RidWrapperId, "hello-rid", ridPackages: (HostRid, RidImplId));
            RidImpl = WriteTool(RidImplId, "hello-rid", ridPackages: null);
        }

        string WriteTool(string packageId, string commandName, (string Rid, string Id)? ridPackages)
        {
            var staging = Path.Combine(Root, "stg-" + packageId);
            var tools = Path.Combine(staging, "tools", "net10.0", "any");
            Directory.CreateDirectory(tools);
            File.WriteAllText(Path.Combine(tools, "tool.bin"), "ok");

            var ridXml = ridPackages is { } map
                ? $"""
                      <RuntimeIdentifierPackages>
                        <RuntimeIdentifierPackage RuntimeIdentifier="{map.Rid}" Id="{map.Id}" />
                      </RuntimeIdentifierPackages>
                    """
                : "";
            var runner = ridPackages is null ? "executable" : "";
            var entry = ridPackages is null ? " EntryPoint=\"tool.bin\" Runner=\"executable\"" : "";
            File.WriteAllText(Path.Combine(tools, "DotnetToolSettings.xml"),
                $"""
                <?xml version="1.0" encoding="utf-8"?>
                <DotNetCliTool Version="1">
                  <Commands>
                    <Command Name="{commandName}"{entry} />
                  </Commands>
                {ridXml}
                </DotNetCliTool>
                """);

            File.WriteAllText(Path.Combine(staging, packageId + ".nuspec"),
                $"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata>
                    <id>{packageId}</id>
                    <version>{VersionText}</version>
                    <authors>ndx</authors>
                    <description>{packageId}</description>
                    <packageTypes>
                      <packageType name="{(ridPackages is null && packageId != RidImplId ? "DotnetTool" : packageId == RidWrapperId ? "DotnetTool" : "DotnetToolRidPackage")}" />
                    </packageTypes>
                  </metadata>
                </package>
                """);

            var nupkg = Path.Combine(Root, $"{packageId}.{VersionText}.nupkg");
            ZipFile.CreateFromDirectory(staging, nupkg);
            return nupkg;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }

    sealed class RecordingHandler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Map { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, HttpStatusCode> Status { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Func<(HttpStatusCode Status, byte[] Body)>> Override { get; } = new(StringComparer.Ordinal);
        public HashSet<string> OmitContentLength { get; } = new(StringComparer.Ordinal);
        public List<string> Hits { get; } = [];
        public Dictionary<string, string?> Authorizations { get; } = new(StringComparer.Ordinal);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? "";
            Hits.Add(url);
            Authorizations[url] = request.Headers.Authorization?.ToString();

            if (Override.TryGetValue(url, out var factory))
            {
                var produced = factory();
                return Task.FromResult(Response(request, produced.Status, produced.Body, OmitContentLength.Contains(url)));
            }

            if (!Map.TryGetValue(url, out var body))
                return Task.FromResult(Response(request, HttpStatusCode.NotFound, "not found"u8.ToArray(), omitLength: false));

            var status = Status.TryGetValue(url, out var mapped) ? mapped : HttpStatusCode.OK;
            return Task.FromResult(Response(request, status, body, OmitContentLength.Contains(url)));
        }

        static HttpResponseMessage Response(HttpRequestMessage request, HttpStatusCode status, byte[] body, bool omitLength)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = omitLength ? new UnsizedContent(body) : new ByteArrayContent(body),
                RequestMessage = request,
            };
            var nupkg = request.RequestUri?.AbsolutePath.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) == true;
            response.Content.Headers.ContentType = new(nupkg ? "application/octet-stream" : "application/json");
            return response;
        }
    }

    sealed class UnsizedContent : HttpContent
    {
        readonly byte[] body;

        public UnsizedContent(byte[] body) => this.body = body;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            => stream.WriteAsync(body.AsMemory(), cancellationToken).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
