using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ndx;

/// <summary>
/// Turns a RID-specific Native AOT tool nupkg into the archive + SHA256
/// attached to a GitHub Release (and consumed by the install scripts).
/// </summary>
public static class NativePacker
{
    public static bool IsWindowsRid(string rid)
        => rid.StartsWith("win", StringComparison.OrdinalIgnoreCase);

    public static string BinaryFileName(string rid)
        => IsWindowsRid(rid) ? "ndx.exe" : "ndx";

    public static string ArchiveFileName(string rid, string version)
        => IsWindowsRid(rid)
            ? $"ndx-{version}-{rid}.zip"
            : $"ndx-{version}-{rid}.tar.gz";

    /// <summary>
    /// glibc Native AOT builds (<c>linux-x64</c>, <c>linux-arm64</c>). Not musl.
    /// </summary>
    public static bool IsGlibcLinuxRid(string rid)
        => rid.Equals("linux-x64", StringComparison.OrdinalIgnoreCase)
        || rid.Equals("linux-arm64", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Same archive as <see cref="ArchiveFileName"/> with <c>-gnu</c> before the extension.
    /// Webi's classifier treats <c>gnu</c> as requiring GNU libc.
    /// </summary>
    public static string GnuArchiveFileName(string rid, string version)
    {
        const string extension = ".tar.gz";
        var name = ArchiveFileName(rid, version);
        if (!name.EndsWith(extension, StringComparison.Ordinal))
            throw new ArgumentException($"A -gnu alias is only published for tar.gz archives ('{rid}').", nameof(rid));

        return string.Concat(name.AsSpan(0, name.Length - extension.Length), "-gnu", extension);
    }

    public static NativePackResult Pack(
        string nupkgPath,
        string rid,
        string outputDirectory,
        string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nupkgPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rid);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var binaryName = BinaryFileName(rid);
        var payload = ReadNativeBinary(nupkgPath, rid, binaryName);

        Directory.CreateDirectory(outputDirectory);

        var archiveName = ArchiveFileName(rid, version);
        var archivePath = Path.Combine(outputDirectory, archiveName);
        if (File.Exists(archivePath))
            File.Delete(archivePath);

        if (IsWindowsRid(rid))
            PackZip(payload, binaryName, archivePath);
        else
            PackTarGz(payload, binaryName, archivePath);

        var sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath)))
            .ToLowerInvariant();
        var sha256Path = archivePath + ".sha256";
        File.WriteAllText(sha256Path, ChecksumLine(sha256, archiveName));

        string? gnuArchivePath = null;
        string? gnuSha256Path = null;
        if (IsGlibcLinuxRid(rid))
            (gnuArchivePath, gnuSha256Path) = CopyGnuAlias(archivePath, sha256, rid, version);

        return new NativePackResult(archivePath, sha256Path, sha256, binaryName, gnuArchivePath, gnuSha256Path);
    }

    /// <summary>
    /// Byte-for-byte copy named <c>ndx-VER-linux-ARCH-gnu.tar.gz</c>.
    /// Webi reads <c>gnu</c> as GNU libc. install.sh keeps the unsuffixed name.
    /// </summary>
    static (string ArchivePath, string Sha256Path) CopyGnuAlias(string archivePath, string sha256, string rid, string version)
    {
        var gnuName = GnuArchiveFileName(rid, version);
        var directory = Path.GetDirectoryName(archivePath)
            ?? throw new InvalidOperationException($"Archive '{archivePath}' has no directory.");
        var gnuArchivePath = Path.Combine(directory, gnuName);
        File.Copy(archivePath, gnuArchivePath, overwrite: true);
        var gnuSha256Path = gnuArchivePath + ".sha256";
        File.WriteAllText(gnuSha256Path, ChecksumLine(sha256, gnuName));
        return (gnuArchivePath, gnuSha256Path);
    }

    static string ChecksumLine(string sha256, string fileName)
        => $"{sha256}  {fileName}{Environment.NewLine}";

    static byte[] ReadNativeBinary(string nupkgPath, string rid, string binaryName)
    {
        if (!File.Exists(nupkgPath))
        {
            throw new FileNotFoundException(
                $"RID package '{nupkgPath}' was not found.",
                nupkgPath);
        }

        using var zip = ZipFile.OpenRead(nupkgPath);
        var matches = zip.Entries
            .Where(entry => IsNativeBinaryEntry(entry, binaryName))
            .ToArray();

        if (matches.Length == 0)
        {
            throw new FileNotFoundException(
                $"Native binary '{binaryName}' was not found in '{nupkgPath}'.",
                binaryName);
        }

        var preferred = "tools/any/" + rid + "/" + binaryName;
        var chosen = matches.FirstOrDefault(entry =>
                PathsEqual(entry.FullName, preferred))
            ?? (matches.Length == 1 ? matches[0] : null);

        if (chosen is null)
        {
            throw new InvalidOperationException(
                $"Multiple '{binaryName}' entries in '{nupkgPath}'.");
        }

        using var stream = chosen.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    static bool IsNativeBinaryEntry(ZipArchiveEntry entry, string binaryName)
    {
        if (entry.Length <= 0)
            return false;

        var path = entry.FullName.Replace('\\', '/');
        if (path.Contains(".dSYM/", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".dSYM", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(Path.GetFileName(path), binaryName, StringComparison.OrdinalIgnoreCase);
    }

    static bool PathsEqual(string left, string right)
        => string.Equals(
            left.Replace('\\', '/').TrimStart('/'),
            right.Replace('\\', '/').TrimStart('/'),
            StringComparison.OrdinalIgnoreCase);

    static void PackZip(byte[] payload, string binaryName, string archivePath)
    {
        using var zip = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        var entry = zip.CreateEntry(binaryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(payload);
    }

    static void PackTarGz(byte[] payload, string binaryName, string archivePath)
    {
        using var file = File.Create(archivePath);
        using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        using var tar = new TarWriter(gzip, TarEntryFormat.Pax);
        using var data = new MemoryStream(payload);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, binaryName)
        {
            DataStream = data,
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                 | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                 | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
        };
        tar.WriteEntry(entry);
    }
}

public sealed record NativePackResult(
    string ArchivePath,
    string Sha256Path,
    string Sha256,
    string BinaryName,
    string? GnuArchivePath = null,
    string? GnuSha256Path = null);
