# prepare-dist.ps1 - D3DTargetApp を動かせる配置一式を作る
#
# NGOL 本体・拡張パッケージ・ノード・アプリ・ブリッジをソースからビルドし、
# 1 つのフォルダへまとめる。出来上がったフォルダの .exe を起動すれば、
# アプリは NGOL を知らないまま NGOL が同居して立ち上がる。
#
# 必要なもの: .NET SDK / CMake + MSVC / 本体の WebUI をビルド済み（scripts\build.ps1 が作る）
#
# Usage:
#   .\prepare-dist.ps1                      -> build\dist へ作る
#   .\prepare-dist.ps1 -Port 11158 -SkipNative
#   .\prepare-dist.ps1 -DistRoot <別の場所>  -> 場所を変えたいときだけ

param(
    # 動かす一式を作る場所。既定はこのスクリプトの隣の build\dist
    [string]$DistRoot = "",
    [int]$Port = 11156,
    [string]$Configuration = "Release",
    # ネイティブ（アプリ本体とブリッジ）のビルドを飛ばし、既存の成果物を使う
    [switch]$SkipNative,
    # ネイティブのビルド作業フォルダ。既定はこのスクリプトの隣の build\native
    [string]$NativeBuildDir = ""
)

$ErrorActionPreference = "Stop"

$AppRoot  = $PSScriptRoot
$RepoRoot = (Resolve-Path (Join-Path $AppRoot "..\..\..")).Path

# 作った物は build の下で閉じる。追跡対象から外れている場所なので、
# 消したいときはこのフォルダごと消せばよい。
if (-not $DistRoot)       { $DistRoot       = Join-Path $AppRoot "build\dist" }
if (-not $NativeBuildDir) { $NativeBuildDir = Join-Path $AppRoot "build\native" }

# Visual Studio 同梱の CMake を探す。入れた版・エディション・場所は環境ごとに違うので、
# 位置が保証されている vswhere から辿る。見つからなければ null を返す。
function Find-VsCMake {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) { return $null }

    $vsPath = & $vswhere -latest -products * -property installationPath 2>$null
    if (-not $vsPath) { return $null }

    $candidate = Join-Path $vsPath "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
    if (Test-Path $candidate) { return $candidate }
    return $null
}

Write-Host ""
Write-Host "=== prepare-dist (D3DTargetApp) ===" -ForegroundColor Cyan
Write-Host "  RepoRoot : $RepoRoot" -ForegroundColor DarkGray
Write-Host "  DistRoot : $DistRoot" -ForegroundColor DarkGray
Write-Host "  Port     : $Port" -ForegroundColor DarkGray

function Invoke-Step([string]$label, [scriptblock]$body) {
    Write-Host ""
    Write-Host "[$label]" -ForegroundColor Yellow
    & $body
    if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "$label failed (exit $LASTEXITCODE)" }
}

# ---------------------------------------------------------------- NGOL 本体
Invoke-Step "1/7 NGOL core build" {
    dotnet build (Join-Path $RepoRoot "NodeGraphModLab\NodeGraphModLab.Core\NodeGraphModLab.Core.csproj") -c $Configuration -f net6.0
    if ($LASTEXITCODE -ne 0) { throw "Core build failed" }
    dotnet build (Join-Path $RepoRoot "NodeGraphModLab\NodeGraphModLab.HostLogging\NodeGraphModLab.HostLogging.csproj") -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "HostLogging build failed" }
    dotnet build (Join-Path $RepoRoot "NodeGraphModLab\NodeGraphModLab.BuiltinNodes\NodeGraphModLab.BuiltinNodes.csproj") -c $Configuration -f netstandard2.0
    if ($LASTEXITCODE -ne 0) { throw "BuiltinNodes build failed" }
}

foreach ($d in @("", "Nodes\Builtin", "Nodes\CustomNodes\cs", "Nodes\CustomNodes\dll", "WebUI", "Graphs")) {
    New-Item -ItemType Directory -Path (Join-Path $DistRoot $d) -Force | Out-Null
}

Invoke-Step "2/7 copy core output" {
    $coreOut = Join-Path $RepoRoot "NodeGraphModLab\NodeGraphModLab.Core\bin\$Configuration\net6.0"
    if (-not (Test-Path $coreOut)) { throw "core output not found: $coreOut" }
    Get-ChildItem $coreOut -Filter "*.dll" | ForEach-Object { Copy-Item $_.FullName $DistRoot -Force }
    # 各言語のリソースフォルダ
    Get-ChildItem $coreOut -Directory | ForEach-Object { Copy-Item $_.FullName -Destination $DistRoot -Recurse -Force }
    Copy-Item (Join-Path $RepoRoot "NodeGraphModLab\NodeGraphModLab.HostLogging\bin\$Configuration\net6.0\NodeGraphModLab.HostLogging.dll") $DistRoot -Force
    Copy-Item (Join-Path $RepoRoot "NodeGraphModLab\NodeGraphModLab.BuiltinNodes\bin\$Configuration\netstandard2.0\NodeGraphModLab.BuiltinNodes.dll") (Join-Path $DistRoot "Nodes\Builtin") -Force
    Write-Host "  core / hostlogging / builtin-nodes" -ForegroundColor DarkCyan
}

