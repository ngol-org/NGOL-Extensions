<#
.SYNOPSIS
  ngol.ext.code を <DistRoot>/Extensions/ngol.ext.code/ へ配置する。

.DESCRIPTION
  この拡張はサービスを提供しない。行うのは
    1. lib/<tfm>/ へライブラリ（Iced 等）を置く
    2. capability を宣言する最小エントリ DLL を置く
  の 2 つだけ。

  lib/<tfm>/ に置かれた DLL は拡張ホストが強制ロードするため、
  動的コンパイルノードの参照に加わり `.cs` から直接 using できるようになる。

.PARAMETER DistRoot
  配置先の ngolRoot（Nodes/ ・ WebUI/ ・ ngol-config.json と同じ階層）。
  既定値は設けない。ホスト固有のパスをこのスクリプトに埋め込まないため。

.EXAMPLE
  .\pack-code-extension.ps1 -DistRoot "<ngolRoot>"
#>
param(
    [Parameter(Mandatory = $true)][string]$DistRoot,
    [string]$Configuration = "Release",
    # 配置先のホストが名乗る値だけを受け付ける。拡張ホストはこの 2 つしか探さないため、
    #    それ以外の名前のフォルダへ置くと「配置したのに読まれない」状態になる。
    [ValidateSet("net6.0", "net462")][string]$Tfm = "net6.0"
)

$ErrorActionPreference = "Stop"
$ExtRoot = $PSScriptRoot
$ExtensionId = "ngol.ext.code"

Write-Host "`n=== pack $ExtensionId ===" -ForegroundColor Cyan

$implProj = Join-Path $ExtRoot "NgolExt.Code.Impl\NgolExt.Code.Impl.csproj"
dotnet build $implProj -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Impl build failed" }

# ビルドは netstandard2.0（CoreCLR / Mono のどちらのホストからも読める最小の土台）。
# 置き場所のフォルダ名だけがホストのランタイム名に従う（$Tfm）。
$implOut = Join-Path $ExtRoot "NgolExt.Code.Impl\bin\$Configuration\netstandard2.0"
if (-not (Test-Path $implOut)) { throw "Build output not found: $implOut" }

$extDir = Join-Path $DistRoot "Extensions\$ExtensionId"
$libDir = Join-Path $extDir "lib\$Tfm"
New-Item -ItemType Directory -Path $libDir -Force | Out-Null

# エントリ DLL（capability 宣言のみ）
Copy-Item (Join-Path $implOut "NgolExt.Code.Impl.dll") $extDir -Force
Write-Host "  NgolExt.Code.Impl.dll" -ForegroundColor DarkCyan

# 同梱ライブラリ。ここに置いたものがノードから using できるようになる。
$libraries = @("Iced.dll")
foreach ($name in $libraries) {
    $src = Join-Path $implOut $name
    if (-not (Test-Path $src)) { throw "Missing library: $src" }
    Copy-Item $src $libDir -Force
    Write-Host "  lib/$Tfm/$name" -ForegroundColor DarkCyan
}

# extension.json はリポジトリ内のファイルが正本。スクリプトで生成しない。
$jsonSrc = Join-Path $ExtRoot "NgolExt.Code.Impl\extension.json"
if (-not (Test-Path $jsonSrc)) { throw "extension.json not found: $jsonSrc" }
Copy-Item $jsonSrc $extDir -Force
Write-Host "  extension.json" -ForegroundColor DarkCyan

Write-Host "  -> $extDir" -ForegroundColor Green
