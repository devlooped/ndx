using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ndx;

/// <summary>
/// Replaces the running ndx binary with a GitHub Release asset.
/// </summary>
public static class SelfUpdate
{
    public const string DefaultRepository = "devlooped/ndx";
    public const string CiChannel = "ci";

    public static bool IsCiChannel(string? value)
        => value is not null && value.Equals(CiChannel, StringComparison.OrdinalIgnoreCase);

    public static string ReleaseTag(string version)
        => IsCiChannel(version) ? CiChannel : "v" + version;

    public static async Task<int> RunAsync(
        Invocation invocation,
        NdxHost host,
        HttpClient http,
        CancellationToken cancellationToken = default)
    {
        var currentVersion = host.CurrentVersion ?? ReadCurrentVersion();
        var repo = host.UpdateRepository
            ?? Environment.GetEnvironmentVariable("NDX_REPO")
            ?? DefaultRepository;
        var rid = host.RuntimeIdentifier ?? DetectRuntimeIdentifier();
        var executable = host.ExecutablePath ?? ResolveExecutablePath();
        var log = IsDetailed(invocation.Verbosity) ? host.Out : null;

        EnsureGitHubHeaders(http);

        string targetLabel;
        string tag;
        if (IsCiChannel(invocation.Version))
        {
            // Rolling prerelease: assets under tag `ci` change in place, so always download.
            targetLabel = CiChannel;
            tag = CiChannel;
        }
        else
        {
            var target = invocation.Version is { } specified
                ? NormalizeVersion(specified)
                : await ResolveLatestVersionAsync(http, repo, log, cancellationToken).ConfigureAwait(false);

            if (!PackageVersion.TryParse(target, out var targetVersion))
                throw new InvalidOperationException($"Invalid version '{target}'.");

            if (PackageVersion.TryParse(currentVersion, out var current) && current.Equals(targetVersion))
            {
                host.Out.WriteLine($"ndx is already {targetVersion}");
                return 0;
            }

            targetLabel = targetVersion.ToString();
            tag = ReleaseTag(targetLabel);
        }

        if (!File.Exists(executable))
        {
            throw new InvalidOperationException(
                $"Cannot self-update: '{executable}' was not found.");
        }

        host.Out.WriteLine($"Updating to {targetLabel}");

        var archiveName = ArchiveFileName(rid, targetLabel);
        var binaryName = BinaryFileName(rid);
        var archiveUrl = AssetUrl(repo, tag, archiveName);
        var shaUrl = archiveUrl + ".sha256";

        var tmp = Path.Combine(Path.GetTempPath(), "ndx-update-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tmp);
        try
        {
            var archivePath = Path.Combine(tmp, archiveName);
            log?.WriteLine($"Downloading {archiveUrl}");
            await DownloadAsync(http, archiveUrl, archivePath, cancellationToken).ConfigureAwait(false);

            log?.WriteLine($"Downloading {shaUrl}");
            var expected = await DownloadStringAsync(http, shaUrl, cancellationToken).ConfigureAwait(false);
            VerifySha256(archivePath, expected);

            var extracted = Path.Combine(tmp, binaryName);
            ExtractBinary(archivePath, rid, binaryName, extracted);
            ReplaceExecutable(executable, extracted);
            host.Out.WriteLine($"updated {executable}");
            return 0;
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); }
            catch (IOException) { }
        }
    }

    public static string ReadCurrentVersion()
        => NormalizeVersion(ReadInformationalVersion());

    /// <summary>
    /// <c>ndx {version} ({short_sha})</c> when a commit is present, otherwise <c>ndx {version}</c>.
    /// </summary>
    public static string FormatVersion(string? informational = null)
    {
        var value = informational ?? ReadInformationalVersion();
        var plus = value.IndexOf('+');
        var version = NormalizeVersion(plus >= 0 ? value[..plus] : value);
        if (plus < 0)
            return $"ndx {version}";

        var sha = value.AsSpan(plus + 1);
        var separator = sha.IndexOfAny(".-+");
        if (separator >= 0)
            sha = sha[..separator];
        if (sha.Length > 9)
            sha = sha[..9];
        if (sha.IsEmpty)
            return $"ndx {version}";

        return $"ndx {version} ({sha})";
    }

    static string ReadInformationalVersion()
        => typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? "0.0.0";

    public static string ResolveExecutablePath()
    {
        var process = Environment.ProcessPath;
        if (process is not null &&
            Path.GetFileNameWithoutExtension(process).Equals("ndx", StringComparison.OrdinalIgnoreCase))
        {
            return process;
        }

        return Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "ndx.exe" : "ndx");
    }

    public static string DetectRuntimeIdentifier()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new InvalidOperationException(
                $"ndx: unsupported architecture '{RuntimeInformation.OSArchitecture}'."),
        };

        if (OperatingSystem.IsWindows())
            return $"win-{arch}";
        if (OperatingSystem.IsMacOS())
            return $"osx-{arch}";
        if (OperatingSystem.IsLinux())
            return HasMuslLoader() ? $"linux-musl-{arch}" : $"linux-{arch}";

        throw new InvalidOperationException("ndx: unsupported OS.");
    }

    /// <summary>
    /// True when <paramref name="root"/> looks like a musl system (Alpine's
    /// <c>/etc/alpine-release</c>, or musl's loader <c>/lib/ld-musl-*.so*</c>).
    /// A glibc dynamic linker installed by gcompat does not hide the musl loader.
    /// </summary>
    public static bool HasMuslLoader(string root = "/")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (File.Exists(Path.Combine(root, "etc", "alpine-release")))
            return true;

        var lib = Path.Combine(root, "lib");
        if (!Directory.Exists(lib))
            return false;

        foreach (var _ in Directory.EnumerateFiles(lib, "ld-musl-*.so*"))
            return true;

        return false;
    }

    public static string LatestReleaseUrl(string repo)
        => $"https://api.github.com/repos/{repo}/releases/latest";

    public static string AssetUrl(string repo, string tag, string fileName)
        => $"https://github.com/{repo}/releases/download/{tag}/{fileName}";

    public static string ArchiveFileName(string rid, string version)
        => IsWindowsRid(rid)
            ? $"ndx-{version}-{rid}.zip"
            : $"ndx-{version}-{rid}.tar.gz";

    public static string BinaryFileName(string rid)
        => IsWindowsRid(rid) ? "ndx.exe" : "ndx";

    static bool IsWindowsRid(string rid)
        => rid.StartsWith("win", StringComparison.OrdinalIgnoreCase);

    static string NormalizeVersion(string value)
    {
        var text = value.Trim();
        var plus = text.IndexOf('+');
        if (plus >= 0)
            text = text[..plus];
        if (text.Length > 0 && (text[0] is 'v' or 'V'))
            text = text[1..];
        return text;
    }

    static async Task<string> ResolveLatestVersionAsync(
        HttpClient http,
        string repo,
        TextWriter? log,
        CancellationToken cancellationToken)
    {
        var url = LatestReleaseUrl(repo);
        log?.WriteLine($"GET {url}");
        using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Could not resolve latest release of {repo} ({(int)response.StatusCode}).");
        }

        var release = await response.Content
            .ReadFromJsonAsync(NuGetJsonContext.Default.GitHubRelease, cancellationToken)
            .ConfigureAwait(false);
        var tag = release?.TagName;
        if (string.IsNullOrWhiteSpace(tag))
            throw new InvalidOperationException($"Could not resolve latest release of {repo}.");

        return NormalizeVersion(tag);
    }

    static async Task DownloadAsync(HttpClient http, string url, string destination, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to download {url} ({(int)response.StatusCode}).");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    static async Task<string> DownloadStringAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to download {url} ({(int)response.StatusCode}).");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    static void VerifySha256(string archivePath, string checksumFile)
    {
        var expected = checksumFile.Trim();
        var separator = expected.IndexOfAny([' ', '\t']);
        if (separator >= 0)
            expected = expected[..separator];
        expected = expected.ToLowerInvariant();
        if (expected.Length == 0)
            throw new InvalidOperationException($"Missing SHA256 for {Path.GetFileName(archivePath)}.");

        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath)))
            .ToLowerInvariant();
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"SHA256 mismatch for {Path.GetFileName(archivePath)}{Environment.NewLine}  expected: {expected}{Environment.NewLine}  actual:   {actual}");
        }
    }

    static void ExtractBinary(string archivePath, string rid, string binaryName, string destination)
    {
        if (IsWindowsRid(rid))
        {
            using var zip = ZipFile.OpenRead(archivePath);
            var entry = zip.Entries.FirstOrDefault(e =>
                string.Equals(Path.GetFileName(e.FullName), binaryName, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Archive did not contain {binaryName}.");
            entry.ExtractToFile(destination, overwrite: true);
            return;
        }

        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry(copyData: true) is { } entry)
        {
            if (!string.Equals(Path.GetFileName(entry.Name), binaryName, StringComparison.OrdinalIgnoreCase))
                continue;

            entry.ExtractToFile(destination, overwrite: true);
            return;
        }

        throw new InvalidOperationException($"Archive did not contain {binaryName}.");
    }

    static void ReplaceExecutable(string currentPath, string newPath)
    {
        var incoming = currentPath + ".new";
        var backup = currentPath + ".old";
        File.Copy(newPath, incoming, overwrite: true);
        TryDelete(backup);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (File.Exists(currentPath))
                    File.Move(currentPath, backup, overwrite: true);
                File.Move(incoming, currentPath);
                TryDelete(backup);
            }
            else
            {
                File.SetUnixFileMode(
                    incoming,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                File.Move(incoming, currentPath, overwrite: true);
            }
        }
        catch
        {
            if (!File.Exists(currentPath) && File.Exists(backup))
                File.Move(backup, currentPath);
            TryDelete(incoming);
            throw;
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static void EnsureGitHubHeaders(HttpClient http)
    {
        if (http.DefaultRequestHeaders.UserAgent.Count == 0)
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ndx");
        if (http.DefaultRequestHeaders.Accept.Count == 0)
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    static bool IsDetailed(string? verbosity)
        => verbosity?.ToLowerInvariant() is "detailed" or "diagnostic" or "d" or "diag";
}
