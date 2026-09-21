using ndx;

namespace Tests;

public class ArgParserTests
{
    [Fact]
    public void Package_at_version_yes_source_and_terminator_forwards_remaining()
    {
        var parsed = ArgParser.Parse(
            "pkg@1.2.3", "--yes", "--source", "https://feed.example", "--", "hello", "world");

        Assert.True(parsed.Success);
        Assert.Equal("pkg", parsed.PackageId);
        Assert.Equal("1.2.3", parsed.Version);
        Assert.True(parsed.Yes);
        Assert.Equal(["https://feed.example"], parsed.Sources);
        Assert.Equal(["hello", "world"], parsed.ForwardedArguments);
    }

    [Fact]
    public void Tokens_after_terminator_that_look_like_ndx_flags_still_forward()
    {
        var parsed = ArgParser.Parse(
            "pkg", "--", "--yes", "--source", "https://feed.example", "-v", "detailed");

        Assert.True(parsed.Success);
        Assert.Equal("pkg", parsed.PackageId);
        Assert.Empty(parsed.Sources);
        Assert.False(parsed.Yes);
        Assert.Null(parsed.Verbosity);
        Assert.Equal(["--yes", "--source", "https://feed.example", "-v", "detailed"], parsed.ForwardedArguments);
    }

    [Fact]
    public void Missing_package_operand_is_an_error()
    {
        var parsed = ArgParser.Parse("--yes", "--source", "https://feed.example");

        Assert.False(parsed.Success);
        Assert.Null(parsed.PackageId);
        Assert.Contains("PACKAGE_NAME", parsed.Error);
    }

    [Fact]
    public void Empty_argv_is_an_error()
    {
        var parsed = ArgParser.Parse();

        Assert.False(parsed.Success);
        Assert.Contains("PACKAGE_NAME", parsed.Error);
    }

    [Fact]
    public void Consumes_all_documented_restore_and_tool_flags()
    {
        var parsed = ArgParser.Parse(
            "acme.tool",
            "--add-source", "https://extra.example",
            "--configfile", "nuget.config",
            "--prerelease",
            "-y",
            "--allow-roll-forward",
            "--disable-parallel",
            "--ignore-failed-sources",
            "--no-http-cache",
            "--interactive",
            "-v", "diagnostic",
            "child-a",
            "--child-flag");

        Assert.True(parsed.Success);
        Assert.Equal("acme.tool", parsed.PackageId);
        Assert.Null(parsed.Version);
        Assert.Equal(["https://extra.example"], parsed.AddSources);
        Assert.Equal("nuget.config", parsed.ConfigFile);
        Assert.True(parsed.Prerelease);
        Assert.True(parsed.Yes);
        Assert.True(parsed.AllowRollForward);
        Assert.True(parsed.DisableParallel);
        Assert.True(parsed.IgnoreFailedSources);
        Assert.True(parsed.NoHttpCache);
        Assert.True(parsed.Interactive);
        Assert.Equal("diagnostic", parsed.Verbosity);
        Assert.Equal(["child-a", "--child-flag"], parsed.ForwardedArguments);
    }

    [Fact]
    public void Version_option_and_at_version_conflict()
    {
        var parsed = ArgParser.Parse("pkg@1.0.0", "--version", "2.0.0");

        Assert.False(parsed.Success);
        Assert.Contains("--version", parsed.Error);
    }

    [Theory]
    [InlineData("pkg@1.*", "1.*")]
    [InlineData("pkg@1.1.*", "1.1.*")]
    [InlineData("pkg@1.1.1.*", "1.1.1.*")]
    public void Star_floating_versions_are_accepted(string identity, string version)
    {
        var parsed = ArgParser.Parse(identity);

        Assert.True(parsed.Success);
        Assert.Equal("pkg", parsed.PackageId);
        Assert.Equal(version, parsed.Version);
    }

    [Fact]
    public void Equals_form_is_accepted_for_valued_options()
    {
        var parsed = ArgParser.Parse("pkg", "--source=./feed", "--version=1.0.0");

        Assert.True(parsed.Success);
        Assert.Equal(["./feed"], parsed.Sources);
        Assert.Equal("1.0.0", parsed.Version);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("--VERSION")]
    public void Version_alone_prints_the_ndx_version(string arg)
    {
        var parsed = ArgParser.Parse(arg);

        Assert.True(parsed.Success);
        Assert.True(parsed.ShowVersion);
        Assert.Null(parsed.PackageId);
        Assert.Null(parsed.Version);
        Assert.Empty(parsed.ForwardedArguments);
    }

    [Fact]
    public void Version_with_a_value_and_no_package_is_still_an_error()
    {
        var parsed = ArgParser.Parse("--version", "1.2.3");

        Assert.False(parsed.Success);
        Assert.False(parsed.ShowVersion);
        Assert.Contains("PACKAGE_NAME", parsed.Error);
    }

    [Theory]
    [InlineData("stop", "--version")]
    [InlineData("stop", "--VERSION")]
    public void Bare_version_after_a_package_is_forwarded_to_the_tool(params string[] args)
    {
        var parsed = ArgParser.Parse(args);
        var viaSeparator = ArgParser.Parse(args[0], "--", args[1]);

        Assert.True(parsed.Success);
        Assert.False(parsed.ShowVersion);
        Assert.Equal("stop", parsed.PackageId);
        Assert.Null(parsed.Version);
        Assert.Equal(viaSeparator.ForwardedArguments, parsed.ForwardedArguments);
        Assert.Equal([args[1]], parsed.ForwardedArguments);
    }

