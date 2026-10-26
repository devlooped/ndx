using System.Diagnostics;
using System.IO.Compression;
using ndx;

namespace Tests;

/// <summary>
/// Opt-in end to end against a Release build of the CodeAlta source
/// (<c>NDX_CODEALTA_E2E=1</c>). ndx posts the same close as a simulated update.
/// CodeAlta, started with <c>CODEALTA_AUTOMATED=1</c>, has to leave by its own
/// automated exit: exit code 0, the desktop log says so, and nothing that
/// outlived that process was terminated.
/// </summary>
public class CodeAltaSourceEvergreenTests
{
    [Fact(Timeout = 1_200_000)]
    public async Task Owned_desktop_update_closes_through_automated_exit()
    {
        if (Environment.GetEnvironmentVariable("NDX_CODEALTA_E2E") != "1")
            return;

        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var project = Path.GetFullPath(Path.Combine(repo, "..", "oss", "CodeAlta", "src", "CodeAlta", "CodeAlta.csproj"));
        Assert.True(File.Exists(project), "CodeAlta.csproj was not found at " + project);

        var built = BuiltOutput(project);
        var root = Path.Combine(Path.GetTempPath(), "ndx-codealta-source", Guid.NewGuid().ToString("n"));
        var feed = Path.Combine(root, "feed");
        var work = Path.Combine(root, "work");
        var store = Path.Combine(root, "store");
        var data = Path.Combine(root, "data");
        var closedData = data + "-closed";
        var catalog = Path.Combine(root, "catalog");
        var projectRoot = Path.Combine(root, "project");
        var home = Path.Combine(root, "home");
        var skills = Path.Combine(root, "skills");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(catalog);
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(skills);

        Pack(built, feed, "1.0.0");
        var nextPackage = Path.Combine(root, "next.nupkg");
        var packingNext = Task.Run(() => Pack(built, nextPackage, "1.0.1"));

        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new FreshDataRootRunner(new ProcessRunner(), data);
        var host = new NdxHost
        {
            WorkingDirectory = work,
            StoreDirectory = store,
            ProcessRunner = runner,
            Out = output,
            Error = error,
            UpdateInterval = TimeSpan.FromMilliseconds(500),
            StopTimeout = TimeSpan.FromSeconds(90),
        };

        var versionDir = Path.Combine(store, "codealta", "1.0.0");
        using var cts = new CancellationTokenSource();
        var run = App.RunAsync(
            [
                "CodeAlta", "--yes", "--source", feed, "--",
                "--data-root", data,
                "--catalog-root", catalog,
                "--allow-catalog-cache",
                "--allow-owned-host",
                "--project-root", projectRoot,
                "--discovery-home", home,
                "--instruction-root", projectRoot,
                "--builtin-skill-root", skills,
                "--mcp-port", "0",
            ],
            host,
            cts.Token);

        Process? main = null;
        var held = new List<Process>();
        try
        {
            await WaitUntil(
                () => LogHas(data, "Startup: application loading"),
                TimeSpan.FromMinutes(3),
                () => $"CodeAlta did not finish starting.{Environment.NewLine}{output}{error}{Environment.NewLine}{ReadLogs(data)}");

            main = FindAlta(versionDir);
            Assert.True(main is not null, "alta.exe from 1.0.0 was not running after startup.");
            var tracked = new HashSet<int>();
            ProcessTree.AddDescendants(main.Id, tracked);
            foreach (var pid in tracked)
            {
                try
                {
                    held.Add(Process.GetProcessById(pid));
                }
                catch (ArgumentException)
                {
                }
            }

            await packingNext;
            Directory.CreateDirectory(feed);
            var stop = System.Diagnostics.Stopwatch.StartNew();
            File.Move(nextPackage, Path.Combine(feed, "CodeAlta.1.0.1.nupkg"), overwrite: true);

            var aliveAfterParent = new List<Process>();
            var sawParentExit = false;
            await WaitUntil(
                () =>
                {
                    if (!sawParentExit && main.HasExited)
                    {
                        sawParentExit = true;
                        foreach (var child in held)
                        {
                            try
                            {
                                child.Refresh();
                                if (!child.HasExited)
                                    aliveAfterParent.Add(child);
                            }
                            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                            {
                            }
                        }
                    }

                    return runner.FirstExit.IsCompleted;
                },
                TimeSpan.FromMinutes(2),
                () => $"CodeAlta 1.0.0 did not exit after the update.{Environment.NewLine}{output}{error}{Environment.NewLine}{ReadLogs(Directory.Exists(closedData) ? closedData : data)}");

            var exitCode = await runner.FirstExit.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, exitCode);
            Assert.True(stop.Elapsed < host.StopTimeout, $"CodeAlta was still up after {stop.Elapsed}, the point where ndx kills the process.");
            Assert.Contains("Updating CodeAlta 1.0.0 → 1.0.1", output.ToString());

            var closedLog = ReadLogs(closedData);
            Assert.Contains("Closing the window (User): ExitWhenIdle.", closedLog);
            Assert.Contains("The application exits: its host closes.", closedLog);
            Assert.Contains("The host is closed.", closedLog);

            foreach (var child in aliveAfterParent)
            {
                child.Refresh();
                // ExitCode is only available for a process this test started. The child exiting
                // before the stop timeout is the graceful case: ndx kills a descendant only after that wait.
                Assert.True(child.HasExited, Describe(child) + " was still alive after CodeAlta exited.");
            }

            var leftovers = AltaUnder(versionDir);
            Assert.True(leftovers.Count == 0, "1.0.0 processes still alive when the update started the next process: " + string.Join(", ", leftovers));

            await WaitUntil(
                () => LogHas(data, "Startup: application loading"),
                TimeSpan.FromMinutes(3),
                () => $"Updated CodeAlta did not start.{Environment.NewLine}{output}{error}{Environment.NewLine}{ReadLogs(data)}");

            cts.Cancel();
            var restarted = await run.WaitAsync(TimeSpan.FromMinutes(2));
            Assert.Equal(0, restarted);
            await WaitUntil(
                () => LogHas(data, "The host is closed."),
                TimeSpan.FromSeconds(20),
                () => $"The restarted CodeAlta did not record a graceful exit.{Environment.NewLine}{ReadLogs(data)}");
            Assert.Contains("Closing the window (User): ExitWhenIdle.", ReadLogs(data));
        }
        finally
        {
            cts.Cancel();
            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch
            {
            }

            main?.Dispose();
            foreach (var child in held)
                child.Dispose();
            KillUnder(store);
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    static string BuiltOutput(string project)
    {
        var output = Path.Combine(Path.GetDirectoryName(project)!, "bin", "Release", "net10.0");
        var exe = Path.Combine(output, OperatingSystem.IsWindows() ? "alta.exe" : "alta");
        Assert.True(File.Exists(exe), "Release build of CodeAlta was not found at " + exe);
        return output;
    }

    static void Pack(string publishDir, string destination, string version)
    {
        var staging = Path.Combine(Path.GetTempPath(), "ndx-codealta-nupkg", Guid.NewGuid().ToString("n"));
        var tools = Path.Combine(staging, "tools", "net10.0", "any");
        CopyTree(publishDir, tools);
        var exe = OperatingSystem.IsWindows() ? "alta.exe" : "alta";
        File.WriteAllText(Path.Combine(tools, "DotnetToolSettings.xml"),
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <DotNetCliTool Version="1">
              <Commands>
                <Command Name="alta" EntryPoint="{exe}" Runner="executable" />
              </Commands>
            </DotNetCliTool>
            """);
        File.WriteAllText(Path.Combine(staging, "CodeAlta.nuspec"),
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>CodeAlta</id>
                <version>{version}</version>
                <authors>ndx</authors>
                <description>CodeAlta built from source</description>
                <packageTypes>
                  <packageType name="DotnetTool" />
                </packageTypes>
              </metadata>
            </package>
            """);

        var nupkg = destination.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)
            ? destination
            : Path.Combine(destination, $"CodeAlta.{version}.nupkg");
        Directory.CreateDirectory(Path.GetDirectoryName(nupkg)!);
        if (File.Exists(nupkg))
            File.Delete(nupkg);
        ZipFile.CreateFromDirectory(staging, nupkg);
        Directory.Delete(staging, recursive: true);
    }

