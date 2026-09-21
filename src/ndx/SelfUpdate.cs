using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace ndx;

/// <summary>
/// Replaces the running ndx binary with the one inside the NuGet RID package.
/// The rolling <c>ci</c> channel still comes from the GitHub Release tag <c>ci</c>.
/// </summary>
public static class SelfUpdate
{
    public const string DefaultRepository = "devlooped/ndx";
    public const string CiChannel = "ci";

    /// <summary>nuget.org flat container. Version lists and nupkgs. No trailing lookup of the service index.</summary>
    public const string NugetFlatContainer = "https://api.nuget.org/v3-flatcontainer/";

    /// <summary>nuget.org registration base. The leaf points at the catalog entry that carries the SHA512.</summary>
    public const string NugetRegistration = "https://api.nuget.org/v3/registration5-gz-semver2/";

    /// <summary>
    /// Sleet feed used when nuget.org can't be reached. Same RID packages, no catalog hash.
    /// </summary>
    public const string BlobFlatContainer = "https://kzu.blob.core.windows.net/nuget/flatcontainer/";

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

        EnsureUserAgent(http);

        if (!File.Exists(executable))
        {
            throw new InvalidOperationException(
                $"Cannot self-update: '{executable}' was not found.");
        }

        // Rolling prerelease: assets under tag `ci` are not on nuget.org.
        if (IsCiChannel(invocation.Version))
        {
            return await UpdateFromReleaseArchiveAsync(
                http, host, repo, rid, executable, CiChannel, CiChannel, log, cancellationToken)
                .ConfigureAwait(false);
        }

        var packageId = RidPackageId(rid);
        string target;
        if (invocation.Version is { } specified)
        {
            target = NormalizeVersion(specified);
            if (!PackageVersion.TryParse(target, out _))
                throw new InvalidOperationException($"Invalid version '{target}'.");
        }
        else
        {
            target = await ResolveLatestStableAsync(http, packageId, log, cancellationToken).ConfigureAwait(false);
        }

        if (PackageVersion.TryParse(target, out var targetVersion) &&
            PackageVersion.TryParse(currentVersion, out var current) &&
            current.Equals(targetVersion))
        {
            host.Out.WriteLine($"ndx is already {targetVersion}");
            return 0;
        }

        host.Out.WriteLine($"Updating to {target}");

