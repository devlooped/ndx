# Install ndx.
# The script is published on GitHub Releases. The binary comes from the
# nuget.org RID package; the blob feed is used when nuget.org is unreachable.
#   irm https://github.com/devlooped/ndx/releases/latest/download/install.ps1 | iex
# Env / flags: NDX_VERSION, NDX_PREFIX, NDX_ARCHIVE, NDX_RID, NDX_REPO, NDX_SKIP_PATH
#              NDX_NUGET_FLAT, NDX_NUGET_REG, NDX_BLOB_FLAT
# Also accepts --version --prefix --archive --rid --repo --skip-path

$ErrorActionPreference = 'Stop'

$Repo = if ($env:NDX_REPO) { $env:NDX_REPO } else { 'devlooped/ndx' }
$Version = $env:NDX_VERSION
$Prefix = $env:NDX_PREFIX
$Archive = $env:NDX_ARCHIVE
$Rid = $env:NDX_RID
$SkipPath = $env:NDX_SKIP_PATH -eq '1'
$NugetFlat = if ($env:NDX_NUGET_FLAT) { $env:NDX_NUGET_FLAT } else { 'https://api.nuget.org/v3-flatcontainer' }
$NugetReg = if ($env:NDX_NUGET_REG) { $env:NDX_NUGET_REG } else { 'https://api.nuget.org/v3/registration5-gz-semver2' }
$BlobFlat = if ($env:NDX_BLOB_FLAT) { $env:NDX_BLOB_FLAT } else { 'https://kzu.blob.core.windows.net/nuget/flatcontainer' }

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

function Get-NdxHttp {
    if (-not $script:NdxClient) {
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        } catch {}
        Add-Type -AssemblyName System.Net.Http
        $handler = [System.Net.Http.HttpClientHandler]::new()
        $handler.AutomaticDecompression = [System.Net.DecompressionMethods]::GZip -bor [System.Net.DecompressionMethods]::Deflate
        $script:NdxClient = [System.Net.Http.HttpClient]::new($handler)
        $script:NdxClient.Timeout = [TimeSpan]::FromMinutes(5)
        $script:NdxClient.DefaultRequestHeaders.UserAgent.ParseAdd('ndx')
    }
    return $script:NdxClient
}

function Save-NdxUrl([string]$url, [string]$dest) {
    $client = Get-NdxHttp
    try {
        $resp = $client.GetAsync($url).GetAwaiter().GetResult()
    } catch {
        return $false
    }
    try {
        if (-not $resp.IsSuccessStatusCode) { return $false }
        $fs = [IO.File]::Create($dest)
        try {
            $stream = $resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            try { $stream.CopyTo($fs) } finally { $stream.Dispose() }
        } finally { $fs.Dispose() }
        return $true
    } finally {
        $resp.Dispose()
    }
}

function Get-NdxText([string]$url) {
    $client = Get-NdxHttp
    try {
        $resp = $client.GetAsync($url).GetAwaiter().GetResult()
    } catch {
        return $null
    }
    try {
        if (-not $resp.IsSuccessStatusCode) { return $null }
        return $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    } finally {
        $resp.Dispose()
    }
}

function Get-NdxLatestStable([string]$flat, [string]$id) {
    $text = Get-NdxText ("{0}/{1}/index.json" -f $flat.TrimEnd('/'), $id.ToLowerInvariant())
    if (-not $text) { return $null }
    try { $obj = $text | ConvertFrom-Json } catch { return $null }
    $best = $null
    $bestText = $null
    foreach ($v in @($obj.versions)) {
        if (-not $v -or "$v".Contains('-')) { continue }
        try { $parsed = [version]$v } catch { continue }
        if ($null -eq $best -or $parsed -gt $best) {
            $best = $parsed
            $bestText = [string]$v
        }
    }
    return $bestText
}

function Get-NdxCatalogHash([string]$reg, [string]$id, [string]$ver) {
    $leafText = Get-NdxText ("{0}/{1}/{2}.json" -f $reg.TrimEnd('/'), $id, $ver)
    if (-not $leafText) { return $null }
    try { $leaf = $leafText | ConvertFrom-Json } catch { return $null }
    $catalog = $leaf.catalogEntry
    if ($catalog -isnot [string]) {
        if ($null -eq $catalog) { return $null }
        $catalog = $catalog.'@id'
    }
    if (-not $catalog) { return $null }
    $entryText = Get-NdxText ([string]$catalog)
    if (-not $entryText) { return $null }
    try { $entry = $entryText | ConvertFrom-Json } catch { return $null }
    $algo = [string]$entry.packageHashAlgorithm
    if ($algo -and $algo -ne 'SHA512') { return $null }
    $hash = [string]$entry.packageHash
    if (-not $hash) { return $null }
    return $hash.Trim()
}

