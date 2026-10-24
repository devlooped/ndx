using System.Diagnostics;

namespace ndx;

/// <summary>
/// Process start settings for the downloaded tool. ndx always starts the child
/// without a shell, with inherited standard in/out/error, and with
/// <c>NDX_LAUNCHED=1</c> so the child can tell it was started by ndx.
/// </summary>
public sealed record ProcessStartSettings
{
    public required string FileName { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public bool UseShellExecute { get; init; }
    public bool RedirectStandardInput { get; init; }
    public bool RedirectStandardOutput { get; init; }
    public bool RedirectStandardError { get; init; }
    public string? WorkingDirectory { get; init; }

    public ProcessStartInfo ToStartInfo()
    {
        var start = new ProcessStartInfo
        {
            FileName = FileName,
            UseShellExecute = UseShellExecute,
            RedirectStandardInput = RedirectStandardInput,
            RedirectStandardOutput = RedirectStandardOutput,
            RedirectStandardError = RedirectStandardError,
        };

        if (WorkingDirectory is { } cwd)
            start.WorkingDirectory = cwd;

        foreach (var argument in Arguments)
            start.ArgumentList.Add(argument);

        start.Environment["NDX_LAUNCHED"] = "1";

        return start;
    }
}

public interface IProcessRunner
{
    int Run(ProcessStartSettings settings);
    IChildProcess Start(ProcessStartSettings settings);
}

public sealed class ProcessRunner : IProcessRunner
{
    public int Run(ProcessStartSettings settings)
    {
        using var process = new Process { StartInfo = settings.ToStartInfo() };
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start '{settings.FileName}'.");
        process.WaitForExit();
        return process.ExitCode;
    }

    public IChildProcess Start(ProcessStartSettings settings)
    {
        var process = new Process { StartInfo = settings.ToStartInfo() };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Failed to start '{settings.FileName}'.");
        }

        return new ChildProcess(process);
    }
}