        var tmp = Path.Combine(Path.GetTempPath(), "ndx-update-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tmp);
        try
        {
            var nupkg = await DownloadRidPackageAsync(http, packageId, target, tmp, log, cancellationToken)
                .ConfigureAwait(false);
            var binaryName = BinaryFileName(rid);
            var extracted = Path.Combine(tmp, binaryName);
            ExtractNupkgBinary(nupkg, rid, binaryName, extracted);
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

    public static string RidPackageId(string rid)
        => "ndx." + rid.ToLowerInvariant();

    public static string FlatIndexUrl(string flatBase, string packageId)
        => Combine(flatBase, packageId.ToLowerInvariant() + "/index.json");

    public static string NupkgUrl(string flatBase, string packageId, string version)
    {
        var id = packageId.ToLowerInvariant();
        var ver = version.ToLowerInvariant();
        return Combine(flatBase, $"{id}/{ver}/{id}.{ver}.nupkg");
    }

    public static string RegistrationLeafUrl(string registrationBase, string packageId, string version)
        => Combine(registrationBase, $"{packageId.ToLowerInvariant()}/{version.ToLowerInvariant()}.json");

    /// <summary>Highest stable version in a flat-container <c>versions</c> array. Prereleases are skipped.</summary>
    public static string? SelectLatestStable(IEnumerable<string>? versions)
    {
        if (versions is null)
            return null;

        PackageVersion? best = null;
        string? bestText = null;
        foreach (var text in versions)
        {
            if (!PackageVersion.TryParse(text, out var version) || version.IsPrerelease)
                continue;
            if (best is null || version.CompareTo(best.Value) > 0)
            {
                best = version;
                bestText = text.Trim();
            }
        }

        return bestText;
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

    static async Task<int> UpdateFromReleaseArchiveAsync(
        HttpClient http,
        NdxHost host,
        string repo,
        string rid,
        string executable,
        string targetLabel,
        string tag,
        TextWriter? log,
        CancellationToken cancellationToken)
    {
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

    static async Task<string> ResolveLatestStableAsync(
        HttpClient http,
        string packageId,
        TextWriter? log,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        foreach (var flat in new[] { NugetFlatContainer, BlobFlatContainer })
        {
            var url = FlatIndexUrl(flat, packageId);
            try
            {
                log?.WriteLine($"GET {url}");
                using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    last = new InvalidOperationException(
                        $"Could not resolve latest {packageId} ({(int)response.StatusCode}).");
                    continue;
                }

                var index = await response.Content
                    .ReadFromJsonAsync(NuGetJsonContext.Default.FlatContainerIndex, cancellationToken)
                    .ConfigureAwait(false);
                var latest = SelectLatestStable(index?.Versions);
                if (latest is null)
                {
                    last = new InvalidOperationException(
                        $"Could not resolve latest stable version of {packageId}.");
                    continue;
                }

                return latest;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw last ?? new InvalidOperationException($"Could not resolve latest stable version of {packageId}.");
    }

    static async Task<string> DownloadRidPackageAsync(
        HttpClient http,
        string packageId,
        string version,
        string directory,
        TextWriter? log,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        foreach (var (flat, registration) in new (string Flat, string? Registration)[]
        {
            (NugetFlatContainer, NugetRegistration),
            (BlobFlatContainer, null),
        })
        {
            var url = NupkgUrl(flat, packageId, version);
            var destination = Path.Combine(directory, Path.GetFileName(url));
            try
            {
                log?.WriteLine($"Downloading {url}");
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    last = new InvalidOperationException(
                        $"Failed to download {url} ({(int)response.StatusCode}).");
                    continue;
                }

                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var output = File.Create(destination))
                {
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }

                if (registration is not null)
                    await VerifyCatalogHashAsync(http, registration, packageId, version, destination, log, cancellationToken)
                        .ConfigureAwait(false);

                return destination;
            }
            catch (PackageHashMismatchException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                TryDelete(destination);
            }
        }

        throw last ?? new InvalidOperationException($"Failed to download {packageId} {version}.");
    }

    static async Task VerifyCatalogHashAsync(
        HttpClient http,
        string registrationBase,
        string packageId,
        string version,
        string nupkgPath,
        TextWriter? log,
        CancellationToken cancellationToken)
    {
        var leafUrl = RegistrationLeafUrl(registrationBase, packageId, version);
        log?.WriteLine($"GET {leafUrl}");
        using var leafResponse = await http.GetAsync(leafUrl, cancellationToken).ConfigureAwait(false);
        if (!leafResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to read registration for {packageId} {version} ({(int)leafResponse.StatusCode}).");
        }

        var leaf = await leafResponse.Content
            .ReadFromJsonAsync(NuGetJsonContext.Default.RegistrationLeaf, cancellationToken)
            .ConfigureAwait(false);
        var catalogUrl = CatalogEntryUrl(leaf?.CatalogEntry ?? default);
        if (string.IsNullOrWhiteSpace(catalogUrl))
            throw new InvalidOperationException($"Registration for {packageId} {version} has no catalog entry.");

        log?.WriteLine($"GET {catalogUrl}");
        using var catalogResponse = await http.GetAsync(catalogUrl, cancellationToken).ConfigureAwait(false);
        if (!catalogResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Failed to read catalog entry for {packageId} {version} ({(int)catalogResponse.StatusCode}).");
        }

        var catalog = await catalogResponse.Content
            .ReadFromJsonAsync(NuGetJsonContext.Default.CatalogLeaf, cancellationToken)
            .ConfigureAwait(false);
        var expected = catalog?.PackageHash?.Trim();
        if (string.IsNullOrEmpty(expected) ||
            !string.Equals(catalog?.PackageHashAlgorithm, "SHA512", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Catalog entry for {packageId} {version} has no SHA512 package hash.");
        }

        var actual = Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(nupkgPath)));
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new PackageHashMismatchException(
                $"SHA512 mismatch for {Path.GetFileName(nupkgPath)}{Environment.NewLine}  expected: {expected}{Environment.NewLine}  actual:   {actual}");
        }
    }

    static string? CatalogEntryUrl(JsonElement catalogEntry)
    {
        switch (catalogEntry.ValueKind)
        {
            case JsonValueKind.String:
                return catalogEntry.GetString();
            case JsonValueKind.Object:
                if (catalogEntry.TryGetProperty("@id", out var id) ||
                    catalogEntry.TryGetProperty("@Id", out id))
                    return id.GetString();
                return null;
            default:
                return null;
        }
    }

    static void ExtractNupkgBinary(string nupkgPath, string rid, string binaryName, string destination)
    {
        var wanted = "tools/any/" + rid.ToLowerInvariant() + "/" + binaryName;
        using var zip = ZipFile.OpenRead(nupkgPath);
        var entry = zip.Entries.FirstOrDefault(e =>
            e.FullName.Replace('\\', '/').Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            throw new InvalidOperationException($"Package did not contain {wanted}.");

        entry.ExtractToFile(destination, overwrite: true);
    }

    static string Combine(string baseUrl, string relative)
    {
        if (!baseUrl.EndsWith('/'))
            baseUrl += "/";
        return baseUrl + relative.TrimStart('/');
    }

    sealed class PackageHashMismatchException(string message) : InvalidOperationException(message);

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

    static void EnsureUserAgent(HttpClient http)
    {
        if (http.DefaultRequestHeaders.UserAgent.Count == 0)
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ndx");
    }

    static bool IsDetailed(string? verbosity)
        => verbosity?.ToLowerInvariant() is "detailed" or "diagnostic" or "d" or "diag";
}
