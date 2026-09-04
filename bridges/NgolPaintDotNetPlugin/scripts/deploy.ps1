# Put the plugin and everything it loads into the folder the host scans.
#
# The host is a .NET application, so there is no launcher and no injection here: one DLL in
# the plugin folder is the whole mechanism. What lands there is what the repository builds.
#
# Usage:
#   ./deploy.ps1 -PluginBinary <NgolForPaintDotNet.dll> -NgolRuntime <repo>\build\runtime
#   ./deploy.ps1 -PluginBinary <...> -NgolRuntime <...> -Dest <folder>
#   ./deploy.ps1 -PluginBinary <...> -NodesOnly
#   ./deploy.ps1 -PluginBinary <...> -NgolRuntime <...> -Clean
#
# The host must be closed: it scans the plugin folder once at start-up and holds what it loaded.

param(
    [Parameter(Mandatory = $true)][string]$PluginBinary,
    [string]$NgolRuntime = "",
    [string]$Dest = "",
    [int]$Port = 11156,
    [switch]$NodesOnly,
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$bridgeRoot = Split-Path -Parent $PSScriptRoot

# The per-user folder rather than the install directory: the host reads both, and only this
# one can be written without elevation. An installed copy under Program Files needs an
# administrator for every deploy, which makes the edit-and-try loop far more expensive.
if (-not $Dest) {
    $Dest = Join-Path ([Environment]::GetFolderPath('MyDocuments')) "paint.net App Files"
}
# FileTypes rather than Effects. The host constructs a file type factory at start-up, which is
# what starts NGOL, and a factory that returns no file types adds nothing to any menu or dialog.
# An effect placed in Effects would sit in the Effects menu, and running it would replace the
# open image with that effect's output - empty, because this plugin does not draw.
$pluginDir = Join-Path $Dest "FileTypes"
$ngolDir = Join-Path $Dest "ngol"

$running = Get-Process -Name "paintdotnet" -ErrorAction SilentlyContinue
if ($running -and -not $NodesOnly) {
    throw "the host is running (pid $($running.Id -join ', ')); close it before deploying"
}

New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null
New-Item -ItemType Directory -Path $ngolDir -Force | Out-Null

# NGOL goes beside the plugin folder, never inside it. The host reads every assembly it finds
# in a plugin folder, so putting the runtime there makes it try to read NGOL itself as a plugin.
if (-not $NodesOnly) {
    if (-not $NgolRuntime) {
        throw ("no NGOL runtime given. Build it first, then hand it over:" +
               [Environment]::NewLine + "  scripts/build.ps1" +
               [Environment]::NewLine + "  deploy.ps1 -PluginBinary <dll> -NgolRuntime <repo>\build\runtime")
    }
    if (-not (Test-Path $NgolRuntime)) { throw "runtime not found: $NgolRuntime" }
    Copy-Item (Join-Path $NgolRuntime "*") $ngolDir -Recurse -Force

    if (-not (Test-Path $PluginBinary)) { throw "plugin binary not found: $PluginBinary" }
    Copy-Item $PluginBinary $pluginDir -Force

    # Written rather than copied: the port is the one thing a second install has to change.
    $config = @{ port = $Port; forceDirectMode = $false } | ConvertTo-Json
    Set-Content -Path (Join-Path $ngolDir "ngol-config.json") -Value $config -Encoding UTF8
}

# This host's own nodes sit beside the generic samples the runtime already placed.
$csDest = Join-Path $ngolDir "Nodes\CustomNodes\cs"
New-Item -ItemType Directory -Path $csDest -Force | Out-Null
Copy-Item (Join-Path $bridgeRoot "nodes\*") $csDest -Recurse -Force

# A node deleted from the repository keeps running: copying over the top never removes it.
# Never delete by default - this folder is outside version control and may hold the user's own
# nodes, so a wrong delete cannot be undone.
$root = Join-Path $bridgeRoot "nodes"
$len = (Get-Item $root).FullName.TrimEnd('\').Length + 1
$placed = @(Get-ChildItem $root -Recurse -File | ForEach-Object { $_.FullName.Substring($len) })
$csLen = (Get-Item $csDest).FullName.TrimEnd('\').Length + 1
$stale = @(Get-ChildItem $csDest -Recurse -File |
           ForEach-Object { $_.FullName.Substring($csLen) } |
           Where-Object { $placed -notcontains $_ -and $_ -notlike 'ext\*' })

if ($stale.Count -gt 0) {
    $note = if ($Clean) { "removing" } else { "-Clean removes them" }
    Write-Host ("  stale    : {0} file(s) this deploy did not place ({1})" -f $stale.Count, $note) -ForegroundColor DarkYellow
    foreach ($s in $stale) { Write-Host ("             {0}" -f $s) -ForegroundColor DarkYellow }
    if ($Clean) {
        foreach ($s in $stale) { Remove-Item (Join-Path $csDest $s) -Force -ErrorAction SilentlyContinue }
    }
}

# graphs\<name>\graph.json goes to the runtime's Graphs folder under the id the file declares,
# which is what the editor and the toolbar menu list. An existing file with the same id is
# overwritten and cannot be recovered, so anything worth keeping needs a different id.
$graphSrc = Join-Path $bridgeRoot "graphs"
if (Test-Path $graphSrc) {
    $graphDest = Join-Path $ngolDir "Graphs"
    New-Item -ItemType Directory -Path $graphDest -Force | Out-Null
    Get-ChildItem -Path $graphSrc -Directory | ForEach-Object {
        $g = Join-Path $_.FullName "graph.json"
        if (Test-Path $g) {
            $id = (Get-Content $g -Raw | ConvertFrom-Json).id
            if (-not $id) { $id = $_.Name }
            $to = Join-Path $graphDest ("{0}.json" -f $id)
            $overwrote = Test-Path $to
            Copy-Item $g $to -Force
            if ($overwrote) {
                Write-Host ("  graph    : {0}  (overwritten)" -f $id) -ForegroundColor DarkYellow
            } else {
                Write-Host ("  graph    : {0}" -f $id) -ForegroundColor DarkGray
            }
        }
    }
}

Write-Host ""
Write-Host "deployed to $Dest" -ForegroundColor Green
Write-Host ("  plugin : {0}" -f (Join-Path $pluginDir "NgolForPaintDotNet.dll")) -ForegroundColor DarkGray
Write-Host ("  runtime: {0}" -f $ngolDir) -ForegroundColor DarkGray
Write-Host ("  nodes  : {0}" -f (Get-ChildItem $csDest -Filter *.cs -Recurse).Count) -ForegroundColor DarkGray
Write-Host ("  address: http://127.0.0.1:{0}/" -f $Port) -ForegroundColor DarkGray
