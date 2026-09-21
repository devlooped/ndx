using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using ndx;

namespace Tests;

public class NativePackerTests
{
    [Theory]
    [InlineData("win-x64")]
    [InlineData("win-arm64")]
    public void Windows_rid_emits_zip_of_ndx_exe_and_matching_sha256(string rid)
    {
        using var dir = new TempDir();
        var payload = "windows-native-bytes"u8.ToArray();
        var nupkg = RidNupkg.Write(dir.Nupkg, rid, payload);

        var result = NativePacker.Pack(nupkg, rid, dir.Output, "1.2.3");

        Assert.Equal($"ndx-1.2.3-{rid}.zip", Path.GetFileName(result.ArchivePath));
        Assert.Equal($"ndx-ci-{rid}.zip", NativePacker.ArchiveFileName(rid, "ci"));
        Assert.True(File.Exists(result.ArchivePath));
        Assert.True(File.Exists(result.Sha256Path));

        using (var zip = ZipFile.OpenRead(result.ArchivePath))
        {
            var entry = Assert.Single(zip.Entries);
            Assert.Equal("ndx.exe", entry.FullName);
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            Assert.Equal(payload, memory.ToArray());
        }

        AssertSha256File(result);
    }

    [Theory]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    [InlineData("osx-x64")]
    [InlineData("osx-arm64")]
    public void Unix_rid_emits_targz_of_ndx_and_matching_sha256(string rid)
    {
        using var dir = new TempDir();
        var payload = "unix-native-bytes"u8.ToArray();
        var nupkg = RidNupkg.Write(dir.Nupkg, rid, payload);

        var result = NativePacker.Pack(nupkg, rid, dir.Output, "4.5.6");

        Assert.Equal($"ndx-4.5.6-{rid}.tar.gz", Path.GetFileName(result.ArchivePath));
        if (NativePacker.IsGlibcLinuxRid(rid))
        {
            Assert.Equal(NativePacker.GnuArchiveFileName(rid, "4.5.6"), Path.GetFileName(result.GnuArchivePath));
            Assert.Equal(File.ReadAllBytes(result.ArchivePath), File.ReadAllBytes(result.GnuArchivePath!));
            var gnuSha = File.ReadAllText(result.GnuSha256Path!).Trim();
            Assert.StartsWith(result.Sha256, gnuSha, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(Path.GetFileName(result.GnuArchivePath)!, gnuSha);
        }
        else
        {
            Assert.Null(result.GnuArchivePath);
            Assert.False(File.Exists(Path.Combine(dir.Output, $"ndx-4.5.6-{rid}-gnu.tar.gz")));
        }

        using var file = File.OpenRead(result.ArchivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var entry = tar.GetNextEntry(copyData: true);
        Assert.NotNull(entry);
        Assert.Equal("ndx", entry.Name);
        using var memory = new MemoryStream();
        entry.DataStream!.CopyTo(memory);
        Assert.Equal(payload, memory.ToArray());
        Assert.Null(tar.GetNextEntry());

        AssertSha256File(result);
    }

    [Theory]
    [InlineData("linux-x64", "ndx-1.0.1-linux-x64-gnu.tar.gz")]
    [InlineData("linux-arm64", "ndx-1.0.1-linux-arm64-gnu.tar.gz")]
    public void Glibc_linux_archive_is_also_copied_with_a_gnu_suffix(string rid, string gnuName)
    {
        Assert.True(NativePacker.IsGlibcLinuxRid(rid));
        Assert.Equal(gnuName, NativePacker.GnuArchiveFileName(rid, "1.0.1"));
        Assert.False(NativePacker.IsGlibcLinuxRid("linux-musl-" + rid["linux-".Length..]));
        Assert.False(NativePacker.IsGlibcLinuxRid("osx-arm64"));
    }

    [Fact]
    public void Program_main_packs_through_the_shipped_entry_point()
    {
        using var dir = new TempDir();
        var nupkg = RidNupkg.Write(dir.Nupkg, "win-x64", "entry-point"u8.ToArray());

        var code = PackProgram.Main([nupkg, "win-x64", dir.Output, "9.9.9"]);

        Assert.Equal(0, code);
        var archive = Path.Combine(dir.Output, "ndx-9.9.9-win-x64.zip");
        var sha = archive + ".sha256";
        Assert.True(File.Exists(archive));
        Assert.True(File.Exists(sha));
        using var zip = ZipFile.OpenRead(archive);
        Assert.Equal("ndx.exe", Assert.Single(zip.Entries).FullName);
        var expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))).ToLowerInvariant();
        Assert.StartsWith(expected, File.ReadAllText(sha).Trim(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Osx_rid_ignores_dsym_dwarf_also_named_ndx()
    {
        using var dir = new TempDir();
        var payload = "osx-native-bytes"u8.ToArray();
        var nupkg = RidNupkg.Write(dir.Nupkg, "osx-arm64", payload);

        using (var zip = ZipFile.OpenRead(nupkg))
        {
            var namedNdx = zip.Entries
                .Where(entry => Path.GetFileName(entry.FullName) == "ndx")
                .Select(entry => entry.FullName.Replace('\\', '/'))
                .ToArray();
            Assert.Contains("tools/any/osx-arm64/ndx", namedNdx);
            Assert.Contains("tools/any/osx-arm64/ndx.dSYM/Contents/Resources/DWARF/ndx", namedNdx);
        }

        var result = NativePacker.Pack(nupkg, "osx-arm64", dir.Output, "1.0.0");
        using var file = File.OpenRead(result.ArchivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var entry = tar.GetNextEntry(copyData: true);
        Assert.NotNull(entry);
        using var memory = new MemoryStream();
        entry.DataStream!.CopyTo(memory);
        Assert.Equal(payload, memory.ToArray());
    }

    [Fact]
    public void Missing_binary_is_a_packer_error()
    {
        using var dir = new TempDir();
        var nupkg = Path.Combine(dir.Nupkg, "empty.nupkg");
        ZipFile.Open(nupkg, ZipArchiveMode.Create).Dispose();

        var ex = Assert.Throws<FileNotFoundException>(
            () => NativePacker.Pack(nupkg, "win-x64", dir.Output, "1.0.0"));
        Assert.Contains("ndx.exe", ex.Message);
        Assert.Equal(1, PackProgram.Main([nupkg, "linux-x64", dir.Output, "1.0.0"]));
    }

    [Fact]
    public void Missing_nupkg_is_a_packer_error()
    {
        using var dir = new TempDir();
        var missing = Path.Combine(dir.Nupkg, "nope.nupkg");
        var ex = Assert.Throws<FileNotFoundException>(
            () => NativePacker.Pack(missing, "win-x64", dir.Output, "1.0.0"));
        Assert.Contains("nope.nupkg", ex.Message);
    }

    static void AssertSha256File(NativePackResult result)
    {
        var expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(result.ArchivePath)))
            .ToLowerInvariant();
        Assert.Equal(expected, result.Sha256);
        var written = File.ReadAllText(result.Sha256Path).Trim();
        Assert.StartsWith(expected, written, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.GetFileName(result.ArchivePath), written);
    }

    sealed class TempDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ndx-pack-tests", Guid.NewGuid().ToString("n"));
        public string Nupkg => Path.Combine(Root, "nupkg");
        public string Output => Path.Combine(Root, "out");

        public TempDir()
        {
            Directory.CreateDirectory(Nupkg);
            Directory.CreateDirectory(Output);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }
}
