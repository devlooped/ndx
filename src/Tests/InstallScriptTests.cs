using System.Diagnostics;
using ndx;

namespace Tests;

public class InstallScriptTests
{
    [Fact]
    public void Install_scripts_are_in_the_repo_root()
    {
        var root = FindRepoRoot();
        Assert.True(File.Exists(Path.Combine(root, "install.sh")), "install.sh");
        Assert.True(File.Exists(Path.Combine(root, "install.ps1")), "install.ps1");
        Assert.True(File.Exists(Path.Combine(root, "uninstall.sh")), "uninstall.sh");
        Assert.True(File.Exists(Path.Combine(root, "uninstall.ps1")), "uninstall.ps1");
    }

    [Fact]
    public void Scripts_cover_the_six_release_rids()
    {
        var root = FindRepoRoot();
        var sh = File.ReadAllText(Path.Combine(root, "install.sh"));
        var ps = File.ReadAllText(Path.Combine(root, "install.ps1"));

        Assert.Contains("linux-${arch}", sh);
        Assert.Contains("linux-musl-${arch}", sh);
        Assert.Contains("/etc/alpine-release", sh);
        Assert.Contains("/lib/ld-musl-", sh);
        Assert.Contains("osx-${arch}", sh);
        Assert.Contains("win-${arch}", sh);
        Assert.Contains("win-$archName", ps);
        Assert.Contains("osx-$archName", ps);
        Assert.Contains("linux-$archName", ps);
        Assert.Contains("linux-musl-$archName", ps);
        Assert.Contains("/etc/alpine-release", ps);
        Assert.Contains("ld-musl-", ps);

        foreach (var rid in new[] { "linux-x64", "linux-arm64", "win-x64", "win-arm64", "osx-x64", "osx-arm64" })
        {
            var archive = NativePacker.ArchiveFileName(rid, "1.2.3");
            Assert.Contains(NativePacker.IsWindowsRid(rid) ? "zip" : "tar.gz", archive);
        }
    }

    [Fact]
    public void Powershell_install_script_installs_from_a_local_archive()
    {
        var root = FindRepoRoot();
        var script = Path.Combine(root, "install.ps1");
        using var dir = new TempDir();

        var payload = "installed-by-script"u8.ToArray();
        var packed = NativePacker.Pack(RidNupkg.Write(dir.Publish, "win-x64", payload), "win-x64", dir.Output, "1.0.0");

        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "pwsh" : "pwsh",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // pwsh may not be on PATH; fall back to Windows PowerShell.
        if (OperatingSystem.IsWindows() && GetFullPath("pwsh") is null)
            start.FileName = "powershell";

        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("--archive");
        start.ArgumentList.Add(packed.ArchivePath);
        start.ArgumentList.Add("--prefix");
        start.ArgumentList.Add(dir.Prefix);
        start.ArgumentList.Add("--rid");
        start.ArgumentList.Add("win-x64");
        start.ArgumentList.Add("--skip-path");

        using var process = Process.Start(start) ?? throw new InvalidOperationException("failed to start PowerShell");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"install.ps1 failed ({process.ExitCode}).{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");

