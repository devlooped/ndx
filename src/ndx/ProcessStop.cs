using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace ndx;

/// <summary>
/// Gracefully stops a child we started: SIGINT / Ctrl+C, then Windows
/// <c>WM_CLOSE</c> if the tool is a GUI, then <see cref="Process.Kill()"/> after a timeout.
/// Processes that child started are stopped as well, before the caller starts another one.
/// A NeoAstra app is closed through its unowned top-level <c>NeoAstra.Window</c> frames only.
/// Any other GUI keeps the generic close.
/// In-process <c>GenerateConsoleCtrlEvent</c> is enough — the parent survives when
/// <see cref="ShutdownScope"/> cancels the Ctrl+C. No stopr helper.
/// </summary>
public static class ProcessStop
{
    const uint CtrlCEvent = 0;
    const uint WmClose = 0x0010;
    const uint GaRoot = 2;
    const uint GwOwner = 4;
    const string NeoAstraWindowClass = "NeoAstra.Window";

    /// <summary>
    /// Asks <paramref name="process"/> to exit. Does not wait.
    /// </summary>
    public static void Signal(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.HasExited)
            return;

        if (OperatingSystem.IsWindows())
            SignalWindows(process);
        else
            SignalUnix(process.Id);
    }

    [SupportedOSPlatform("windows")]
    static void SignalWindows(Process process)
    {
#if NDX_WINDOWS
        if (TryCloseGui(process))
            return;

        if (!GenerateConsoleCtrlEvent(CtrlCEvent, 0) && !process.HasExited)
            process.Kill(entireProcessTree: true);
#else
        process.Kill(entireProcessTree: true);
#endif
    }

    static void SignalUnix(int pid)
    {
        try
        {
            using var kill = Process.Start(new ProcessStartInfo("kill", $"-s INT {pid}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            kill?.WaitForExit();
            if (kill is { ExitCode: 0 })
                return;
        }
        catch (Exception)
        {
        }

        try
        {
            Process.GetProcessById(pid).Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

#if NDX_WINDOWS
    [SupportedOSPlatform("windows")]
    static bool TryCloseGui(Process process)
    {
        process.Refresh();
        if (process.HasExited)
            return true;

        var windows = CollectWindows((uint)process.Id);
        if (TrySelectNeoAstraFrames(windows, out var frames))
        {
            foreach (var hWnd in frames)
                PostMessage(hWnd, WmClose, nint.Zero, nint.Zero);

            return true;
        }

        var posted = false;
        if (process.MainWindowHandle != nint.Zero && process.CloseMainWindow())
        {
            posted = true;
            process.Refresh();
            if (process.HasExited)
                return true;
        }

        if (process.MainWindowHandle != nint.Zero)
            windows.Add(process.MainWindowHandle);

        foreach (var hWnd in windows)
        {
            if (PostMessage(hWnd, WmClose, nint.Zero, nint.Zero))
                posted = true;
        }

        return posted;
    }

    [SupportedOSPlatform("windows")]
    static HashSet<nint> CollectWindows(uint pid)
    {
        var state = new GuiCloseState(pid);
        var handle = GCHandle.Alloc(state);
        try
        {
            var statePtr = GCHandle.ToIntPtr(handle);
            EnumWindows(EnumWindowsByPid, statePtr);
            EnumWindows(EnumHostedParent, statePtr);
            return state.Windows;
        }
        finally
        {
            handle.Free();
        }
    }

    // NeoAstra turns WM_CLOSE on an unowned top-level NeoAstra.Window into a cancelable user close.
    // An owned window of that class is a dialog. The same message on its dispatcher, caption, or
    // WebView child destroys that window instead. Filtering applies only once such a frame is
    // present; every other GUI is closed as before.
    [SupportedOSPlatform("windows")]
    static bool TrySelectNeoAstraFrames(HashSet<nint> windows, out List<nint> frames)
    {
        frames = [];
        var sawNeoAstraWindow = false;
        foreach (var hWnd in windows)
        {
            if (!HasClass(hWnd, NeoAstraWindowClass))
                continue;

            sawNeoAstraWindow = true;
            if (GetAncestor(hWnd, GaRoot) == hWnd && GetWindow(hWnd, GwOwner) == nint.Zero)
                frames.Add(hWnd);
        }

        return sawNeoAstraWindow;
    }

    [SupportedOSPlatform("windows")]
    static bool HasClass(nint hWnd, string className)
    {
        var name = new StringBuilder(256);
        return GetClassName(hWnd, name, name.Capacity) > 0
            && string.Equals(name.ToString(), className, StringComparison.Ordinal);
    }

    [SupportedOSPlatform("windows")]
    static bool EnumWindowsByPid(nint hWnd, nint lParam)
    {
        var state = (GuiCloseState)GCHandle.FromIntPtr(lParam).Target!;
        GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == state.TargetPid)
            state.Windows.Add(hWnd);
        return true;
    }

    [SupportedOSPlatform("windows")]
    static bool EnumHostedParent(nint hWnd, nint lParam)
    {
        EnumChildWindows(hWnd, EnumChildForHosted, lParam);
        return true;
    }

    [SupportedOSPlatform("windows")]
    static bool EnumChildForHosted(nint hWnd, nint lParam)
    {
        var state = (GuiCloseState)GCHandle.FromIntPtr(lParam).Target!;
        GetWindowThreadProcessId(hWnd, out var pid);
        if (pid != state.TargetPid)
            return true;

        state.Windows.Add(hWnd);
        var parent = GetAncestor(hWnd, GaRoot);
        if (parent != nint.Zero)
            state.Windows.Add(parent);
        return true;
    }

    sealed class GuiCloseState(uint targetPid)
    {
        public uint TargetPid { get; } = targetPid;
        public HashSet<nint> Windows { get; } = [];
    }

    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hWnd, uint uCmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);
#endif
}

/// <summary>
/// Owns Ctrl+C for the evergreen lifetime so an update-stop is not treated as a user shutdown.
/// </summary>
public sealed class ShutdownScope : IDisposable
{
    readonly CancellationTokenSource cts = new();
    int stoppingChild;
    bool disposed;

    public ShutdownScope() => Console.CancelKeyPress += OnCancel;

    public CancellationToken Token => cts.Token;

    public StoppingChildLease StoppingChild()
    {
        Interlocked.Increment(ref stoppingChild);
        return new StoppingChildLease(this);
    }

    internal void EndStoppingChild() => Interlocked.Decrement(ref stoppingChild);

    public void Cancel() => cts.Cancel();

    void OnCancel(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        if (Volatile.Read(ref stoppingChild) == 0)
            cts.Cancel();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Console.CancelKeyPress -= OnCancel;
        cts.Dispose();
    }

    public readonly struct StoppingChildLease(ShutdownScope scope) : IDisposable
    {
        public void Dispose() => scope.EndStoppingChild();
    }
}
