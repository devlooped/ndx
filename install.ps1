# Install ndx from GitHub Releases.
#   irm https://github.com/devlooped/ndx/releases/latest/download/install.ps1 | iex
# Env / flags: NDX_VERSION, NDX_PREFIX, NDX_ARCHIVE, NDX_RID, NDX_REPO, NDX_SKIP_PATH
# Also accepts --version --prefix --archive --rid --repo --skip-path

$ErrorActionPreference = 'Stop'

$Repo = if ($env:NDX_REPO) { $env:NDX_REPO } else { 'devlooped/ndx' }
$Version = $env:NDX_VERSION
$Prefix = $env:NDX_PREFIX
$Archive = $env:NDX_ARCHIVE
$Rid = $env:NDX_RID
$SkipPath = $env:NDX_SKIP_PATH -eq '1'

function Get-NdxRuntimeIdentifier {
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    $archName = switch ($arch) {
        'X64' { 'x64' }
        'Arm64' { 'arm64' }
        default {
            throw "ndx: unsupported architecture '$arch'"
        }
    }

    if ($IsWindows -or $env:OS -eq 'Windows_NT') {
        return "win-$archName"
    }

    if (Get-Variable IsMacOS -ErrorAction SilentlyContinue) {
        if ($IsMacOS) { return "osx-$archName" }
        if ($IsLinux) {
            # Same signals as install.sh: Alpine, or musl's loader. gcompat does not hide it.
            $musl = (Test-Path -LiteralPath '/etc/alpine-release') -or
                (@(Get-ChildItem -Path '/lib' -Filter 'ld-musl-*.so*' -ErrorAction SilentlyContinue).Count -gt 0)
            if (-not $musl -and (Get-Command ldd -ErrorAction SilentlyContinue)) {
                $musl = ((& ldd --version 2>&1 | Out-String) -match 'musl')
            }
            if ($musl) { return "linux-musl-$archName" }
            return "linux-$archName"
        }
    }

    throw "ndx: unsupported OS"
}

function Send-EnvironmentChange {
    if (-not ($IsWindows -or $env:OS -eq 'Windows_NT')) {
        return
    }

    if (-not ('Win32.NativeBroadcast' -as [type])) {
        Add-Type -Namespace Win32 -Name NativeBroadcast -MemberDefinition @"
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
public static extern IntPtr SendMessageTimeout(
    IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam,
    uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
"@
    }

    $result = [UIntPtr]::Zero
    [void][Win32.NativeBroadcast]::SendMessageTimeout(
        [IntPtr]0xffff,
        0x1a,
        [UIntPtr]::Zero,
        'Environment',
        2,
        5000,
        [ref]$result)
}

function Add-NdxToUserPath([string]$dir) {
    $parts = [Environment]::GetEnvironmentVariable('Path', 'User')
    if (-not $parts) { $parts = '' }
    $entries = $parts.Split([char]';', [StringSplitOptions]::RemoveEmptyEntries)
    $env:Path = "$dir;$env:Path"
    if ($entries -contains $dir) {
        return
    }

    $updated = if ($parts) { "$parts;$dir" } else { $dir }
    [Environment]::SetEnvironmentVariable('Path', $updated, 'User')
    Send-EnvironmentChange
    Write-Host "added $dir to the user PATH"
}

for ($i = 0; $i -lt $args.Count; $i++) {
    switch -Regex ($args[$i]) {
        '^--version$|^-Version$' { $Version = $args[++$i]; continue }
        '^--prefix$|^-Prefix$' { $Prefix = $args[++$i]; continue }
        '^--archive$|^-Archive$' { $Archive = $args[++$i]; continue }
        '^--rid$|^-Rid$' { $Rid = $args[++$i]; continue }
        '^--repo$|^-Repo$' { $Repo = $args[++$i]; continue }
        '^--skip-path$|^-SkipPath$' { $SkipPath = $true; continue }
        default { throw "ndx: unrecognized argument '$($args[$i])'" }
    }
}

if (-not $Rid) {
    $Rid = Get-NdxRuntimeIdentifier
}

$windows = $Rid.StartsWith('win', [StringComparison]::OrdinalIgnoreCase)
$binary = if ($windows) { 'ndx.exe' } else { 'ndx' }
$ext = if ($windows) { 'zip' } else { 'tar.gz' }

if (-not $Prefix) {
    $Prefix = if ($windows) {
        Join-Path $env:LOCALAPPDATA 'ndx'
    } else {
        Join-Path $HOME '.local/bin'
    }
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("ndx-install-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    if (-not $Archive) {
        if ($Version) {
            if ($Version -eq 'ci') {
                $tag = 'ci'
                $resolved = 'ci'
            } elseif ($Version.StartsWith('v')) {
                $tag = $Version
                $resolved = $tag.TrimStart('v')
            } else {
                $tag = "v$Version"
                $resolved = $Version
            }
        } else {
            $release = Invoke-RestMethod -Headers @{ Accept = 'application/vnd.github+json' } `
                -Uri "https://api.github.com/repos/$Repo/releases/latest"
            $tag = $release.tag_name
            if (-not $tag) { throw "ndx: could not resolve latest release of $Repo" }
            $resolved = $tag.TrimStart('v')
        }

        $name = "ndx-$resolved-$Rid.$ext"
        $base = "https://github.com/$Repo/releases/download/$tag"
        $Archive = Join-Path $tmp $name
        Invoke-WebRequest -Uri "$base/$name" -OutFile $Archive
        Invoke-WebRequest -Uri "$base/$name.sha256" -OutFile "$Archive.sha256"
    }

    if (Test-Path "$Archive.sha256") {
        $expected = ((Get-Content -Raw "$Archive.sha256").Trim() -split '\s+')[0].ToLowerInvariant()
        $actual = (Get-FileHash -Algorithm SHA256 -Path $Archive).Hash.ToLowerInvariant()
        if ($actual -ne $expected) {
            throw "ndx: SHA256 mismatch for $(Split-Path $Archive -Leaf)`n  expected: $expected`n  actual:   $actual"
        }
    }

    $extract = Join-Path $tmp 'extract'
    New-Item -ItemType Directory -Path $extract | Out-Null
    if ($windows) {
        Expand-Archive -Path $Archive -DestinationPath $extract -Force
    } else {
        tar -xzf $Archive -C $extract
    }

    $source = Join-Path $extract $binary
    if (-not (Test-Path $source)) {
        throw "ndx: archive did not contain $binary"
    }

    New-Item -ItemType Directory -Force -Path $Prefix | Out-Null
    $dest = Join-Path $Prefix $binary
    Copy-Item -Force -Path $source -Destination $dest
    Write-Host "installed $dest"

    if (-not $SkipPath) {
        Add-NdxToUserPath $Prefix
    }
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
