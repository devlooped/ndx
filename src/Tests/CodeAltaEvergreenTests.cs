using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ndx;

namespace Tests;

public class CodeAltaEvergreenTests
{
    [Fact]
    public async Task Simulated_update_sends_close_and_reaps_alta_processes_before_restart()
    {
        var published = CodeAltaSim.Publish();
        var root = Path.Combine(Path.GetTempPath(), "ndx-codealta-run", Guid.NewGuid().ToString("n"));
        var feed = Path.Combine(root, "feed");
        var work = Path.Combine(root, "work");
        var store = Path.Combine(root, "store");
        Directory.CreateDirectory(feed);
        Directory.CreateDirectory(work);
        CodeAltaSim.Pack(published, feed, "1.0.0");

        var output = new StringWriter();
        var error = new StringWriter();
        var host = new NdxHost
        {
            WorkingDirectory = work,
            StoreDirectory = store,
            ProcessRunner = new ProcessRunner(),
            Out = output,
            Error = error,
            UpdateInterval = TimeSpan.FromMilliseconds(200),
            StopTimeout = TimeSpan.FromSeconds(8),
        };

        var simDir = Path.Combine(work, "codealta-sim");
        var previous = Path.Combine(store, "codealta", "1.0.0");
        using var cts = new CancellationTokenSource();
        var run = App.RunAsync(["CodeAlta", "--yes", "--source", feed], host, cts.Token);
        try
        {
            await WaitUntil(
                () => File.Exists(Path.Combine(simDir, "ready")),
                TimeSpan.FromSeconds(30),
                () => $"CodeAlta did not become ready.{Environment.NewLine}{output}{Environment.NewLine}{error}{Environment.NewLine}{Read(Path.Combine(simDir, "error.txt"))}");

            var workers = ReadWorkers(Path.Combine(simDir, "workers.txt"));
            Assert.Contains(workers, worker => worker.Name.Equals("alta", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(workers, worker => worker.Name.Equals("CodeAlta", StringComparison.OrdinalIgnoreCase));
            Assert.All(workers, worker => Assert.True(IsAlive(worker.Pid), $"{worker.Name} {worker.Pid} was not alive before the update."));

            var start = Read(Path.Combine(simDir, "start-1.txt"));
            Assert.Contains("automated=1", start);

            CodeAltaSim.Pack(published, feed, "1.0.1");

            await WaitUntil(
                () => File.Exists(Path.Combine(simDir, "start-2.txt")),
                TimeSpan.FromSeconds(20),
                () => $"Updated CodeAlta did not start.{Environment.NewLine}{output}{Environment.NewLine}{error}{Environment.NewLine}{Read(Path.Combine(simDir, "error.txt"))}");

            var signal = Read(Path.Combine(simDir, "signal.txt")).Trim();
            Assert.Equal(OperatingSystem.IsWindows() ? "WM_CLOSE" : "SIGINT", signal);
            Assert.Contains("Updating CodeAlta 1.0.0 → 1.0.1", output.ToString());

            var next = Read(Path.Combine(simDir, "start-2.txt"));
            Assert.Contains("prior=", next);
            var prior = next.Split('\n').Select(line => line.Trim('\r')).First(line => line.StartsWith("prior=", StringComparison.Ordinal));
            Assert.Equal("prior=", prior);

            var stillRunning = workers
                .Where(worker => IsAltaUnder(worker.Pid, previous))
                .Select(worker => $"{worker.Name} {worker.Pid}")
                .ToList();
            Assert.True(stillRunning.Count == 0, "Processes still alive after the close signal: " + string.Join(", ", stillRunning));

            var leftovers = AltaUnder(previous);
            Assert.True(
                leftovers.Count == 0,
                "alta/CodeAlta processes from 1.0.0 were still alive when 1.0.1 started: " + string.Join(", ", leftovers));
        }
        finally
        {
            cts.Cancel();
            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(20));
            }
            catch
            {
            }

            KillUnder(store);
        }
    }

    static List<(int Pid, string Name)> ReadWorkers(string path)
    {
        var workers = new List<(int Pid, string Name)>();
        foreach (var line in File.ReadAllLines(path))
        {
            var split = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (split.Length == 2 && int.TryParse(split[0], out var pid))
                workers.Add((pid, split[1]));
        }

        return workers;
    }

