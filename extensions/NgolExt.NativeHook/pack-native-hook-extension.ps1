# pack-native-hook-extension.ps1 - packages ngol.ext.native-hook into the caller-specified Extensions/ folder (-DistRoot)
param(
    [string]$DistRoot = "",
    [string]$Configuration = "Release",
    [string]$ImplTfm = "net6.0"
)

# 置き先は使う側の環境で違うので既定を持たせない。既定を書くと、渡し忘れたときに
# 気づかないまま別のフォルダへ配置され、古いものが読まれ続ける。
if (-not $DistRoot) {
    throw "-DistRoot is required: pass the folder that holds Extensions/"
}

$ErrorActionPreference = "Stop"
$ExtRoot  = $PSScriptRoot
$RepoRoot = Split-Path -Parent (Split-Path -Parent $ExtRoot)

if (-not $PSBoundParameters.ContainsKey('ImplTfm')) {
    Write-Warning "-ImplTfm not specified; using default '$ImplTfm'. For .NET Framework hosts (net462), pass -ImplTfm net462 explicitly."
}

$apiProj   = Join-Path $ExtRoot "NgolExt.NativeHook.Api\NgolExt.NativeHook.Api.csproj"
$implProj  = Join-Path $ExtRoot "NgolExt.NativeHook.Impl\NgolExt.NativeHook.Impl.csproj"

function Resolve-BuildOut([string]$projectDir, [string]$configuration, [string]$tfm) {
    $candidates = @(
        (Join-Path $projectDir "bin\$configuration\$tfm"),
        (Join-Path $projectDir "bin\x64\$configuration\$tfm")
    )
    foreach ($dir in $candidates) {
        if (Test-Path $dir) { return $dir }
    }
    throw "Build output not found under $projectDir for $configuration/$tfm"
}

Write-Host "`n=== pack ngol.ext.native-hook ===" -ForegroundColor Cyan

dotnet build $apiProj   -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Api build failed" }
dotnet build $implProj  -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Impl build failed" }

$implOut  = Resolve-BuildOut (Join-Path $ExtRoot "NgolExt.NativeHook.Impl") $Configuration $ImplTfm

$extDir   = Join-Path $DistRoot "Extensions\ngol.ext.native-hook"
New-Item -ItemType Directory -Path $extDir -Force | Out-Null

# Impl DLL + Api DLL
Copy-Item (Join-Path $implOut  "NgolExt.NativeHook.Impl.dll") $extDir  -Force
Copy-Item (Join-Path $implOut  "NgolExt.NativeHook.Api.dll")  $extDir  -Force
Write-Host "  NgolExt.NativeHook.Impl.dll" -ForegroundColor DarkCyan
Write-Host "  NgolExt.NativeHook.Api.dll"  -ForegroundColor DarkCyan

# ngol_native.dll
# 置き場所は CMake のジェネレーターで変わる。Ninja のような単一構成なら build\ 直下、Visual Studio の
# ような複数構成なら build\<Configuration>\。片方しか見ないと、ビルドは成功しているのに
# 「無い」と言って警告だけ出して先へ進み、ネイティブ側の無い拡張が出来上がる。
$nativeCandidates = @(
    (Join-Path $RepoRoot "native\ngol_native\build\ngol_native.dll"),
    (Join-Path $RepoRoot "native\ngol_native\build\$Configuration\ngol_native.dll")
)
$nativeSrc = @($nativeCandidates | Where-Object { Test-Path $_ })[0]
if ($nativeSrc) {
    Copy-Item $nativeSrc $extDir -Force
    Write-Host "  ngol_native.dll" -ForegroundColor DarkCyan
} else {
    Write-Warning ("ngol_native.dll not found (build native project first). Looked in: " +
                   ($nativeCandidates -join ", "))
}

# Api DLL - Roslyn custom-node ref (ngolRoot)
$apiDll = Join-Path $implOut "NgolExt.NativeHook.Api.dll"
if (-not (Test-Path $apiDll)) { throw "Api DLL not found: $apiDll" }
Copy-Item $apiDll $DistRoot -Force
Write-Host "  NgolExt.NativeHook.Api.dll -> ngolRoot (Roslyn ref)" -ForegroundColor DarkCyan

# extension.json
$jsonSrc = Join-Path $ExtRoot "NgolExt.NativeHook.Impl\extension.json"
Copy-Item $jsonSrc $extDir -Force
Write-Host "  extension.json" -ForegroundColor DarkCyan

# LICENSE-minhook.md (MinHook license notice)
$licenseSrc = Join-Path $ExtRoot "LICENSE-minhook.md"
if (Test-Path $licenseSrc) {
    Copy-Item $licenseSrc $extDir -Force
    Write-Host "  LICENSE-minhook.md" -ForegroundColor DarkCyan
} else {
    Write-Warning "LICENSE-minhook.md not found: $licenseSrc"
}

Write-Host "  -> $extDir" -ForegroundColor Green
