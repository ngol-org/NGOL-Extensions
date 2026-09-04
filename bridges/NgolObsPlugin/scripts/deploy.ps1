# Put the plugin and everything it loads into the host's per-user plugin folder.
#
# What lands there is what the repository builds, so a hand-copied file cannot drift
# away from the source. The runtime is assembled by scripts/build.ps1 and handed over
# with -NgolRuntime; this script only adds the parts that are specific to this host.
#
# Usage:
#   ./deploy.ps1 -PluginBinary <NgolForObs.dll> -NgolRuntime <repo>\build\runtime
#   ./deploy.ps1 -PluginBinary <...> -Dest <folder> -Port 11156
#   ./deploy.ps1 -PluginBinary <...> -Clean   # also drop node sources this deploy did not place
#
# The host must be closed: it holds the plugin open while it runs.

param(
    [Parameter(Mandatory = $true)][string]$PluginBinary,
    [string]$Dest = "",
    [string]$NgolRuntime = "",
    [int]$Port = 11156,
    [switch]$NodesOnly,
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$bridgeRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $bridgeRoot "..\..")).Path

# Read out of the host's own source rather than from a guide: on Windows the host looks
# under the machine-wide data folder (CSIDL_COMMON_APPDATA), not the roaming user one.
# Guides that say the roaming folder describe an older version, and the plugin is then
# never scanned at all.
if (-not $Dest) {
    $Dest = Join-Path $env:ProgramData "obs-studio\plugins\NgolForObs"
}
$ngolDir = Join-Path $Dest "ngol"
$binDir = Join-Path $Dest "bin\64bit"

# The host keeps its own plugins loaded for the whole session, so an overwrite while it
# runs fails halfway and leaves a folder that is neither the old nor the new one.
# Node sources are picked up while it runs, so only a full deploy has to wait.
$running = Get-Process -Name "obs64" -ErrorAction SilentlyContinue
if ($running -and -not $NodesOnly) {
    throw "the host is running (pid $($running.Id -join ', ')); close it before deploying"
}

New-Item -ItemType Directory -Path $binDir -Force | Out-Null
New-Item -ItemType Directory -Path $ngolDir -Force | Out-Null


# NGOL 一式をどこから取るか。-NgolRuntime を渡せば、そこにあるものをそのまま置く。
# 渡さなければリポジトリから組む。別のリポジトリから配る場合や、組む手順が
# こちらと違う場合は、組んだ結果を渡してもらう形にしておく。
if (-not $NodesOnly -and $NgolRuntime) {
    if (-not (Test-Path $NgolRuntime)) { throw "runtime not found: $NgolRuntime" }
    Copy-Item (Join-Path $NgolRuntime "*") $ngolDir -Recurse -Force
}
elseif (-not $NodesOnly) {
    throw ("no NGOL runtime given. Build it first, then hand it over:" +
           [Environment]::NewLine + "  scripts/build.ps1" +
           [Environment]::NewLine + "  deploy.ps1 -PluginBinary <dll> -NgolRuntime <repo>\build\runtime")
}

# This host's own nodes sit beside the generic samples the payload already placed.
$csDest = Join-Path $ngolDir "Nodes\CustomNodes\cs"
New-Item -ItemType Directory -Path $csDest -Force | Out-Null
Copy-Item (Join-Path $bridgeRoot "nodes\*") $csDest -Recurse -Force

# The generic extension samples are node sources too, so they hot-reload the same way.
# Refreshing only this host's own nodes meant a change over there needed a full redeploy,
# which needs the host closed - for a file the host would have picked up on its own.
$extSrc = Join-Path $repoRoot "samples\nodes"
if (Test-Path $extSrc) {
    $extDest = Join-Path $csDest "ext"
    New-Item -ItemType Directory -Path $extDest -Force | Out-Null
    Copy-Item (Join-Path $extSrc "*") $extDest -Recurse -Force
}

# A node deleted from the repository keeps running: copying over the top never removes it.
# A node folded into another one stayed registered here long after it was gone upstream.
#    Only the node sources are compared. Graphs, WebUI plugins and the files the runtime
#    writes live in the same tree and belong to whoever runs the host.
# Never delete by default: someone may keep their own nodes in this same folder, and the
#    folder is outside version control, so a wrong delete cannot be undone.
$placed = @()
foreach ($root in @((Join-Path $bridgeRoot "nodes"), $extSrc)) {
    if (-not $root -or -not (Test-Path $root)) { continue }
    $prefix = if ($root -eq $extSrc) { "ext\" } else { "" }
    $len = (Get-Item $root).FullName.TrimEnd('\').Length + 1
    $placed += Get-ChildItem $root -Recurse -File | ForEach-Object { $prefix + $_.FullName.Substring($len) }
}
$csLen = (Get-Item $csDest).FullName.TrimEnd('\').Length + 1
$stale = @(Get-ChildItem $csDest -Recurse -File |
           ForEach-Object { $_.FullName.Substring($csLen) } |
           Where-Object { $placed -notcontains $_ })

if ($stale.Count -gt 0) {
    $note = if ($Clean) { "removing" } else { "-Clean removes them" }
    Write-Host ("  stale    : {0} file(s) this deploy did not place ({1})" -f $stale.Count, $note) -ForegroundColor DarkYellow
    foreach ($s in $stale) { Write-Host ("             {0}" -f $s) -ForegroundColor DarkYellow }
    if ($Clean) {
        # Delete exactly what was listed. Re-scanning here would delete files that appeared
        #    between the two scans, which is not what the list shown to the user said.
        foreach ($s in $stale) { Remove-Item (Join-Path $csDest $s) -Force -ErrorAction SilentlyContinue }
    }
}

if (-not $NodesOnly) {
    if (-not (Test-Path $PluginBinary)) { throw "plugin binary not found: $PluginBinary" }
    Copy-Item $PluginBinary $binDir -Force

    $dataDest = Join-Path $Dest "data"
    New-Item -ItemType Directory -Path $dataDest -Force | Out-Null
    Copy-Item (Join-Path $bridgeRoot "data\*") $dataDest -Recurse -Force

    # Written rather than copied: the port is the one thing a second install has to change.
    $config = @{ port = $Port; forceDirectMode = $false } | ConvertTo-Json
    Set-Content -Path (Join-Path $ngolDir "ngol-config.json") -Value $config -Encoding UTF8
}

Write-Host ""
Write-Host "deployed to $Dest" -ForegroundColor Green
Write-Host ("  plugin : {0}" -f (Join-Path $binDir "NgolForObs.dll")) -ForegroundColor DarkGray
Write-Host ("  runtime: {0}" -f $ngolDir) -ForegroundColor DarkGray
Write-Host ("  nodes  : {0}" -f (Get-ChildItem $csDest -Filter *.cs -Recurse).Count) -ForegroundColor DarkGray
Write-Host ("  address: http://127.0.0.1:{0}/" -f $Port) -ForegroundColor DarkGray
