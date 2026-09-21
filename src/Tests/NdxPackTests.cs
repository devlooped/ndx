using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using ndx;

namespace Tests;

public class NdxPackTests
{
    static readonly string[] ExpectedRids =
        ["win-x64", "win-arm64", "linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64", "osx-x64", "osx-arm64", "any"];

    [Fact]
    public void Pack_without_runtime_identifier_is_a_pointer_dotnet_tool()
    {
        using var dir = new TempDir("pointer");
        var first = Pack(dir, rid: null);
        var second = Pack(dir, rid: null);

        AssertSameIdentity(first, second);
        Assert.Equal("ndx", first.Id);
        Assert.Equal("DotnetTool", first.PackageType);
        Assert.Equal($"ndx.{first.Version}.nupkg", Path.GetFileName(first.NupkgPath));
        Assert.Equal(ExpectedRids.Length, first.RuntimeIdentifierPackages.Count);
        foreach (var rid in ExpectedRids)
            Assert.Equal("ndx." + rid, first.RuntimeIdentifierPackages[rid]);

        using (var zip = ZipFile.OpenRead(first.NupkgPath))
        {
            var readme = zip.Entries.Single(entry =>
                entry.Name.Equals("readme.md", StringComparison.OrdinalIgnoreCase));
            using var reader = new StreamReader(readme.Open());
            var text = reader.ReadToEnd();
            Assert.Contains("dotnet tool install -g ndx", text);
            Assert.Contains("*n*ative *d*otnet e*x*ecute", text);
            Assert.Contains("Sponsor this project", text);
        }
    }