    static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    static bool IsAltaUnder(int pid, string directory)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
                return false;

            var name = process.ProcessName;
            if (!name.Equals("alta", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("CodeAlta", StringComparison.OrdinalIgnoreCase))
                return false;

            var path = process.MainModule?.FileName;
            return path is not null && IsUnder(path, directory);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    static List<string> AltaUnder(string directory)
    {
        var found = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                if (!name.Equals("alta", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("CodeAlta", StringComparison.OrdinalIgnoreCase))
                    continue;

                var path = process.MainModule?.FileName;
                if (path is not null && IsUnder(path, directory))
                    found.Add($"{process.Id} {name} {path}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
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
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                if (!name.Equals("alta", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("CodeAlta", StringComparison.OrdinalIgnoreCase))
                    continue;

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

    static string Read(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

    static async Task WaitUntil(Func<bool> condition, TimeSpan timeout, Func<string> failure)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(20);
        }

        throw new TimeoutException(failure());
    }
}

static class CodeAltaSim
{
    const string SimSource = """
        using System.Diagnostics;
        using System.Runtime.InteropServices;

        static class Program
        {
            static string simDirectory = "";
            static WndProcDel? windowProc;

            [STAThread]
            static int Main(string[] args)
            {
                simDirectory = Path.Combine(Environment.CurrentDirectory, "codealta-sim");
                Directory.CreateDirectory(simDirectory);

                if (args.Contains("--worker"))
                    return Worker();

                var prior = LivingRecorded();
                var self = Environment.ProcessPath ?? "";
                var folder = Path.GetDirectoryName(self) ?? "";
                var codeAlta = Path.Combine(folder, OperatingSystem.IsWindows() ? "CodeAlta.exe" : "CodeAlta");
                if (!File.Exists(codeAlta))
                {
                    File.WriteAllText(Path.Combine(simDirectory, "error.txt"), "Missing " + codeAlta);
                    return 1;
                }

                if (!Start(self) || !Start(codeAlta))
                    return 1;

                if (!WaitForWorkers(2, TimeSpan.FromSeconds(10)))
                {
                    File.WriteAllText(Path.Combine(simDirectory, "error.txt"), "Workers did not record themselves.");
                    return 1;
                }

                var generation = 1 + Directory.GetFiles(simDirectory, "start-*.txt").Length;
                File.WriteAllText(
                    Path.Combine(simDirectory, $"start-{generation}.txt"),
                    $"pid={Environment.ProcessId}{Environment.NewLine}" +
                    $"prior={string.Join(',', prior)}{Environment.NewLine}" +
                    $"workers={string.Join(',', ReadWorkerIds())}{Environment.NewLine}" +
                    $"automated={Environment.GetEnvironmentVariable("CODEALTA_AUTOMATED")}{Environment.NewLine}");

                return OperatingSystem.IsWindows() ? WindowsLoop() : UnixLoop();
            }

            static int Worker()
            {
                try
                {
                    Console.CancelKeyPress += (_, e) => e.Cancel = true;
                }
                catch (InvalidOperationException)
                {
                }

                AppendLine(Path.Combine(simDirectory, "workers.txt"), Environment.ProcessId + " " + Process.GetCurrentProcess().ProcessName);
                Thread.Sleep(Timeout.Infinite);
                return 0;
            }

            static bool Start(string file)
            {
                try
                {
                    var started = Process.Start(new ProcessStartInfo(file, "--worker")
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Environment.CurrentDirectory,
                    });
                    if (started is not null)
                        return true;

                    File.WriteAllText(Path.Combine(simDirectory, "error.txt"), "Failed to start " + file);
                    return false;
                }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(simDirectory, "error.txt"), ex.ToString());
                    return false;
                }
            }

            static bool WaitForWorkers(int count, TimeSpan timeout)
            {
                var path = Path.Combine(simDirectory, "workers.txt");
                var deadline = DateTime.UtcNow + timeout;
                while (DateTime.UtcNow < deadline)
                {
                    var lines = 0;
                    foreach (var line in ReadLines(path))
                    {
                        if (line.Length > 0)
                            lines++;
                    }

                    if (lines >= count)
                        return true;

                    Thread.Sleep(20);
                }

                return false;
            }

            static List<int> ReadWorkerIds()
            {
                var ids = new List<int>();
                foreach (var line in ReadLines(Path.Combine(simDirectory, "workers.txt")))
                {
                    var split = line.Split(' ', 2);
                    if (int.TryParse(split[0], out var pid))
                        ids.Add(pid);
                }

                return ids;
            }

            static string[] ReadLines(string path)
            {
                if (!File.Exists(path))
                    return [];

                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    var lines = new List<string>();
                    while (reader.ReadLine() is { } line)
                        lines.Add(line);
                    return lines.ToArray();
                }
                catch (IOException)
                {
                    return [];
                }
            }

            static List<int> LivingRecorded()
            {
                var ids = new HashSet<int>();
                foreach (var file in Directory.GetFiles(simDirectory, "start-*.txt"))
                {
                    foreach (var line in ReadLines(file))
                    {
                        if (line.StartsWith("pid=", StringComparison.Ordinal) && int.TryParse(line.AsSpan(4), out var pid))
                            ids.Add(pid);
                        if (!line.StartsWith("workers=", StringComparison.Ordinal))
                            continue;

                        foreach (var part in line["workers=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (int.TryParse(part, out var worker))
                                ids.Add(worker);
                        }
                    }
                }

                foreach (var pid in ReadWorkerIds())
                    ids.Add(pid);

                var alive = new List<int>();
                foreach (var pid in ids)
                {
                    if (pid == Environment.ProcessId || !IsAlta(pid))
                        continue;
                    alive.Add(pid);
                }

                return alive;
            }

            static bool IsAlta(int pid)
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (process.HasExited)
                        return false;
                    var name = process.ProcessName;
                    return name.Equals("alta", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("CodeAlta", StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    return false;
                }
            }

            static void AppendLine(string path, string line)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                        stream.Write(bytes);
                        return;
                    }
                    catch (IOException) when (attempt < 50)
                    {
                        Thread.Sleep(10);
                    }
                }
            }

            static int UnixLoop()
            {
                var done = new ManualResetEventSlim(false);
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    File.WriteAllText(Path.Combine(simDirectory, "signal.txt"), "SIGINT");
                    done.Set();
                };
                File.WriteAllText(Path.Combine(simDirectory, "ready"), "1");
                done.Wait();
                return 0;
            }

            static int WindowsLoop()
            {
                windowProc = WndProc;
                var instance = GetModuleHandle(null);
                var wndClass = new WndClass
                {
                    lpfnWndProc = windowProc,
                    hInstance = instance,
                    lpszClassName = "NeoAstra.Window",
                };
                var atom = RegisterClass(ref wndClass);
                if (atom == 0)
                {
                    File.WriteAllText(Path.Combine(simDirectory, "error.txt"), "RegisterClass failed: " + Marshal.GetLastWin32Error());
                    return 1;
                }

                var hwnd = CreateWindowEx(0, (nint)atom, "CodeAlta", 0x00CF0000, 0, 0, 80, 80, 0, 0, instance, 0);
                if (hwnd == 0)
                {
                    File.WriteAllText(Path.Combine(simDirectory, "error.txt"), "CreateWindowEx failed: " + Marshal.GetLastWin32Error());
                    return 1;
                }

                ShowWindow(hwnd, 0);
                File.WriteAllText(Path.Combine(simDirectory, "ready"), "1");
                while (GetMessage(out var msg, 0, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }

                return 0;
            }

            static nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
            {
                if (msg == 0x0010)
                {
                    File.WriteAllText(Path.Combine(simDirectory, "signal.txt"), "WM_CLOSE");
                    PostQuitMessage(0);
                    return 0;
                }

                return DefWindowProc(hWnd, msg, wParam, lParam);
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            struct WndClass
            {
                public uint style;
                public WndProcDel lpfnWndProc;
                public int cbClsExtra;
                public int cbWndExtra;
                public nint hInstance;
                public nint hIcon;
                public nint hCursor;
                public nint hbrBackground;
                public string? lpszMenuName;
                public string lpszClassName;
            }

            delegate nint WndProcDel(nint hWnd, uint msg, nint wParam, nint lParam);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            static extern nint GetModuleHandle(string? lpModuleName);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern ushort RegisterClass(ref WndClass lpWndClass);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern nint CreateWindowEx(uint dwExStyle, nint lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

            [DllImport("user32.dll")]
            static extern bool ShowWindow(nint hWnd, int nCmdShow);

            [DllImport("user32.dll")]
            static extern int GetMessage(out Msg lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

            [DllImport("user32.dll")]
            static extern bool TranslateMessage(ref Msg lpMsg);

            [DllImport("user32.dll")]
            static extern nint DispatchMessage(ref Msg lpMsg);

            [DllImport("user32.dll")]
            static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

            [DllImport("user32.dll")]
            static extern void PostQuitMessage(int nExitCode);

            struct Msg
            {
                public nint hwnd;
                public uint message;
                public nint wParam;
                public nint lParam;
                public uint time;
                public int ptX;
                public int ptY;
            }
        }
        """;

    static readonly object Gate = new();

    public static string Publish()
    {
        lock (Gate)
        {
            var root = Path.Combine(Path.GetTempPath(), "ndx-codealta-sim");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SimSource)));
            var stamp = Path.Combine(root, "stamp.txt");
            var altaOut = Path.Combine(root, "publish-alta");
            var exeName = OperatingSystem.IsWindows() ? "alta.exe" : "alta";
            var codeName = OperatingSystem.IsWindows() ? "CodeAlta.exe" : "CodeAlta";
            if (File.Exists(stamp) && File.ReadAllText(stamp) == hash &&
                File.Exists(Path.Combine(altaOut, exeName)) &&
                File.Exists(Path.Combine(altaOut, codeName)))
                return altaOut;

            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);

            Directory.CreateDirectory(root);
            var altaProj = Path.Combine(root, "alta");
            var codeProj = Path.Combine(root, "CodeAlta");
            Directory.CreateDirectory(altaProj);
            Directory.CreateDirectory(codeProj);
            File.WriteAllText(Path.Combine(altaProj, "Program.cs"), SimSource);
            File.WriteAllText(Path.Combine(altaProj, "alta.csproj"), Project(includeProgram: false));
            File.WriteAllText(Path.Combine(codeProj, "CodeAlta.csproj"), Project(includeProgram: true));

            var codeOut = Path.Combine(root, "publish-codealta");
            RunDotnet(altaProj, altaOut);
            RunDotnet(codeProj, codeOut);
            foreach (var file in Directory.GetFiles(codeOut))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith("CodeAlta", StringComparison.OrdinalIgnoreCase))
                    File.Copy(file, Path.Combine(altaOut, name), overwrite: true);
            }

