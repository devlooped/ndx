using System.Net;
using ndx;

namespace Tests;

public class SourceCredentialsTests
{
    [Fact]
    public void Config_credentials_are_matched_to_sources_by_key()
    {
        var config = WriteConfig("""
            <configuration>
              <packageSources>
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                <add key="My Feed" value="https://pkgs.test/feed/index.json" />
                <add key="NoPassword" value="https://other.test/index.json" />
              </packageSources>
              <packageSourceCredentials>
                <My_x0020_Feed>
                  <add key="Username" value="me" />
                  <add key="ClearTextPassword" value="pat" />
                </My_x0020_Feed>
                <NoPassword>
                  <add key="Username" value="me" />
                </NoPassword>
              </packageSourceCredentials>
            </configuration>
            """);

        var credentials = PackageSources.ReadConfig(config).Credentials;

        var credential = Assert.Single(credentials);
        Assert.Equal(new SourceCredential("https://pkgs.test/feed/index.json", "me", "pat"), credential);
    }

    [Fact]
    public void Config_without_credentials_yields_none()
    {
        var config = WriteConfig("""
            <configuration>
              <packageSources>
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """);

        Assert.Empty(PackageSources.ReadConfig(config).Credentials);
    }

    [Fact]
    public async Task Credentials_are_sent_only_to_the_source_host()
    {
        var inner = new CapturingHandler();
        using var http = new HttpClient(new SourceCredentialsHandler(
            [new SourceCredential("https://pkgs.test/org/_packaging/feed/nuget/v3/index.json", "me", "pat")])
        {
            InnerHandler = inner,
        });

        await http.GetAsync("https://pkgs.test/org/_packaging/feed/nuget/v3/flat2/tool/index.json");
        await http.GetAsync("https://api.nuget.org/v3/index.json");
        await http.GetAsync("http://pkgs.test/org/_packaging/feed/nuget/v3/index.json");

        Assert.Equal(
            "Basic " + Convert.ToBase64String("me:pat"u8.ToArray()),
            inner.Authorizations["https://pkgs.test/org/_packaging/feed/nuget/v3/flat2/tool/index.json"]);
        Assert.Null(inner.Authorizations["https://api.nuget.org/v3/index.json"]);
        Assert.Null(inner.Authorizations["http://pkgs.test/org/_packaging/feed/nuget/v3/index.json"]);
    }

    static string WriteConfig(string xml)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ndx-credentials", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, "nuget.config");
        File.WriteAllText(path, xml);
        return path;
    }

    sealed class CapturingHandler : HttpMessageHandler
    {
        public Dictionary<string, string?> Authorizations { get; } = new(StringComparer.Ordinal);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations[request.RequestUri!.ToString()] = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request });
        }
    }
}