    [Fact]
    public void Pack_any_is_a_framework_dependent_rid_package()
    {
        using var dir = new TempDir("any");
        var first = Pack(dir, rid: "any");
        var second = Pack(dir, rid: "any");

        AssertSameIdentity(first, second);
        Assert.Equal("ndx.any", first.Id);
        Assert.Equal("DotnetToolRidPackage", first.PackageType);
        Assert.Equal($"ndx.any.{first.Version}.nupkg", Path.GetFileName(first.NupkgPath));
        Assert.Equal("dotnet", first.Runner);
        Assert.Equal("ndx.dll", first.EntryPoint);
        Assert.True(first.PackedEntryPoint, first.EntryPoint);
        Assert.EndsWith(".dll", first.EntryPoint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pack_host_rid_is_a_native_executable_rid_package()
    {
        var rid = HostPortableRid();
        using var dir = new TempDir(rid);
        var first = Pack(dir, rid);
        var second = Pack(dir, rid);

        AssertSameIdentity(first, second);
        Assert.Equal("ndx." + rid, first.Id);
        Assert.Equal("DotnetToolRidPackage", first.PackageType);
        Assert.Equal($"ndx.{rid}.{first.Version}.nupkg", Path.GetFileName(first.NupkgPath));
        Assert.Equal("executable", first.Runner);
        Assert.False(string.IsNullOrEmpty(first.EntryPoint));
        Assert.False(first.EntryPoint.EndsWith(".dll", StringComparison.OrdinalIgnoreCase), first.EntryPoint);
        Assert.Equal(OperatingSystem.IsWindows() ? "ndx.exe" : "ndx", first.EntryPoint);
        Assert.True(first.PackedEntryPoint, first.EntryPoint);

        var archives = Path.Combine(dir.Root, "archives");
        var packed = NativePacker.Pack(first.NupkgPath, rid, archives, first.Version);
        Assert.Equal(NativePacker.ArchiveFileName(rid, first.Version), Path.GetFileName(packed.ArchivePath));
        Assert.True(File.Exists(packed.Sha256Path), packed.Sha256Path);
        Assert.Equal(first.EntryPoint, packed.BinaryName);
        using var nupkg = ZipFile.OpenRead(first.NupkgPath);
        var source = nupkg.Entries.Single(entry =>
            entry.Name.Equals(first.EntryPoint, StringComparison.OrdinalIgnoreCase));
        using var sourceStream = source.Open();
        using var expected = new MemoryStream();
        sourceStream.CopyTo(expected);
        if (NativePacker.IsWindowsRid(rid))
        {
            using var zip = ZipFile.OpenRead(packed.ArchivePath);
            var entry = Assert.Single(zip.Entries);
            Assert.Equal(first.EntryPoint, entry.FullName);
            using var stream = entry.Open();
            using var actual = new MemoryStream();
            stream.CopyTo(actual);
            Assert.Equal(expected.ToArray(), actual.ToArray());
        }
        else
        {
            using var file = File.OpenRead(packed.ArchivePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var tar = new System.Formats.Tar.TarReader(gzip);
            var entry = tar.GetNextEntry(copyData: true);
            Assert.NotNull(entry);
            Assert.Equal(first.EntryPoint, entry.Name);
            using var actual = new MemoryStream();
            entry.DataStream!.CopyTo(actual);
            Assert.Equal(expected.ToArray(), actual.ToArray());
            Assert.Null(tar.GetNextEntry());
        }
    }

    static PackedTool Pack(TempDir dir, string? rid)
    {
        Directory.CreateDirectory(dir.Nupkg);
        foreach (var existing in Directory.GetFiles(dir.Nupkg, "*.nupkg"))
            File.Delete(existing);

        var project = Path.Combine(FindRepoRoot(), "src", "ndx", "ndx.csproj");
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet",
            WorkingDirectory = FindRepoRoot(),
        };
        start.ArgumentList.Add("pack");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("Release");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add(dir.Nupkg);
        start.ArgumentList.Add("--nologo");
        start.ArgumentList.Add("-p:GeneratePackageOnBuild=false");
        start.ArgumentList.Add("--artifacts-path");
        start.ArgumentList.Add(dir.Artifacts);
        if (rid is not null)
        {
            start.ArgumentList.Add("-r");
            start.ArgumentList.Add(rid);
        }

        var (exit, stdout, stderr) = ProcessCapture.Run(start, timeoutMs: 600_000);
        var log = stdout + Environment.NewLine + stderr;
        File.WriteAllText(dir.LogPath, log);
        Assert.True(exit == 0, $"dotnet pack failed ({exit}).{Environment.NewLine}{log}");

        var nupkgs = Directory.GetFiles(dir.Nupkg, "*.nupkg")
            .Where(path => !path.EndsWith(".symbols.nupkg", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.True(nupkgs.Length == 1, $"expected one nupkg in {dir.Nupkg}, found {nupkgs.Length}:{Environment.NewLine}{string.Join(Environment.NewLine, nupkgs)}{Environment.NewLine}{log}");

        return ReadPackage(nupkgs[0]);
    }

    static PackedTool ReadPackage(string nupkgPath)
    {
        using var zip = ZipFile.OpenRead(nupkgPath);
        var nuspec = zip.Entries.Single(entry => entry.Name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        using var nuspecStream = nuspec.Open();
        var doc = XDocument.Load(nuspecStream);
        var metadata = doc.Root!.Elements().First(e => e.Name.LocalName == "metadata");
        var id = metadata.Elements().First(e => e.Name.LocalName == "id").Value;
        var version = metadata.Elements().First(e => e.Name.LocalName == "version").Value;
        var packageType = metadata.Descendants()
            .First(e => e.Name.LocalName == "packageType")
            .Attribute("name")!
            .Value;

        var settingsEntry = zip.Entries.Single(entry =>
            entry.Name.Equals("DotnetToolSettings.xml", StringComparison.OrdinalIgnoreCase));
        using var settingsStream = settingsEntry.Open();
        var settings = XDocument.Load(settingsStream);
        var command = settings.Descendants().First(e => e.Name.LocalName == "Command");
        var rids = settings.Descendants()
            .Where(e => e.Name.LocalName == "RuntimeIdentifierPackage")
            .ToDictionary(
                e => e.Attribute("RuntimeIdentifier")!.Value,
                e => e.Attribute("Id")!.Value,
                StringComparer.OrdinalIgnoreCase);

        var entryPoint = command.Attribute("EntryPoint")?.Value ?? "";
        var packedEntryPoint = !string.IsNullOrEmpty(entryPoint) &&
            zip.Entries.Any(entry => entry.Name.Equals(entryPoint, StringComparison.OrdinalIgnoreCase));

        return new PackedTool(
            nupkgPath,
            id,
            version,
            packageType,
            command.Attribute("Runner")?.Value ?? "",
            entryPoint,
            packedEntryPoint,
            rids);
    }

    static void AssertSameIdentity(PackedTool first, PackedTool second)
    {
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Version, second.Version);
        Assert.Equal(first.PackageType, second.PackageType);
        Assert.Equal(first.Runner, second.Runner);
        Assert.Equal(first.EntryPoint, second.EntryPoint);
        Assert.Equal(first.RuntimeIdentifierPackages, second.RuntimeIdentifierPackages);
    }

    static string HostPortableRid()
    {
        var host = RuntimeInformation.RuntimeIdentifier;
        return RidPackageResolver.Expand(host)
            .First(rid => ExpectedRids.Contains(rid, StringComparer.OrdinalIgnoreCase) && rid != "any");
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ndx.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root.");
    }

    sealed record PackedTool(
        string NupkgPath,
        string Id,
        string Version,
        string PackageType,
        string Runner,
        string EntryPoint,
        bool PackedEntryPoint,
        IReadOnlyDictionary<string, string> RuntimeIdentifierPackages);

    sealed class TempDir : IDisposable
    {
        public string Root { get; }
        public string Nupkg => Path.Combine(Root, "nupkg");
        public string Artifacts => Path.Combine(Root, "artifacts");
        public string LogPath => Path.Combine(Root, "pack.log");

        public TempDir(string suffix)
        {
            Root = Path.Combine(Path.GetTempPath(), "ndx-pack-nupkg-tests", suffix, Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }
}
