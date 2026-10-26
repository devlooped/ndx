using ndx;

namespace Tests;

public class ToolLauncherTests
{
    [Fact]
    public void Executable_runner_inherits_console_and_does_not_use_shell()
    {
        var command = new ToolCommand("hello", @"C:\tools\hello.exe", "executable");
        var settings = ToolLauncher.CreateStartSettings(command, ["a", "b"], allowRollForward: false);

        Assert.Equal(@"C:\tools\hello.exe", settings.FileName);
        Assert.Equal(["a", "b"], settings.Arguments);
        Assert.False(settings.UseShellExecute);
        Assert.False(settings.RedirectStandardInput);
        Assert.False(settings.RedirectStandardOutput);
        Assert.False(settings.RedirectStandardError);
    }

    [Fact]
    public void Dotnet_runner_uses_muxer_exec_and_still_inherits_console()
    {
        var command = new ToolCommand("hello", @"C:\tools\hello.dll", "dotnet");
        var settings = ToolLauncher.CreateStartSettings(command, ["x"], allowRollForward: true);

        Assert.False(settings.UseShellExecute);
        Assert.False(settings.RedirectStandardInput);
        Assert.False(settings.RedirectStandardOutput);
        Assert.False(settings.RedirectStandardError);
        Assert.Contains("exec", settings.Arguments);
        Assert.Contains(@"C:\tools\hello.dll", settings.Arguments);
        Assert.Contains("--roll-forward", settings.Arguments);
        Assert.Contains("Major", settings.Arguments);
        Assert.Contains("x", settings.Arguments);
        var muxer = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        Assert.EndsWith(muxer, settings.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToStartInfo_preserves_no_shell_and_no_redirect()
    {
        var settings = new ProcessStartSettings
        {
            FileName = "tool.exe",
            Arguments = ["--flag"],
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };

        var start = settings.ToStartInfo();
        Assert.False(start.UseShellExecute);
        Assert.False(start.RedirectStandardInput);
        Assert.False(start.RedirectStandardOutput);
        Assert.False(start.RedirectStandardError);
        Assert.Equal(["--flag"], start.ArgumentList.ToArray());
        Assert.Equal("1", start.Environment["NDX_LAUNCHED"]);
        Assert.Equal("1", start.Environment["CODEALTA_AUTOMATED"]);
    }
}
