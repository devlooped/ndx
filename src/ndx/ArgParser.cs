namespace ndx;

/// <summary>
/// dnx.cmd-compatible argv split: first operand is PACKAGE[@VERSION], listed
/// flags are consumed by ndx, everything else (including tokens after --) is
/// forwarded to the child. <c>--update [VERSION]</c> is a standalone self-update.
/// A lone <c>--version</c> prints the ndx version. After a package id, a
/// <c>--version</c> with no value is forwarded to the tool, as in
/// <c>ndx stop -- --version</c>.
/// </summary>
public static class ArgParser
{
    static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--prerelease",
        "--yes",
        "-y",
        "--allow-roll-forward",
        "--disable-parallel",
        "--ignore-failed-sources",
        "--no-http-cache",
        "--interactive",
        "--no-cache",
    };

    static readonly HashSet<string> Valued = new(StringComparer.OrdinalIgnoreCase)
    {
        "--source",
        "--add-source",
        "--configfile",
        "--version",
        "--verbosity",
        "-v",
    };

    public static Invocation Parse(params string[] args) => Parse((IReadOnlyList<string>)args);

    public static Invocation Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 1 && IsBareVersionFlag(args[0]))
            return Invocation.ToolVersion();

        string? packageId = null;
        string? identityVersion = null;
        string? versionOption = null;
        string? updateVersion = null;
        string? configFile = null;
        string? verbosity = null;
        var update = false;
        var prerelease = false;
        var yes = false;
        var allowRollForward = false;
        var disableParallel = false;
        var ignoreFailedSources = false;
        var noHttpCache = false;
        var interactive = false;
        var sources = new List<string>();
        var addSources = new List<string>();
        var forwarded = new List<string>();
        var terminated = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            if (terminated)
            {
                forwarded.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                terminated = true;
                continue;
            }

            if (arg is "-h" or "--help")
            {
                if (packageId is null)
                    return Invocation.Help();

                forwarded.Add(arg);
                continue;
            }

            var (option, inline) = SplitOption(arg);
            if (option is not null && option.Equals("--update", StringComparison.OrdinalIgnoreCase))
            {
                update = true;
                if (inline is not null)
                {
                    if (updateVersion is not null)
                        return Invocation.Failed("Cannot specify --update version more than once.");

                    updateVersion = NormalizeUpdateVersion(inline);
                    if (updateVersion.Length == 0)
                        return Invocation.Failed("Missing value for --update.");
                }

                continue;
            }
            if (option is not null && Valued.Contains(option))
            {
                var value = inline;
                if (value is null)
                {
                    if (i + 1 >= args.Count)
                    {
                        // `ndx stop --version` names the tool, not a package version.
                        // A following token is still the value (`--version 1.2.3`,
                        // or an invalid one such as `--version --help`).
                        if (IsVersionOption(option) && packageId is not null && !update)
                        {
                            forwarded.Add(arg);
                            continue;
                        }

                        return Invocation.Failed($"Missing value for {option}.");
                    }

                    value = args[++i];
                }

                if (value.Length == 0)
                    return Invocation.Failed($"Missing value for {option}.");

                switch (option.ToLowerInvariant())
                {
                    case "--source":
                        sources.Add(value);
                        break;
                    case "--add-source":
                        addSources.Add(value);
                        break;
                    case "--configfile":
                        configFile = value;
                        break;
                    case "--version":
                        versionOption = value;
                        break;
                    case "--verbosity":
                    case "-v":
                        verbosity = value;
                        break;
                }

                continue;
            }

            if (option is not null && Flags.Contains(option))
            {
                switch (option.ToLowerInvariant())
                {
                    case "--prerelease":
                        prerelease = true;
                        break;
                    case "--yes":
                    case "-y":
                        yes = true;
                        break;
                    case "--allow-roll-forward":
                        allowRollForward = true;
                        break;
                    case "--disable-parallel":
                        disableParallel = true;
                        break;
                    case "--ignore-failed-sources":
                        ignoreFailedSources = true;
                        break;
                    case "--no-http-cache":
                    case "--no-cache":
                        noHttpCache = true;
                        break;
                    case "--interactive":
                        interactive = true;
                        break;
                }

                continue;
            }

            if (arg.StartsWith('-') && packageId is null)
                return Invocation.Failed($"Unrecognized option '{arg}'.");

            if (packageId is null)
            {
                if (!TryParseIdentity(arg, out packageId, out identityVersion, out var identityError))
                    return Invocation.Failed(identityError!);

                continue;
            }

            forwarded.Add(arg);
        }

        if (update)
            return FinishUpdate(packageId, identityVersion, versionOption, updateVersion, forwarded, verbosity);

        if (packageId is null)
        {
            return Invocation.Failed(
                "Required argument missing: specify PACKAGE_NAME or PACKAGE_NAME@VERSION.");
        }

        if (identityVersion is not null && versionOption is not null)
        {
            return Invocation.Failed(
                "Cannot specify a version in the package identity and also with --version.");
        }

        var version = identityVersion ?? versionOption;
        if (version is not null && prerelease)
        {
            return Invocation.Failed(
                $"The --prerelease option cannot be used with a specific version ({version}).");
        }

        if (version is not null && !PackageVersion.TryParse(version, out _) && !VersionRange.TryParse(version, out _))
            return Invocation.Failed($"Invalid version '{version}'.");

        return new Invocation
        {
            Success = true,
            PackageId = packageId,
            Version = version,
            Prerelease = prerelease,
            Yes = yes,
            AllowRollForward = allowRollForward,
            DisableParallel = disableParallel,
            IgnoreFailedSources = ignoreFailedSources,
            NoHttpCache = noHttpCache,
            Interactive = interactive,
            ConfigFile = configFile,
            Verbosity = verbosity,
            Sources = sources,
            AddSources = addSources,
            ForwardedArguments = forwarded,
        };
    }

    static Invocation FinishUpdate(
        string? packageId,
        string? identityVersion,
        string? versionOption,
        string? updateVersion,
        List<string> forwarded,
        string? verbosity)
    {
        if (forwarded.Count > 0)
            return Invocation.Failed("Unexpected arguments for --update.");

        if (identityVersion is not null)
        {
            return Invocation.Failed(
                "--update cannot be combined with a package identity.");
        }

        var versions = new List<string>();
        if (updateVersion is not null)
            versions.Add(updateVersion);
        if (versionOption is not null)
            versions.Add(NormalizeUpdateVersion(versionOption));

        if (packageId is not null)
        {
            var fromOperand = NormalizeUpdateVersion(packageId);
            if (!IsUpdateTarget(fromOperand))
            {
                return Invocation.Failed(
                    $"--update cannot be combined with a package identity. Unexpected '{packageId}'.");
            }

            versions.Add(fromOperand);
        }

        var distinct = versions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count > 1)
            return Invocation.Failed("Cannot specify multiple versions for --update.");

        var version = distinct.Count == 0 ? null : distinct[0];
        if (version is { Length: 0 })
            return Invocation.Failed("Missing value for --update.");

        if (version is not null && !IsUpdateTarget(version))
            return Invocation.Failed($"Invalid version '{version}'.");

        return new Invocation
        {
            Success = true,
            Update = true,
            Version = version,
            Verbosity = verbosity,
        };
    }

    static string NormalizeUpdateVersion(string value)
    {
        var text = value.Trim();
        if (text.Length > 0 && (text[0] is 'v' or 'V'))
            text = text[1..];
        return SelfUpdate.IsCiChannel(text) ? SelfUpdate.CiChannel : text;
    }

    static bool IsUpdateTarget(string version)
        => SelfUpdate.IsCiChannel(version) || PackageVersion.TryParse(version, out _);

    static bool TryParseIdentity(string token, out string? packageId, out string? version, out string? error)
    {
        packageId = null;
        version = null;
        error = null;

        var at = token.IndexOf('@');
        if (at < 0)
        {
            if (token.Length == 0)
            {
                error = "Package identity is empty.";
                return false;
            }

            packageId = token;
            return true;
        }

        packageId = token[..at];
        version = token[(at + 1)..];
        if (packageId.Length == 0)
        {
            error = "Package identity is missing a package id.";
            return false;
        }

        if (version.Length == 0)
        {
            version = null;
        }

        return true;
    }

    static bool IsBareVersionFlag(string token)
    {
        var (option, inline) = SplitOption(token);
        return IsVersionOption(option) && inline is null;
    }

    static bool IsVersionOption(string? option)
        => option is not null && option.Equals("--version", StringComparison.OrdinalIgnoreCase);

    static (string? Name, string? InlineValue) SplitOption(string token)
    {
        if (token.Length < 2 || token[0] != '-')
            return (null, null);

        var separator = token.IndexOfAny(['=', ':']);
        if (separator < 0)
            return (token, null);

        return (token[..separator], token[(separator + 1)..]);
    }
}