    static readonly HashSet<string> RidOutputs = new(StringComparer.OrdinalIgnoreCase)
    {
        "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "ref",
    };

    static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                continue;
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (RidOutputs.Contains(name))
                continue;

            if (name.Equals("runtimes", StringComparison.OrdinalIgnoreCase))
            {
                var runtimes = Path.Combine(destination, name);
                Directory.CreateDirectory(runtimes);
                foreach (var rid in new[] { "win-x64", "win" })
                {
                    var from = Path.Combine(directory, rid);
                    if (Directory.Exists(from))
                        CopyAll(from, Path.Combine(runtimes, rid));
                }

                continue;
            }

            CopyTree(directory, Path.Combine(destination, name));
        }
    }

    static void CopyAll(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                continue;
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(source))
            CopyAll(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    static Process? FindAlta(string directory)
    {
        foreach (var process in Process.GetProcessesByName("alta"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (path is not null && IsUnder(path, directory))
                    return process;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            process.Dispose();
        }

        return null;
    }

    static List<string> AltaUnder(string directory)
    {
        var found = new List<string>();
        foreach (var process in Process.GetProcessesByName("alta"))
        {
            try
            {
                if (process.HasExited)
                    continue;
                var path = process.MainModule?.FileName;
                if (path is not null && IsUnder(path, directory))
                    found.Add($"{process.Id} {path}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return found;
    }

    static void KillUnder(string directory)
    {
        foreach (var process in Process.GetProcessesByName("alta"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (path is null || !IsUnder(path, directory))
                    continue;
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    static bool IsUnder(string path, string directory)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    static bool LogHas(string dataRoot, string text)
    {
        var logs = Path.Combine(dataRoot, "logs");
        if (!Directory.Exists(logs))
            return false;

        foreach (var file in Directory.GetFiles(logs))
        {
            if (ReadShared(file).Contains(text, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    static string ReadLogs(string dataRoot)
    {
        var logs = Path.Combine(dataRoot, "logs");
        if (!Directory.Exists(logs))
            return "";

        return string.Join(Environment.NewLine, Directory.GetFiles(logs).Select(file => Path.GetFileName(file) + Environment.NewLine + ReadShared(file)));
    }

    static string ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return "";
        }
    }

    static string Describe(Process process)
    {
        try
        {
            return $"{process.Id} {process.ProcessName}";
        }
        catch (InvalidOperationException)
        {
            return process.Id.ToString();
        }
    }

    static async Task WaitUntil(Func<bool> condition, TimeSpan timeout, Func<string> failure)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException(failure());
    }

    sealed class FreshDataRootRunner(IProcessRunner inner, string dataRoot) : IProcessRunner
    {
        readonly TaskCompletionSource<int> firstExit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int starts;

        public Task<int> FirstExit => firstExit.Task;

        public int Run(ProcessStartSettings settings) => inner.Run(settings);

        public IChildProcess Start(ProcessStartSettings settings)
        {
            var generation = Interlocked.Increment(ref starts);
            if (generation > 1)
                MoveAside(dataRoot);

            var child = inner.Start(settings);
            return generation == 1 ? new RememberExit(child, firstExit) : child;
        }

        static void MoveAside(string source)
        {
            var destination = source + "-closed";
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (Directory.Exists(destination))
                        Directory.Delete(destination, recursive: true);
                    if (Directory.Exists(source))
                        Directory.Move(source, destination);
                    return;
                }
                catch (IOException) when (attempt < 50)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }

    sealed class RememberExit(IChildProcess inner, TaskCompletionSource<int> exited) : IChildProcess
    {
        public int Id => inner.Id;
        public bool HasExited => inner.HasExited;
        public int ExitCode => inner.ExitCode;

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
            => inner.WaitForExitAsync(cancellationToken);

        public async Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            await inner.StopAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (inner.HasExited)
                exited.TrySetResult(inner.ExitCode);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