function Get-NdxSha512([string]$path) {
    $sha = [Security.Cryptography.SHA512]::Create()
    try {
        $fs = [IO.File]::OpenRead($path)
        try { return [Convert]::ToBase64String($sha.ComputeHash($fs)) }
        finally { $fs.Dispose() }
    } finally {
        $sha.Dispose()
    }
}

function Save-NdxRidPackage([string]$id, [string]$ver, [string]$dest) {
    $idL = $id.ToLowerInvariant()
    $verL = $ver.ToLowerInvariant()
    $rel = "$idL/$verL/$idL.$verL.nupkg"
    if (Save-NdxUrl ("{0}/{1}" -f $NugetFlat.TrimEnd('/'), $rel) $dest) {
        $expected = Get-NdxCatalogHash $NugetReg $idL $verL
        if (-not $expected) {
            Remove-Item -LiteralPath $dest -Force -ErrorAction SilentlyContinue
        } else {
            $actual = Get-NdxSha512 $dest
            if ($actual -ne $expected) {
                throw "ndx: SHA512 mismatch for $idL.$verL.nupkg`n  expected: $expected`n  actual:   $actual"
            }
            return $true
        }
    }
    return (Save-NdxUrl ("{0}/{1}" -f $BlobFlat.TrimEnd('/'), $rel) $dest)
}

function Expand-NdxPackage([string]$nupkg, [string]$destFile, [string]$entryName) {
    if (-not ('System.IO.Compression.ZipFile' -as [type])) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    }
    if (-not ('System.IO.Compression.ZipFile' -as [type])) {
        Add-Type -AssemblyName System.IO.Compression
    }
    $zip = [IO.Compression.ZipFile]::OpenRead($nupkg)
    try {
        $entry = $null
        foreach ($item in $zip.Entries) {
            if ($item.FullName.Replace('\', '/') -eq $entryName) {
                $entry = $item
                break
            }
        }
        if (-not $entry) { throw "ndx: package did not contain $entryName" }
        $out = [IO.File]::Create($destFile)
        try {
            $input = $entry.Open()
            try { $input.CopyTo($out) } finally { $input.Dispose() }
        } finally { $out.Dispose() }
    } finally {
        $zip.Dispose()
    }
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
$Rid = $Rid.ToLowerInvariant()

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
    $extract = Join-Path $tmp 'extract'
    New-Item -ItemType Directory -Path $extract | Out-Null
    $fromPackage = $false
    if (-not $Archive) {
        $pkg = "ndx.$Rid"
        if ($Version -and $Version.ToLowerInvariant() -eq 'ci') {
            $tag = 'ci'
            $resolved = 'ci'
            $name = "ndx-$resolved-$Rid.$ext"
            $base = "https://github.com/$Repo/releases/download/$tag"
            $Archive = Join-Path $tmp $name
            Invoke-WebRequest -Uri "$base/$name" -OutFile $Archive
            Invoke-WebRequest -Uri "$base/$name.sha256" -OutFile "$Archive.sha256"
        } else {
            # GitHub's unauthenticated releases API returns 403 once the hourly
            # quota is spent. The RID package on nuget.org is the same binary.
            if ($Version) {
                $resolved = $Version
                if ($resolved.StartsWith('v') -or $resolved.StartsWith('V')) {
                    $resolved = $resolved.Substring(1)
                }
            } else {
                $resolved = Get-NdxLatestStable $NugetFlat $pkg
                if (-not $resolved) { $resolved = Get-NdxLatestStable $BlobFlat $pkg }
                if (-not $resolved) { throw "ndx: could not resolve the latest stable version of $pkg" }
            }

            $nupkg = Join-Path $tmp "$pkg.$resolved.nupkg"
            if (-not (Save-NdxRidPackage $pkg $resolved $nupkg)) {
                throw "ndx: could not download $pkg $resolved"
            }
            Expand-NdxPackage $nupkg (Join-Path $extract $binary) "tools/any/$Rid/$binary"
            $fromPackage = $true
        }
    }

    if (-not $fromPackage) {
        if (Test-Path "$Archive.sha256") {
            $expected = ((Get-Content -Raw "$Archive.sha256").Trim() -split '\s+')[0].ToLowerInvariant()
            $actual = (Get-FileHash -Algorithm SHA256 -Path $Archive).Hash.ToLowerInvariant()
            if ($actual -ne $expected) {
                throw "ndx: SHA256 mismatch for $(Split-Path $Archive -Leaf)`n  expected: $expected`n  actual:   $actual"
            }
        }

        if ($windows) {
            Expand-Archive -Path $Archive -DestinationPath $extract -Force
        } else {
            tar -xzf $Archive -C $extract
        }
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
    if ($script:NdxClient) { $script:NdxClient.Dispose() }
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
