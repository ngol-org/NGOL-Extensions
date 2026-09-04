# Assemble everything NGOL loads at runtime, from source, into one folder.
#
# Two repositories are involved. NGOL itself lives in the submodule under NodeGraphModLab;
# the extensions, the nodes and the bridges live here. Nothing is shipped pre-built, so this
# script compiles both sides and puts the result together.
#
# Usage:
#   ./scripts/build.ps1
#   ./scripts/build.ps1 -Dest <folder>
#   ./scripts/build.ps1 -SkipNative     # no C++ toolchain available
#
# Needed: .NET SDK, Node.js. The native part additionally needs CMake and MSVC; without them
# pass -SkipNative and the hook extension will load without its native side.

param(
    [string]$Dest = "",
    [string]$Configuration = "Release",
    [switch]$SkipNative
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$core = Join-Path $repoRoot "NodeGraphModLab"
if (-not $Dest) { $Dest = Join-Path $repoRoot "build\runtime" }

# Checked by a file rather than by the folder existing: a submodule that was never
# initialised leaves an empty directory behind, and every later step then fails with a
# path error that says nothing about the cause.
if (-not (Test-Path (Join-Path $core "NodeGraphModLab.Core\NodeGraphModLab.Core.csproj"))) {
    throw ("the NGOL core submodule is not checked out: " + $core +
           "  ->  git submodule update --init --recursive")
}

Write-Host ""
Write-Host "=== build.ps1 ===" -ForegroundColor Cyan
Write-Host ("  core : {0}" -f $core) -ForegroundColor DarkGray
Write-Host ("  dest : {0}" -f $Dest) -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
# 1. NGOL itself
# ---------------------------------------------------------------------------
$projects = @(
    @{ Name = "NodeGraphModLab.Core";         Path = "NodeGraphModLab.Core\NodeGraphModLab.Core.csproj";                Framework = "net6.0" }
    @{ Name = "NodeGraphModLab.BuiltinNodes"; Path = "NodeGraphModLab.BuiltinNodes\NodeGraphModLab.BuiltinNodes.csproj"; Framework = "netstandard2.0" }
    @{ Name = "NodeGraphModLab.HostLogging";  Path = "NodeGraphModLab.HostLogging\NodeGraphModLab.HostLogging.csproj";   Framework = "" }
)
foreach ($p in $projects) {
    Write-Host ("building {0}..." -f $p.Name) -ForegroundColor Yellow
    $buildArgs = @("build", (Join-Path $core $p.Path), "-c", $Configuration, "--nologo")
    if ($p.Framework) { $buildArgs += @("-f", $p.Framework) }
    & dotnet @buildArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw ("build failed: " + $p.Name) }
}

# ---------------------------------------------------------------------------
# 2. The web interface
#
# Built here rather than left to the reader: without it NGOL still answers on its port,
# but the browser gets nothing, and the cause is not visible from the running process.
# ---------------------------------------------------------------------------
$webUiSrc = Join-Path $core "WebUI"
Write-Host "building the web interface..." -ForegroundColor Yellow
Push-Location $webUiSrc
try {
    if (-not (Test-Path (Join-Path $webUiSrc "node_modules"))) { & npm ci | Out-Null }
    & npm run build | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "build failed: WebUI" }
}
finally { Pop-Location }

# ---------------------------------------------------------------------------
# 3. The managed entry point the native side calls by name
# ---------------------------------------------------------------------------
Write-Host "building the managed entry point..." -ForegroundColor Yellow
& dotnet build (Join-Path $repoRoot "bridges\NgolActivator\NgolActivator.csproj") -c $Configuration --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "build failed: NgolActivator" }

# ---------------------------------------------------------------------------
# 4. The native side of the hook extension
#
# Optional on purpose: it is the only part that needs a C++ toolchain, and the rest is
# usable without it. Skipping is reported, because an extension that loads without its
# native side fails later in a way that looks unrelated to this build.
# ---------------------------------------------------------------------------
$nativeSrc = Join-Path $repoRoot "native\ngol_native"
# Where the library lands depends on the generator: directly under build\ for a
# single-config one such as Ninja, under build\<Configuration>\ for Visual Studio.
$nativeDll = @(
    (Join-Path $nativeSrc "build\ngol_native.dll"),
    (Join-Path $nativeSrc "build\$Configuration\ngol_native.dll")
)
if ($SkipNative) {
    Write-Host "skipping the native build (-SkipNative)" -ForegroundColor DarkYellow
} else {
    # Also looked for inside Visual Studio: it ships CMake but does not put it on PATH,
    # so a machine that can build the native side would otherwise be told it cannot.
    # Where VS is installed varies, hence vswhere, whose own location is fixed.
    $cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
    if (-not $cmake) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
        if (Test-Path $vswhere) {
            $vsPath = & $vswhere -latest -products * -property installationPath 2>$null
            if ($vsPath) {
                $candidate = Join-Path $vsPath "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
                if (Test-Path $candidate) { $cmake = $candidate }
            }
        }
    }
    if (-not $cmake) {
        throw "cmake not found. Install CMake and a C++ toolchain, or pass -SkipNative."
    }

    Write-Host ("building ngol_native... ({0})" -f $cmake) -ForegroundColor Yellow
    & $cmake -S $nativeSrc -B (Join-Path $nativeSrc "build") -DCMAKE_BUILD_TYPE=$Configuration | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "configure failed: ngol_native" }
    & $cmake --build (Join-Path $nativeSrc "build") --config $Configuration | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "build failed: ngol_native" }
}

