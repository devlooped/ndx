namespace ndx;

/// <summary>
/// Result of splitting argv into ndx options versus child arguments.
/// </summary>
public sealed record Invocation
{
    public bool Success { get; init; }
    public bool ShowHelp { get; init; }
    public bool ShowVersion { get; init; }
    public bool Update { get; init; }
    public bool Install { get; init; }
    public string? Error { get; init; }

    public string? PackageId { get; init; }
    public string? Version { get; init; }
    public bool Prerelease { get; init; }
    public bool Yes { get; init; }
    public bool AllowRollForward { get; init; }
    public bool DisableParallel { get; init; }
    public bool IgnoreFailedSources { get; init; }
    public bool NoHttpCache { get; init; }
    public bool Interactive { get; init; }
    public string? ConfigFile { get; init; }
    public string? Verbosity { get; init; }
    public IReadOnlyList<string> Sources { get; init; } = [];
    public IReadOnlyList<string> AddSources { get; init; } = [];
    public IReadOnlyList<string> ForwardedArguments { get; init; } = [];

    public static Invocation Failed(string error) => new()
    {
        Success = false,
        Error = error
    };

    public static Invocation Help() => new()
    {
        Success = true,
        ShowHelp = true
    };

    public static Invocation ToolVersion() => new()
    {
        Success = true,
        ShowVersion = true
    };
}