            if (!File.Exists(Path.Combine(altaOut, codeName)))
                throw new InvalidOperationException("CodeAlta apphost was not produced.");

            File.WriteAllText(stamp, hash);
            return altaOut;
        }
    }

    public static void Pack(string publishDir, string feedDir, string version)
    {
        var staging = Path.Combine(Path.GetTempPath(), "ndx-codealta-nupkg", Guid.NewGuid().ToString("n"));
        var tools = Path.Combine(staging, "tools", "net10.0", "any");
        Directory.CreateDirectory(tools);
        foreach (var file in Directory.GetFiles(publishDir))
            File.Copy(file, Path.Combine(tools, Path.GetFileName(file)), overwrite: true);

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
                <description>simulated CodeAlta</description>
                <packageTypes>
                  <packageType name="DotnetTool" />
                </packageTypes>
              </metadata>
            </package>
            """);

        Directory.CreateDirectory(feedDir);
        var nupkg = Path.Combine(feedDir, $"CodeAlta.{version}.nupkg");
        if (File.Exists(nupkg))
            File.Delete(nupkg);
        ZipFile.CreateFromDirectory(staging, nupkg);
    }

    static string Project(bool includeProgram) =>
        includeProgram
            ? """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <AssemblyName>CodeAlta</AssemblyName>
                <UseAppHost>true</UseAppHost>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="..\alta\Program.cs" Link="Program.cs" />
              </ItemGroup>
            </Project>
            """
            : """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>WinExe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <AssemblyName>alta</AssemblyName>
                <UseAppHost>true</UseAppHost>
              </PropertyGroup>
            </Project>
            """;

    static void RunDotnet(string projectDir, string output)
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")
        {
            WorkingDirectory = projectDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("publish");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("Release");
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add(output);
        start.ArgumentList.Add("--nologo");
        var (exit, stdout, stderr) = ProcessCapture.Run(start, timeoutMs: 180_000);
        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"dotnet publish failed ({exit}).{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        }
    }
}
