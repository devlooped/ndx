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
    public void Environment_credentials_take_precedence_over_config()
    {
        var suffix = Guid.NewGuid().ToString("n");
        var config = WriteConfig($"""
            <configuration>
              <packageSources>
                <add key="Feed{suffix}" value="https://pkgs.test/feed/index.json" />
                <add key="Other{suffix}" value="https://other.test/index.json" />
                <add key="Invalid{suffix}" value="https://invalid.test/index.json" />
              </packageSources>
              <packageSourceCredentials>
                <Feed{suffix}>
                  <add key="Username" value="config" />
                  <add key="ClearTextPassword" value="config-pat" />
                </Feed{suffix}>
              </packageSourceCredentials>
            </configuration>
            """);

        Environment.SetEnvironmentVariable($"NuGetPackageSourceCredentials_Feed{suffix}", "Username=env;Password=env-pat");
        Environment.SetEnvironmentVariable($"NuGetPackageSourceCredentials_Other{suffix}", "Username=other;Password=other-pat;ValidAuthenticationTypes=basic");
        Environment.SetEnvironmentVariable($"NuGetPackageSourceCredentials_Invalid{suffix}", "Password=pat");
        try
        {
            var credentials = PackageSources.ReadConfig(config).Credentials;

            Assert.Equal(
                [
                    new SourceCredential("https://pkgs.test/feed/index.json", "env", "env-pat"),
                    new SourceCredential("https://other.test/index.json", "other", "other-pat"),
                ],
                credentials);
        }
        finally
        {
            Environment.SetEnvironmentVariable($"NuGetPackageSourceCredentials_Feed{suffix}", null);
            Environment.SetEnvironmentVariable($"NuGetPackageSourceCredentials_Other{suffix}", null);
            Environment.SetEnvironmentVariable($"NuGetPackageSourceCredentials_Invalid{suffix}", null);
        }
    }

    [Fact]
    public void Config_credentials_expand_environment_variables()
    {
        var variable = "NDX_TEST_PAT_" + Guid.NewGuid().ToString("n");
        var config = WriteConfig($"""
            <configuration>
              <packageSources>
                <add key="Feed" value="https://pkgs.test/feed/index.json" />
              </packageSources>
              <packageSourceCredentials>
                <Feed>
                  <add key="Username" value="me" />
                  <add key="ClearTextPassword" value="%{variable}%" />
                </Feed>
              </packageSourceCredentials>
            </configuration>
            """);

        Environment.SetEnvironmentVariable(variable, "secret");
        try
        {
            var credential = Assert.Single(PackageSources.ReadConfig(config).Credentials);
            Assert.Equal("secret", credential.Password);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
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
