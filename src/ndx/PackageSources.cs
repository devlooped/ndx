using System.Xml;
using System.Xml.Linq;

namespace ndx;

public sealed record ResolvedSources(IReadOnlyList<string> Sources, IReadOnlyList<SourceCredential> Credentials);

/// <summary>
/// Resolves NuGet sources and credentials from --source / --add-source / --configfile / nuget.config.
/// </summary>
public static class PackageSources
{
    public const string NugetOrg = "https://api.nuget.org/v3/index.json";

    public static ResolvedSources Resolve(Invocation invocation, string workingDirectory)
    {
        var configPath = invocation.ConfigFile is { } explicitConfig
            ? Path.GetFullPath(explicitConfig, workingDirectory)
            : FindNuGetConfig(workingDirectory);

        // even if --source overrides config sources, credentials still apply to matching source URLs
        var config = configPath is not null && File.Exists(configPath)
            ? ReadConfig(configPath)
            : new ResolvedSources([], []);

        var sources = invocation.Sources.Count > 0
            ? invocation.Sources.Select(s => Normalize(s, workingDirectory)).ToList()
            : config.Sources.ToList();

        foreach (var extra in invocation.AddSources)
        {
            var normalized = Normalize(extra, workingDirectory);
            if (!sources.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                sources.Add(normalized);
        }

        if (sources.Count == 0)
            sources.Add(NugetOrg);

        return new ResolvedSources(sources, config.Credentials);
    }

    public static string? FindNuGetConfig(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "nuget.config");
            if (File.Exists(candidate))
                return candidate;

            candidate = Path.Combine(directory.FullName, "NuGet.Config");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        return null;
    }

    public static ResolvedSources ReadConfig(string configPath)
    {
        var document = XDocument.Load(configPath);
        var root = document.Root;
        if (root is null)
            return new ResolvedSources([], []);

        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in root.Element("disabledPackageSources")?.Elements("add") ?? [])
        {
            var key = (string?)item.Attribute("key");
            var value = (string?)item.Attribute("value");
            if (key is not null && value is not null &&
                (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1"))
            {
                disabled.Add(key);
            }
        }

        var sources = new List<string>();
        var credentials = new List<SourceCredential>();
        var credentialEntries = root.Element("packageSourceCredentials")?.Elements().ToList() ?? [];
        var configDirectory = Path.GetDirectoryName(configPath) ?? Directory.GetCurrentDirectory();
        foreach (var item in root.Element("packageSources")?.Elements("add") ?? [])
        {
            var key = (string?)item.Attribute("key");
            var value = (string?)item.Attribute("value");
            if (value is null)
                continue;

            if (key is null || !disabled.Contains(key))
                sources.Add(Normalize(value, configDirectory));

            if (key is null)
                continue;

            // nuget stores source keys as XML names, e.g. "My Feed" becomes My_x0020_Feed.
            var entry = credentialEntries.FirstOrDefault(e =>
                string.Equals(XmlConvert.DecodeName(e.Name.LocalName), key, StringComparison.OrdinalIgnoreCase));
            var username = ReadSetting(entry, "Username");
            var password = ReadSetting(entry, "ClearTextPassword");
            if (username is not null && password is not null)
                credentials.Add(new SourceCredential(value, username, password));
        }

        return new ResolvedSources(sources, credentials);
    }

    static string? ReadSetting(XElement? entry, string name)
    {
        var value = (string?)entry?.Elements("add")
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("key"), name, StringComparison.OrdinalIgnoreCase))
            ?.Attribute("value");

        return value is null ? null : Environment.ExpandEnvironmentVariables(value);
    }

    static string Normalize(string source, string baseDirectory)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return source;
        }

        return Path.GetFullPath(source, baseDirectory);
    }
}
