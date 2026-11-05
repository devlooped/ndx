using System.Diagnostics;

namespace ndx;

/// <summary>
/// Process start settings for the downloaded tool. ndx always starts the child
/// without a shell, with inherited standard in/out/error, with
/// <c>NDX_LAUNCHED=1</c> so the child can tell it was started by ndx, and with
/// <c>CODEALTA_AUTOMATED=1</c> so a CodeAlta child exits without prompts when
/// ndx stops it. See https://github.com/CodeAlta/CodeAlta/issues/221.
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
        start.Environment["CODEALTA_AUTOMATED"] = "1";

        return start;
    }
}

public interface IProcessRunner
{
    int Run(ProcessStartSettings settings);
    IChildProcess Start(ProcessStartSettings settings);
}

public interface IProcessOutputRunner
{
    ProcessResult RunWithOutput(ProcessStartSettings settings);
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class ProcessRunner : IProcessRunner, IProcessOutputRunner
{
    public int Run(ProcessStartSettings settings)
    {
        using var process = new Process { StartInfo = settings.ToStartInfo() };
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start '{settings.FileName}'.");
        process.WaitForExit();
        return process.ExitCode;
    }

    public ProcessResult RunWithOutput(ProcessStartSettings settings)
    {
        var start = settings.ToStartInfo();
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = new Process { StartInfo = start };
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start '{settings.FileName}'.");

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
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