# ---------------------------------------------------------------------------
# 5. Put it together
# ---------------------------------------------------------------------------
New-Item -ItemType Directory -Path $Dest -Force | Out-Null

$coreOut = Join-Path $core "NodeGraphModLab.Core\bin\$Configuration\net6.0"
if (-not (Test-Path $coreOut)) { throw ("core output not found: " + $coreOut) }
Get-ChildItem $coreOut -Filter *.dll | ForEach-Object { Copy-Item $_.FullName $Dest -Force }

# Only the satellite resource folders, picked by what is inside them rather than by name:
# taking every subdirectory also sweeps in stale RID-specific output and .pdb files, and
# a .pdb carries the absolute paths of the machine that produced it.
Get-ChildItem $coreOut -Directory | Where-Object {
    Get-ChildItem $_.FullName -Filter *.resources.dll -File -ErrorAction SilentlyContinue
} | ForEach-Object { Copy-Item $_.FullName $Dest -Recurse -Force }

Copy-Item (Join-Path $core "NodeGraphModLab.HostLogging\bin\$Configuration\net6.0\NodeGraphModLab.HostLogging.dll") $Dest -Force

$builtinDest = Join-Path $Dest "Nodes\Builtin"
New-Item -ItemType Directory -Path $builtinDest -Force | Out-Null
Copy-Item (Join-Path $core "NodeGraphModLab.BuiltinNodes\bin\$Configuration\netstandard2.0\NodeGraphModLab.BuiltinNodes.dll") $builtinDest -Force

$csDest = Join-Path $Dest "Nodes\CustomNodes\cs"
New-Item -ItemType Directory -Path $csDest -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $Dest "Nodes\CustomNodes\dll") -Force | Out-Null

# The extension assemblies are useless without the nodes that call them, so the node
# sources travel with them (.srclist / .rsp included - they are part of a node).
$nodes = Join-Path $repoRoot "samples\nodes"
if (Test-Path $nodes) {
    $extDest = Join-Path $csDest "ext"
    New-Item -ItemType Directory -Path $extDest -Force | Out-Null
    Copy-Item (Join-Path $nodes "*") $extDest -Recurse -Force
}

$activatorOut = Join-Path $repoRoot "bridges\NgolActivator\bin\$Configuration\net8.0"
foreach ($n in @("NgolActivator.dll", "NgolActivator.runtimeconfig.json", "NgolActivator.deps.json")) {
    $src = Join-Path $activatorOut $n
    if (Test-Path $src) { Copy-Item $src $Dest -Force }
}

$webUiDest = Join-Path $Dest "WebUI"
New-Item -ItemType Directory -Path $webUiDest -Force | Out-Null
Copy-Item (Join-Path $webUiSrc "dist\*") $webUiDest -Recurse -Force

# Found rather than listed: a fixed list silently drops an extension added later, and the
# nodes that need it then fail to compile with no obvious cause.
$packs = @(Get-ChildItem (Join-Path $repoRoot "extensions") -Filter "pack-*.ps1" -Recurse -File)
if ($packs.Count -eq 0) { throw "no extension pack scripts found under extensions/" }
foreach ($pack in $packs) {
    Write-Host ("packing {0}..." -f $pack.Directory.Name) -ForegroundColor Yellow
    & $pack.FullName -DistRoot $Dest | Out-Null
}

Write-Host ""
Write-Host ("runtime ready: {0}" -f $Dest) -ForegroundColor Green
Write-Host ("  files : {0}" -f (Get-ChildItem $Dest -Recurse -File).Count) -ForegroundColor DarkGray
if (-not (@($nativeDll | Where-Object { Test-Path $_ }).Count)) {
    Write-Host "  note  : ngol_native.dll is absent; the hook extension loads without its native side" -ForegroundColor DarkYellow
}
Write-Host ""
Write-Host ("Hand it to a bridge with:  -NgolRuntime " + $Dest) -ForegroundColor DarkGray
