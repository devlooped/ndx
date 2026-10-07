# Uninstall ndx.
#   irm https://github.com/devlooped/ndx/releases/latest/download/uninstall.ps1 | iex
# Env / flags: NDX_PREFIX, NDX_RID, NDX_SKIP_PATH
# Also accepts --prefix --rid --skip-path
# Also removes leftover ndnx from the pre-rename install location.

$ErrorActionPreference = 'Stop'

$Prefix = $env:NDX_PREFIX
$Rid = $env:NDX_RID
$SkipPath = $env:NDX_SKIP_PATH -eq '1'

function Get-NdxRuntimeIdentifier {
    # PSReadLine 2.0, shipped with Windows PowerShell 5.1, defines its own
    # RuntimeInformation that has no OSArchitecture. Qualifying mscorlib
    # selects the real type there, and PowerShell 7 still type-forwards it.
    $arch = [System.Runtime.InteropServices.RuntimeInformation,mscorlib]::OSArchitecture
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
        if ($IsLinux) { return "linux-$archName" }
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

function Remove-NdxBinary([string]$path) {
    if (Test-Path $path) {
        Remove-Item -Force $path
        Write-Host "removed $path"
    }
}

function Remove-EmptyDir([string]$dir) {
    if ((Test-Path $dir) -and -not @(Get-ChildItem -Force $dir).Count) {
        Remove-Item -Force $dir
    }
}

function Remove-NdxFromUserPath([string]$dir) {
    $parts = [Environment]::GetEnvironmentVariable('Path', 'User')
    if (-not $parts) { $parts = '' }
    $entries = @($parts.Split([char]';', [StringSplitOptions]::RemoveEmptyEntries))
    $kept = @($entries | Where-Object { $_ -ne $dir })

    $session = if ($env:Path) { $env:Path } else { '' }
    $env:Path = (@($session.Split([char]';', [StringSplitOptions]::RemoveEmptyEntries) | Where-Object { $_ -ne $dir }) -join ';')

    if ($kept.Count -eq $entries.Count) {
        return
    }

    $updated = $kept -join ';'
    [Environment]::SetEnvironmentVariable('Path', $updated, 'User')
    Send-EnvironmentChange
    Write-Host "removed $dir from the user PATH"
}

for ($i = 0; $i -lt $args.Count; $i++) {
    switch -Regex ($args[$i]) {
        '^--prefix$|^-Prefix$' { $Prefix = $args[++$i]; continue }
        '^--rid$|^-Rid$' { $Rid = $args[++$i]; continue }
        '^--skip-path$|^-SkipPath$' { $SkipPath = $true; continue }
        default { throw "ndx: unrecognized argument '$($args[$i])'" }
    }
}

if (-not $Rid) {
    $Rid = Get-NdxRuntimeIdentifier
}

$windows = $Rid.StartsWith('win', [StringComparison]::OrdinalIgnoreCase)
$binary = if ($windows) { 'ndx.exe' } else { 'ndx' }
$legacyBinary = if ($windows) { 'ndnx.exe' } else { 'ndnx' }

if (-not $Prefix) {
    $Prefix = if ($windows) {
        Join-Path $env:LOCALAPPDATA 'ndx'
    } else {
        Join-Path $HOME '.local/bin'
    }
}

$legacyPrefix = if ($windows) {
    Join-Path $env:LOCALAPPDATA 'ndnx'
} else {
    Join-Path $HOME '.local/bin'
}

$dest = Join-Path $Prefix $binary
if (Test-Path $dest) {
    Remove-NdxBinary $dest
} else {
    Write-Host "ndx not installed at $dest"
}

Remove-NdxBinary (Join-Path $Prefix $legacyBinary)
if ($Prefix -ne $legacyPrefix) {
    Remove-NdxBinary (Join-Path $legacyPrefix $legacyBinary)
}

Remove-EmptyDir $Prefix
if ($Prefix -ne $legacyPrefix) {
    Remove-EmptyDir $legacyPrefix
}

if (-not $SkipPath) {
    Remove-NdxFromUserPath $Prefix
    if ($Prefix -ne $legacyPrefix) {
        Remove-NdxFromUserPath $legacyPrefix
    }
}