# ---------------------------------------------------------------- 拡張パッケージ
# 各拡張の pack スクリプトが正本。手でコピーすると同梱ライブラリや extension.json の
# 配置漏れが起きるため、必ずこれを通すこと。
Invoke-Step "3/7 extension packages" {
    foreach ($pack in @(
        "extensions\NgolExt.Code\pack-code-extension.ps1",
        "extensions\NgolExt.Il\pack-il-extension.ps1",
        "extensions\NgolExt.NativeHook\pack-native-hook-extension.ps1"
    )) {
        $p = Join-Path $RepoRoot $pack
        if (Test-Path $p) { & $p -DistRoot $DistRoot } else { Write-Warning "not found: $p" }
    }
}

# ---------------------------------------------------------------- ノードと WebUI
Invoke-Step "4/7 nodes and WebUI" {
    $csDest = Join-Path $DistRoot "Nodes\CustomNodes\cs"

    $baseNodes = Join-Path $RepoRoot "samples\nodes"
    Copy-Item (Join-Path $baseNodes "*") $csDest -Recurse -Force
    Write-Host "  base nodes -> Nodes\CustomNodes\cs" -ForegroundColor DarkCyan

    # このサンプルに添付するグラフ。ノードを繋いだ状態で渡せるので、
    # 利用者は WebUI を開いて実行するだけで済む。
    $graphSrc = Join-Path $AppRoot "graphs"
    if (Test-Path $graphSrc) {
        Copy-Item (Join-Path $graphSrc "*.json") (Join-Path $DistRoot "Graphs") -Force
        Write-Host "  sample graphs -> Graphs" -ForegroundColor DarkCyan
    }

    $webUi = Join-Path $RepoRoot "NodeGraphModLab\WebUI\dist"
    if (Test-Path $webUi) {
        Copy-Item (Join-Path $webUi "*") (Join-Path $DistRoot "WebUI") -Recurse -Force
        Write-Host "  WebUI\dist -> WebUI" -ForegroundColor DarkCyan
    } else {
        Write-Warning "WebUI is not built. Run: scripts/build.ps1"
    }
}

# ---------------------------------------------------------------- ブリッジ（マネージド入口）
Invoke-Step "5/7 bridge activator" {
    $proj = Join-Path $AppRoot "bridge\clr_activator\NgolActivator.csproj"
    dotnet build $proj -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "activator build failed" }
    $out = Join-Path $AppRoot "bridge\clr_activator\bin\$Configuration\net8.0"
    foreach ($f in @("NgolActivator.dll", "NgolActivator.runtimeconfig.json", "NgolActivator.deps.json")) {
        Copy-Item (Join-Path $out $f) $DistRoot -Force
    }
    Write-Host "  activator -> dist" -ForegroundColor DarkCyan
}

# ---------------------------------------------------------------- アプリとブリッジ（ネイティブ）
Invoke-Step "6/7 native app + bridge" {
    if ($SkipNative) { Write-Host "  skipped" -ForegroundColor Gray; return }
    $cmake = Get-Command cmake -ErrorAction SilentlyContinue
    if ($cmake) {
        $cmake = $cmake.Source
    } else {
        $cmake = Find-VsCMake
        if (-not $cmake) { throw "cmake not found (install CMake, or Visual Studio with its CMake component)" }
    }

    & $cmake -S $AppRoot -B $NativeBuildDir -A x64
    if ($LASTEXITCODE -ne 0) { throw "cmake configure failed" }
    & $cmake --build $NativeBuildDir --config $Configuration
    if ($LASTEXITCODE -ne 0) { throw "cmake build failed" }

    foreach ($f in @("D3DTargetApp.exe", "NgolBridge.dll")) {
        Copy-Item (Join-Path $NativeBuildDir "$Configuration\$f") $DistRoot -Force
    }
    Write-Host "  app + bridge -> dist" -ForegroundColor DarkCyan
}

# ---------------------------------------------------------------- 設定
Invoke-Step "7/7 config" {
    $cfg = Join-Path $DistRoot "ngol-config.json"
    # forceDirectMode=false: 更新はブリッジがアプリの画面更新に合わせて回す。
    @{ port = $Port; forceDirectMode = $false } | ConvertTo-Json | Set-Content $cfg -Encoding UTF8
    Write-Host "  ngol-config.json (port=$Port)" -ForegroundColor DarkCyan
}

Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Green
Write-Host "  run: $(Join-Path $DistRoot 'D3DTargetApp.exe')" -ForegroundColor Green
Write-Host "  then connect to ws://127.0.0.1:$Port/ws" -ForegroundColor Green
