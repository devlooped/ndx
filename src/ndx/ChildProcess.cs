using System.ComponentModel;
using System.Diagnostics;

namespace ndx;

public interface IChildProcess : IAsyncDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);
    Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

public sealed class ChildProcess : IChildProcess
{
    readonly Process process;

    public ChildProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        this.process = process;
        this.process.EnableRaisingEvents = true;
    }

    public Process Process => process;

    public int Id => process.Id;

    public bool HasExited
    {
        get
        {
            process.Refresh();
            return process.HasExited;
        }
    }

    public int ExitCode => process.ExitCode;

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
        => WaitCoreAsync(cancellationToken);

    async Task<int> WaitCoreAsync(CancellationToken cancellationToken)
    {
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    public async Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        // Remember descendants before the stop signal. After this process exits they are
        // reparented, and a later kill of this pid no longer reaches them.
        var tracked = new HashSet<int>();
        if (!HasExited)
        {
            ProcessTree.AddDescendants(process.Id, tracked);
            ProcessStop.Signal(process);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            using var refresh = new CancellationTokenSource();
            var refreshing = RefreshDescendantsAsync(tracked, refresh.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                    {
                    }
                }

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                refresh.Cancel();
                try
                {
                    await refreshing.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        ProcessTree.AddDescendants(process.Id, tracked);
        // Descendants of a graceful exit are already stopping. Wait for that before killing anyone.
        await ReapAsync(tracked, timeout).ConfigureAwait(false);
    }

    async Task RefreshDescendantsAsync(HashSet<int> tracked, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !HasExited)
        {
            ProcessTree.AddDescendants(process.Id, tracked);
            try
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        ProcessTree.AddDescendants(process.Id, tracked);
    }

    async Task ReapAsync(HashSet<int> pids, TimeSpan timeout)
    {
        var children = new List<(int Pid, Process Process)>();
        foreach (var pid in pids)
        {
            if (pid <= 4 || pid == Environment.ProcessId || pid == process.Id)
                continue;

            try
            {
                var child = Process.GetProcessById(pid);
                if (child.HasExited)
                {
                    child.Dispose();
                    continue;
                }

                children.Add((pid, child));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
            {
            }
        }

        await Task.WhenAll(children.Select(item => WaitThenKillAsync(item.Pid, item.Process, timeout))).ConfigureAwait(false);
    }

    static async Task WaitThenKillAsync(int pid, Process child, TimeSpan timeout)
    {
        using (child)
        {
            using var wait = new CancellationTokenSource(timeout);
            try
            {
                if (!child.HasExited)
                    await child.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
            }

            if (child.HasExited)
                return;

            try
            {
                child.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
            }

            using var killed = new CancellationTokenSource(timeout);
            try
            {
                if (!child.HasExited)
                    await child.WaitForExitAsync(killed.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!child.HasExited)
                    throw new InvalidOperationException($"Process {pid} did not exit.");
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        process.Dispose();
        return ValueTask.CompletedTask;
    }
}