        var dest = Path.Combine(dir.Prefix, "ndx.exe");
        Assert.True(File.Exists(dest), stdout);
        Assert.Equal(payload, File.ReadAllBytes(dest));
        Assert.Contains("installed", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Powershell_uninstall_script_removes_the_binary()
    {
        var root = FindRepoRoot();
        var install = Path.Combine(root, "install.ps1");
        var uninstall = Path.Combine(root, "uninstall.ps1");
        using var dir = new TempDir();

        var payload = "installed-by-script"u8.ToArray();
        var packed = NativePacker.Pack(RidNupkg.Write(dir.Publish, "win-x64", payload), "win-x64", dir.Output, "1.0.0");

        var installed = RunPowershell(install, dir, packed.ArchivePath, skipPath: true);
        Assert.True(installed.ExitCode == 0, $"install.ps1 failed ({installed.ExitCode}).{Environment.NewLine}{installed.Stdout}{Environment.NewLine}{installed.Stderr}");
        var dest = Path.Combine(dir.Prefix, "ndx.exe");
        Assert.True(File.Exists(dest), installed.Stdout);

        var (exit, stdout, stderr) = RunPowershell(uninstall, dir, archive: null, skipPath: true);
        Assert.True(exit == 0, $"uninstall.ps1 failed ({exit}).{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        Assert.False(File.Exists(dest), stdout);
        Assert.Contains("removed", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Powershell_installer_persists_user_path_and_broadcasts_environment_change()
    {
        var ps = File.ReadAllText(Path.Combine(FindRepoRoot(), "install.ps1"));
        Assert.Contains("SetEnvironmentVariable('Path', $updated, 'User')", ps);
        Assert.Contains("SendMessageTimeout", ps);
        Assert.Contains("'Environment'", ps);
        Assert.Contains("0x1a", ps);
        Assert.Contains("Add-NdxToUserPath", ps);
    }

    [Fact]
    public void Powershell_uninstaller_removes_user_path_and_broadcasts_environment_change()
    {
        var ps = File.ReadAllText(Path.Combine(FindRepoRoot(), "uninstall.ps1"));
        Assert.Contains("SetEnvironmentVariable('Path', $updated, 'User')", ps);
        Assert.Contains("SendMessageTimeout", ps);
        Assert.Contains("'Environment'", ps);
        Assert.Contains("0x1a", ps);
        Assert.Contains("Remove-NdxFromUserPath", ps);
        Assert.Contains("ndnx", ps);
        Assert.Contains("LOCALAPPDATA", ps);
    }

    [Fact]
    public void Shell_installer_writes_a_guarded_path_block_into_zshrc()
    {
        var bash = FindBash();
        Assert.True(bash is not null, "bash is required to verify install.sh PATH persist");

        var root = FindRepoRoot();
        using var dir = new TempDir();
        var packed = NativePacker.Pack(RidNupkg.Write(dir.Publish, "linux-x64", "unix-ndx"u8.ToArray()), "linux-x64", dir.Output, "1.0.0");

        RunInstallSh(bash, root, dir, packed.ArchivePath, skipPath: false);
        RunInstallSh(bash, root, dir, packed.ArchivePath, skipPath: false);

        var zshrc = File.ReadAllText(Path.Combine(dir.Home, ".zshrc"));
        Assert.Contains("# >>> ndx path >>>", zshrc);
        Assert.Contains("# <<< ndx path <<<", zshrc);
        Assert.Contains(ToBashPath(bash, dir.Prefix), zshrc);
        Assert.Equal(1, CountOccurrences(zshrc, "# >>> ndx path >>>"));
        Assert.True(File.Exists(Path.Combine(dir.Prefix, "ndx")));
    }

    [Fact]
    public void Shell_installer_skip_path_does_not_write_rc()
    {
        var bash = FindBash();
        Assert.True(bash is not null, "bash is required to verify install.sh PATH persist");

        var root = FindRepoRoot();
        using var dir = new TempDir();
        var packed = NativePacker.Pack(RidNupkg.Write(dir.Publish, "linux-x64", "unix-ndx"u8.ToArray()), "linux-x64", dir.Output, "1.0.0");

        RunInstallSh(bash, root, dir, packed.ArchivePath, skipPath: true);

        Assert.False(File.Exists(Path.Combine(dir.Home, ".zshrc")));
        Assert.True(File.Exists(Path.Combine(dir.Prefix, "ndx")));
    }

    [Fact]
    public void Shell_uninstaller_removes_binary_and_path_block_from_zshrc()
    {
        var bash = FindBash();
        Assert.True(bash is not null, "bash is required to verify uninstall.sh");

        var root = FindRepoRoot();
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Home, ".zshrc"), "# keep me\n");
        var packed = NativePacker.Pack(RidNupkg.Write(dir.Publish, "linux-x64", "unix-ndx"u8.ToArray()), "linux-x64", dir.Output, "1.0.0");

        RunInstallSh(bash, root, dir, packed.ArchivePath, skipPath: false);
        Assert.True(File.Exists(Path.Combine(dir.Prefix, "ndx")));

        RunUninstallSh(bash, root, dir, skipPath: false);

        Assert.False(File.Exists(Path.Combine(dir.Prefix, "ndx")));
        var zshrc = File.ReadAllText(Path.Combine(dir.Home, ".zshrc"));
        Assert.Contains("# keep me", zshrc);
        Assert.DoesNotContain("# >>> ndx path >>>", zshrc);
        Assert.DoesNotContain("# <<< ndx path <<<", zshrc);
        Assert.DoesNotContain(ToBashPath(bash, dir.Prefix), zshrc);
    }

    [Fact]
    public void Shell_uninstaller_skip_path_leaves_rc_intact()
    {
        var bash = FindBash();
        Assert.True(bash is not null, "bash is required to verify uninstall.sh PATH skip");

        var root = FindRepoRoot();
        using var dir = new TempDir();
        var packed = NativePacker.Pack(RidNupkg.Write(dir.Publish, "linux-x64", "unix-ndx"u8.ToArray()), "linux-x64", dir.Output, "1.0.0");

        RunInstallSh(bash, root, dir, packed.ArchivePath, skipPath: false);
        RunUninstallSh(bash, root, dir, skipPath: true);

        Assert.False(File.Exists(Path.Combine(dir.Prefix, "ndx")));
        var zshrc = File.ReadAllText(Path.Combine(dir.Home, ".zshrc"));
        Assert.Contains("# >>> ndx path >>>", zshrc);
        Assert.Contains("# <<< ndx path <<<", zshrc);
        Assert.Contains(ToBashPath(bash, dir.Prefix), zshrc);
    }

    [Fact]
    public void Powershell_uninstall_script_removes_legacy_ndnx()
    {
        var root = FindRepoRoot();
        var uninstall = Path.Combine(root, "uninstall.ps1");
        using var dir = new TempDir();

        var dest = Path.Combine(dir.Prefix, "ndx.exe");
        File.WriteAllBytes(dest, "ndx"u8.ToArray());

        var localApp = Path.Combine(dir.Root, "localapp");
        var legacyDir = Path.Combine(localApp, "ndnx");
        Directory.CreateDirectory(legacyDir);
        var legacy = Path.Combine(legacyDir, "ndnx.exe");
        File.WriteAllBytes(legacy, "legacy-ndnx"u8.ToArray());

        var (exit, stdout, stderr) = RunPowershell(uninstall, dir, archive: null, skipPath: true);
        Assert.True(exit == 0, $"uninstall.ps1 failed ({exit}).{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        Assert.False(File.Exists(dest), stdout);
        Assert.False(File.Exists(legacy), stdout);
        Assert.False(Directory.Exists(legacyDir), stdout);
        Assert.Contains("ndnx.exe", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Shell_uninstaller_removes_legacy_ndnx_binary_and_path_block()
    {
        var bash = FindBash();
        Assert.True(bash is not null, "bash is required to verify uninstall.sh ndnx cleanup");

        var root = FindRepoRoot();
        using var dir = new TempDir();
        var legacyDir = Path.Combine(dir.Home, ".local", "bin");
        Directory.CreateDirectory(legacyDir);
        File.WriteAllBytes(Path.Combine(legacyDir, "ndnx"), "legacy-ndnx"u8.ToArray());
        File.WriteAllText(Path.Combine(dir.Home, ".zshrc"), """
            # keep me
            # >>> ndnx path >>>
            export PATH="/old/ndnx:$PATH"
            # <<< ndnx path <<<
            """);

        RunUninstallSh(bash, root, dir, skipPath: false);

        Assert.False(File.Exists(Path.Combine(legacyDir, "ndnx")));
        var zshrc = File.ReadAllText(Path.Combine(dir.Home, ".zshrc"));
        Assert.Contains("# keep me", zshrc);
        Assert.DoesNotContain("# >>> ndnx path >>>", zshrc);
        Assert.DoesNotContain("# <<< ndnx path <<<", zshrc);
        Assert.DoesNotContain("/old/ndnx", zshrc);
    }

    [Fact]
    public void Install_scripts_resolve_ci_channel_without_a_v_prefix()
    {
        var root = FindRepoRoot();
        var sh = File.ReadAllText(Path.Combine(root, "install.sh"));
        var ps = File.ReadAllText(Path.Combine(root, "install.ps1"));

        Assert.Contains("ci)", sh);
        Assert.Contains("tag=ci", sh);
        Assert.Contains("version=ci", sh);
        Assert.Contains("$Version -eq 'ci'", ps);
        Assert.Contains("$tag = 'ci'", ps);
        Assert.Contains("$resolved = 'ci'", ps);
    }

    [Fact]
    public void Workflow_publishes_nuget_and_sleet_from_release_and_ci_build()
    {
        var yml = File.ReadAllText(Path.Combine(FindRepoRoot(), ".github", "workflows", "publish.yml"));
        var build = File.ReadAllText(Path.Combine(FindRepoRoot(), ".github", "workflows", "build.yml"));
        var ci = File.ReadAllText(Path.Combine(FindRepoRoot(), ".github", "workflows", "ci-release.yml"));
        Assert.Contains("dotnet nuget push", yml);
        Assert.Contains("sleet push", yml);
        Assert.Contains("NUGET_API_KEY", yml);
        Assert.Contains("SLEET_CONNECTION", yml);
        Assert.Contains("sleet push", build);
        Assert.Contains("SLEET_CONNECTION", build);
        Assert.Contains("-p:RuntimeIdentifiers=any", build);
        Assert.Contains("-r any", build);
        Assert.DoesNotContain("GeneratePackageOnBuild", build);
        Assert.DoesNotContain("dotnet nuget push", ci);
        Assert.DoesNotContain("sleet push", ci);
        Assert.Contains("install.sh", yml);
        Assert.Contains("install.ps1", yml);
        Assert.Contains("uninstall.sh", yml);
        Assert.Contains("uninstall.ps1", yml);
        Assert.Contains("uninstall.sh", ci);
        Assert.Contains("uninstall.ps1", ci);
    }

    static (int ExitCode, string Stdout, string Stderr) RunPowershell(string script, TempDir dir, string? archive, bool skipPath)
    {
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "pwsh" : "pwsh",
            WorkingDirectory = FindRepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (OperatingSystem.IsWindows() && GetFullPath("pwsh") is null)
            start.FileName = "powershell";

        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        if (archive is not null)
        {
            start.ArgumentList.Add("--archive");
            start.ArgumentList.Add(archive);
        }
        start.ArgumentList.Add("--prefix");
        start.ArgumentList.Add(dir.Prefix);
        start.ArgumentList.Add("--rid");
        start.ArgumentList.Add("win-x64");
        if (skipPath)
            start.ArgumentList.Add("--skip-path");

        start.Environment["HOME"] = dir.Home;
        start.Environment["LOCALAPPDATA"] = Path.Combine(dir.Root, "localapp");

        using var process = Process.Start(start) ?? throw new InvalidOperationException("failed to start PowerShell");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    static void RunUninstallSh(string bash, string repoRoot, TempDir dir, bool skipPath)
    {
        var start = new ProcessStartInfo
        {
            FileName = bash,
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(ToBashPath(bash, Path.Combine(repoRoot, "uninstall.sh")));
        start.Environment["HOME"] = ToBashPath(bash, dir.Home);
        start.Environment["SHELL"] = "/bin/zsh";
        start.Environment["NDX_PREFIX"] = ToBashPath(bash, dir.Prefix);
        start.Environment["NDX_RID"] = "linux-x64";
        start.Environment["NDX_SKIP_PATH"] = skipPath ? "1" : "0";

        using var process = Process.Start(start) ?? throw new InvalidOperationException("failed to start bash");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"uninstall.sh failed ({process.ExitCode}).{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        Assert.Contains("removed", stdout, StringComparison.OrdinalIgnoreCase);
    }

    static void RunInstallSh(string bash, string repoRoot, TempDir dir, string archive, bool skipPath)
    {
        var start = new ProcessStartInfo
        {
            FileName = bash,
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(ToBashPath(bash, Path.Combine(repoRoot, "install.sh")));
        start.Environment["HOME"] = ToBashPath(bash, dir.Home);
        start.Environment["SHELL"] = "/bin/zsh";
        start.Environment["NDX_ARCHIVE"] = ToBashPath(bash, archive);
        start.Environment["NDX_PREFIX"] = ToBashPath(bash, dir.Prefix);
        start.Environment["NDX_RID"] = "linux-x64";
        start.Environment["NDX_SKIP_PATH"] = skipPath ? "1" : "0";

        using var process = Process.Start(start) ?? throw new InvalidOperationException("failed to start bash");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"install.sh failed ({process.ExitCode}).{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        if (!skipPath)
            Assert.Contains("PATH configured", stdout);
    }

    static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = 0; (i = text.IndexOf(value, i, StringComparison.Ordinal)) >= 0; i += value.Length)
            count++;
        return count;
    }

    static string? FindBash()
    {
        var gitBash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        if (File.Exists(gitBash))
            return gitBash;
        return GetFullPath("bash");
    }

    static string ToBashPath(string bash, string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows())
            return full.Replace('\\', '/');

        var root = Path.GetPathRoot(full) ?? @"C:\";
        var drive = char.ToLowerInvariant(root[0]);
        var rest = full[root.Length..].Replace('\\', '/');
        var gitBash = bash.Contains("Git", StringComparison.OrdinalIgnoreCase);
        return gitBash ? $"/{drive}/{rest}" : $"/mnt/{drive}/{rest}";
    }

    static string? GetFullPath(string name)
    {
        if (File.Exists(name))
            return Path.GetFullPath(name);

        var path = Environment.GetEnvironmentVariable("PATH");
        if (path is null)
            return null;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
                return candidate;
            if (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe"))
                return candidate + ".exe";
        }

        return null;
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

    sealed class TempDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ndx-install-tests", Guid.NewGuid().ToString("n"));
        public string Publish => Path.Combine(Root, "publish");
        public string Output => Path.Combine(Root, "out");
        public string Prefix => Path.Combine(Root, "prefix");
        public string Home => Path.Combine(Root, "home");

        public TempDir()
        {
            Directory.CreateDirectory(Publish);
            Directory.CreateDirectory(Output);
            Directory.CreateDirectory(Prefix);
            Directory.CreateDirectory(Home);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }
}
