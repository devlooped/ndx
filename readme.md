<p align="center">
  <img src="assets/icon.svg" alt="ndx" width="256">
</p>

<p align="center">
  <a href="https://github.com/devlooped/ndx/releases"><img src="https://img.shields.io/github/v/release/devlooped/ndx?include_prereleases&color=green" alt="Release"></a>
  <a href="https://www.nuget.org/packages/ndx"><img src="https://img.shields.io/nuget/v/ndx.svg?color=royalblue" alt="Version"></a>
  <a href="https://github.com/devlooped/oss/blob/main/license.txt"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="License"></a>
</p>

<!-- #content -->
`ndx` (*n*ative *d*otnet e*x*ecute) is [`dnx`](https://learn.microsoft.com/dotnet/core/tools/dotnet-tool-exec) for
native tools packaged and distributed as NuGet packages. Same
`PACKAGE[@VERSION]` identity, same restore flags, same global packages folder.

If you can `dnx stop`, you can `ndx stop`. `dotnet dnx` and `dotnet tool exec`
are the same command; `ndx` speaks that protocol, including RID-specific Native
AOT tools (`stop`, `go`, `winget`) and ordinary framework-dependent tools
(`dotnetsay`).

A first run downloads into the NuGet cache. A later `ndx tool@version` starts
the cached binary — no SDK, no restore.

## Install

macOS / Linux:

```bash
curl -fsSL https://github.com/devlooped/ndx/releases/latest/download/install.sh | sh
```

Windows (PowerShell):

```powershell
irm https://github.com/devlooped/ndx/releases/latest/download/install.ps1 | iex
```

> Alternatively (perhaps of debatable utility), you can install using the .NET SDK too:
> `dotnet tool install -g ndx`


## Update

Self-update the installed binary (optional version, including downgrades):

```bash
ndx --update
ndx --update 0.1.0
```

## Uninstall

macOS / Linux:

```bash
curl -fsSL https://github.com/devlooped/ndx/releases/latest/download/uninstall.sh | sh
```

Windows (PowerShell):

```powershell
irm https://github.com/devlooped/ndx/releases/latest/download/uninstall.ps1 | iex
```

## Usage
*ndx*

Same shape as `dnx` / `dotnet dnx` / `dotnet tool exec`:

```bash
dnx stop -- --help
ndx stop -- --help

dnx stop@2.1.0 -- --help
ndx stop@2.1.0 -- --help

dnx dotnetsay@1.0.0 -- Hello
ndx dotnetsay@1.0.0 -- Hello
```

Pin a feed the same way (here the CI feed used for the timings below):

```bash
dnx --source https://kzu.blob.core.windows.net/nuget/index.json stop@2.1.0 -- --help
ndx --source https://kzu.blob.core.windows.net/nuget/index.json stop@2.1.0 -- --help
```

`stop` is a Native AOT RID tool. `dotnetsay` is a classic framework-dependent
tool. Both are just NuGet packages; ndx picks the host RID and starts
`Runner=executable` binaries directly, or `dotnet exec` for `Runner=dotnet`.

An exact version already in the cache (`NUGET_PACKAGES`, then
`nuget.config`'s `globalPackagesFolder`, then `~/.nuget/packages`) is not
downloaded again. A first `tool@version` run builds the nupkg URL from the
service index (cached for the process) and lists versions only if that GET
is 404. Packages written by `dnx` or `dotnet restore` are reused; packages
written by ndx are visible to them.

Shared flags: `--source`, `--add-source`, `--configfile`, `--version`,
`--prerelease`, `--yes`/`-y`, `--allow-roll-forward`, `--verbosity`/`-v`,
`--disable-parallel`, `--ignore-failed-sources`, `--no-http-cache`,
`--interactive`.

## Startup time

Cold is empty-cache download → start; cached is start only (`tool@version` so
neither runner resolves latest). linux-x64 (WSL2) is SDK 10.0.110; win-x64 is
SDK 10.0.303.

| Tool | Runner | Cold linux-x64 | Cold win-x64 | Cached linux-x64 | Cached win-x64 |
| --- | --- | ---: | ---: | ---: | ---: |
| `stop@2.1.0` (native AOT) | `dnx` | 2.2 s | 2.1 s | 580 ms | 520 ms |
| | `ndx` | **1.2 s** | **1.5 s** | **10 ms** | **34 ms** |
| `dotnetsay@1.0.0` (framework-dependent) | `dnx` | 1.6 s | 1.8 s | 570 ms | 545 ms |
| | `ndx` | **849 ms** | **1.1 s** | **12 ms** | **50 ms** |
| `winget` (native AOT TUI, Windows) | `dnx` | — | 2.7 s | — | 820 ms |
| | `ndx` | — | **1.2 s** | — | **275 ms** |

Isolated `NUGET_PACKAGES`. `stop` / `dotnetsay` from
`https://kzu.blob.core.windows.net/nuget/index.json`; `winget` from nuget.org.
Cold is the median of 3 empty-cache runs. Cached is the median of 10 runs after
one seed. `stop` invoked as `-- --help`; `dotnetsay` as `-- ndx`. `winget` is
a TUI so it does not exit on its own: cold is `winget` (latest), cached is
`winget@0.13.2`; each run waits until `winget-tui` is up, then `ndx stop@2.1.0`
signals that PID and the clock stops when the runner exits. `dnx` is the SDK
script (`dotnet dnx`). `winget` timings are win-x64 SDK 11.0.100-preview.

## Evergreen

Unlike `dnx`, omitting the version is `@*` — not a one-shot latest.
`ndx my-agent` and `ndx my-agent@*` are the same: start the latest match,
and while that process is still running, watch the feed, download a newer
matching version *before* stopping the child, then send SIGINT / Ctrl+C
(and `WM_CLOSE` if the tool is a GUI) and restart.

`@*-*` includes prereleases. `@1.*` / `@1.1.*` pin a major or minor the
GitHub Actions way (float the rest). A NuGet range like `[1.0,2.0)` works
too. One-shot tools still exit as soon as the child exits; the watch loop
only continues while the child is alive. Pin an exact version
(`tool@2.1.0`) to disable it.

```bash
ndx my-agent
ndx my-agent@*
ndx my-agent@1.*
ndx my-agent@1.1.*
ndx my-agent@[1.0,2.0)
```

That's the point for anything that is supposed to keep running: a daemon,
an agent, an MCP server, a watcher, a local gateway. You start it once.
When a newer matching package lands on the feed, ndx stages the bits, asks
the current process to exit cleanly, and starts the new one. No
`dotnet tool update -g`, no unit file to bounce, no cron that reinstalls.
The running process is always the latest version the range allows, so an
agent picks up new tools and bugfixes, a daemon picks up a patched build,
without anyone being there to restart it.

The poll interval defaults to 5 seconds and can be set in `.netconfig`:

```
[ndx]
    interval = 5
```

ndx walks from the working directory up, then `~/.netconfig`. `--verbosity
quiet` hides the `Updating …` line.

<!-- #content -->
---
<!-- include https://github.com/devlooped/sponsors/raw/main/footer.md -->
# Sponsors 

<!-- sponsors.md -->
[![Clarius Org](https://avatars.githubusercontent.com/u/71888636?v=4&s=39 "Clarius Org")](https://github.com/clarius)
[![MFB Technologies, Inc.](https://avatars.githubusercontent.com/u/87181630?v=4&s=39 "MFB Technologies, Inc.")](https://github.com/MFB-Technologies-Inc)
[![SandRock](https://avatars.githubusercontent.com/u/321868?u=99e50a714276c43ae820632f1da88cb71632ec97&v=4&s=39 "SandRock")](https://github.com/sandrock)
[![DRIVE.NET, Inc.](https://avatars.githubusercontent.com/u/15047123?v=4&s=39 "DRIVE.NET, Inc.")](https://github.com/drivenet)
[![Keith Pickford](https://avatars.githubusercontent.com/u/16598898?u=64416b80caf7092a885f60bb31612270bffc9598&v=4&s=39 "Keith Pickford")](https://github.com/Keflon)
[![Thomas Bolon](https://avatars.githubusercontent.com/u/127185?u=7f50babfc888675e37feb80851a4e9708f573386&v=4&s=39 "Thomas Bolon")](https://github.com/tbolon)
[![Reuben Swartz](https://avatars.githubusercontent.com/u/724704?u=2076fe336f9f6ad678009f1595cbea434b0c5a41&v=4&s=39 "Reuben Swartz")](https://github.com/rbnswartz)
[![Jacob Foshee](https://avatars.githubusercontent.com/u/480334?v=4&s=39 "Jacob Foshee")](https://github.com/jfoshee)
[![](https://avatars.githubusercontent.com/u/33566379?u=bf62e2b46435a267fa246a64537870fd2449410f&v=4&s=39 "")](https://github.com/Mrxx99)
[![Eric Johnson](https://avatars.githubusercontent.com/u/26369281?u=41b560c2bc493149b32d384b960e0948c78767ab&v=4&s=39 "Eric Johnson")](https://github.com/eajhnsn1)
[![Jonathan ](https://avatars.githubusercontent.com/u/5510103?u=98dcfbef3f32de629d30f1f418a095bf09e14891&v=4&s=39 "Jonathan ")](https://github.com/Jonathan-Hickey)
[![Ken Bonny](https://avatars.githubusercontent.com/u/6417376?u=569af445b6f387917029ffb5129e9cf9f6f68421&v=4&s=39 "Ken Bonny")](https://github.com/KenBonny)
[![Simon Cropp](https://avatars.githubusercontent.com/u/122666?v=4&s=39 "Simon Cropp")](https://github.com/SimonCropp)
[![agileworks-eu](https://avatars.githubusercontent.com/u/5989304?v=4&s=39 "agileworks-eu")](https://github.com/agileworks-eu)
[![Zheyu Shen](https://avatars.githubusercontent.com/u/4067473?v=4&s=39 "Zheyu Shen")](https://github.com/arsdragonfly)
[![Vezel](https://avatars.githubusercontent.com/u/87844133?v=4&s=39 "Vezel")](https://github.com/vezel-dev)
[![ChilliCream](https://avatars.githubusercontent.com/u/16239022?v=4&s=39 "ChilliCream")](https://github.com/ChilliCream)
[![4OTC](https://avatars.githubusercontent.com/u/68428092?v=4&s=39 "4OTC")](https://github.com/4OTC)
[![domischell](https://avatars.githubusercontent.com/u/66068846?u=0a5c5e2e7d90f15ea657bc660f175605935c5bea&v=4&s=39 "domischell")](https://github.com/DominicSchell)
[![Adrian Alonso](https://avatars.githubusercontent.com/u/2027083?u=129cf516d99f5cb2fd0f4a0787a069f3446b7522&v=4&s=39 "Adrian Alonso")](https://github.com/adalon)
[![torutek](https://avatars.githubusercontent.com/u/33917059?v=4&s=39 "torutek")](https://github.com/torutek)
[![Ryan McCaffery](https://avatars.githubusercontent.com/u/16667079?u=c0daa64bb5c1b572130e05ae2b6f609ecc912d4d&v=4&s=39 "Ryan McCaffery")](https://github.com/mccaffers)
[![Seika Logiciel](https://avatars.githubusercontent.com/u/2564602?v=4&s=39 "Seika Logiciel")](https://github.com/SeikaLogiciel)
[![Andrew Grant](https://avatars.githubusercontent.com/devlooped-user?s=39 "Andrew Grant")](https://github.com/wizardness)
[![eska-gmbh](https://avatars.githubusercontent.com/devlooped-team?s=39 "eska-gmbh")](https://github.com/eska-gmbh)
[![Geodata AS](https://avatars.githubusercontent.com/u/5946299?v=4&s=39 "Geodata AS")](https://github.com/geodata-no)
[![Jiri Slachta](https://avatars.githubusercontent.com/u/6891947?u=802cfeb13b070d04c53269fc662b0d58963480dd&v=4&s=39 "Jiri Slachta")](https://github.com/jslachta)


<!-- sponsors.md -->
[![Sponsor this project](https://avatars.githubusercontent.com/devlooped-sponsor?s=118 "Sponsor this project")](https://github.com/sponsors/devlooped)

[Learn more about GitHub Sponsors](https://github.com/sponsors)

<!-- https://github.com/devlooped/sponsors/raw/main/footer.md -->