    [Fact]
    public void Bare_version_after_a_pinned_package_keeps_the_pin_and_forwards()
    {
        var parsed = ArgParser.Parse("stop@1.2.3", "--version");

        Assert.True(parsed.Success);
        Assert.Equal("stop", parsed.PackageId);
        Assert.Equal("1.2.3", parsed.Version);
        Assert.Equal(["--version"], parsed.ForwardedArguments);
    }

    [Fact]
    public void Bare_version_after_other_options_still_forwards_only_the_flag()
    {
        var parsed = ArgParser.Parse(
            "stop", "--prerelease", "--source", "https://feed.example", "--version");

        Assert.True(parsed.Success);
        Assert.False(parsed.ShowVersion);
        Assert.Equal("stop", parsed.PackageId);
        Assert.Null(parsed.Version);
        Assert.True(parsed.Prerelease);
        Assert.Equal(["https://feed.example"], parsed.Sources);
        Assert.Equal(["--version"], parsed.ForwardedArguments);
    }

    [Fact]
    public void Version_value_after_a_package_still_selects_that_version()
    {
        var parsed = ArgParser.Parse("stop", "--version", "1.2.3", "--", "--version");

        Assert.True(parsed.Success);
        Assert.Equal("stop", parsed.PackageId);
        Assert.Equal("1.2.3", parsed.Version);
        Assert.Equal(["--version"], parsed.ForwardedArguments);
    }

    [Fact]
    public void Second_bare_version_is_forwarded_when_the_first_already_has_a_value()
    {
        var parsed = ArgParser.Parse("stop", "--version", "1.2.3", "--version");

        Assert.True(parsed.Success);
        Assert.Equal("1.2.3", parsed.Version);
        Assert.Equal(["--version"], parsed.ForwardedArguments);
    }

    [Theory]
    [InlineData("--yes", "--version")]
    [InlineData("--update", "--version")]
    [InlineData("--update", "1.2.3", "--version")]
    public void Bare_version_without_a_package_still_requires_a_value(params string[] args)
    {
        var parsed = ArgParser.Parse(args);

        Assert.False(parsed.Success);
        Assert.False(parsed.ShowVersion);
        Assert.Contains("Missing value for --version", parsed.Error);
    }

    [Fact]
    public void Version_followed_by_another_token_is_still_that_tokens_value()
    {
        var parsed = ArgParser.Parse("stop", "--version", "--help");

        Assert.False(parsed.Success);
        Assert.Contains("Invalid version '--help'", parsed.Error);
    }

    [Fact]
    public void Empty_version_assignment_is_still_missing_a_value()
    {
        var parsed = ArgParser.Parse("stop", "--version=");

        Assert.False(parsed.Success);
        Assert.Contains("Missing value for --version", parsed.Error);
    }

    [Fact]
    public void Update_alone_is_a_self_update_to_latest()
    {
        var parsed = ArgParser.Parse("--update");

        Assert.True(parsed.Success);
        Assert.True(parsed.Update);
        Assert.Null(parsed.PackageId);
        Assert.Null(parsed.Version);
        Assert.Empty(parsed.ForwardedArguments);
    }

    [Theory]
    [InlineData("--update", "1.2.3")]
    [InlineData("--update", "v1.2.3")]
    [InlineData("--update=1.2.3")]
    [InlineData("--update=v1.2.3")]
    [InlineData("--update", "--version", "1.2.3")]
    [InlineData("--version", "1.2.3", "--update")]
    public void Update_accepts_an_optional_version(params string[] args)
    {
        var parsed = ArgParser.Parse(args);

        Assert.True(parsed.Success);
        Assert.True(parsed.Update);
        Assert.Null(parsed.PackageId);
        Assert.Equal("1.2.3", parsed.Version);
    }

    [Theory]
    [InlineData("--update", "ci")]
    [InlineData("--update", "CI")]
    [InlineData("--update=ci")]
    [InlineData("--update=vci")]
    public void Update_accepts_the_ci_channel(params string[] args)
    {
        var parsed = ArgParser.Parse(args);

        Assert.True(parsed.Success);
        Assert.True(parsed.Update);
        Assert.Null(parsed.PackageId);
        Assert.Equal(SelfUpdate.CiChannel, parsed.Version);
    }

    [Fact]
    public void Update_rejects_a_package_identity()
    {
        var parsed = ArgParser.Parse("--update", "acme.tool");

        Assert.False(parsed.Success);
        Assert.Contains("package identity", parsed.Error);
        Assert.Contains("acme.tool", parsed.Error);
    }

    [Fact]
    public void Update_rejects_package_at_version()
    {
        var parsed = ArgParser.Parse("--update", "acme.tool@1.0.0");

        Assert.False(parsed.Success);
        Assert.Contains("package identity", parsed.Error);
    }

    [Fact]
    public void Update_rejects_extra_operands()
    {
        var parsed = ArgParser.Parse("--update", "1.2.3", "extra");

        Assert.False(parsed.Success);
        Assert.Contains("Unexpected arguments", parsed.Error);
    }

    [Fact]
    public void Update_rejects_conflicting_versions()
    {
        var parsed = ArgParser.Parse("--update", "1.2.3", "--version", "4.5.6");

        Assert.False(parsed.Success);
        Assert.Contains("multiple versions", parsed.Error);
    }
}
