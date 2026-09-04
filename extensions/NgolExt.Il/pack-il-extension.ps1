<#
.SYNOPSIS
  ngol.ext.il を <DistRoot>/Extensions/ngol.ext.il/ へ配置する。

.DESCRIPTION
  この拡張はサービスを提供しない。lib/<tfm>/ へライブラリを置き、
  capability を宣言する最小エントリ DLL を置くだけ。

  同梱ライブラリは名前を列挙せず、ビルド出力から LibraryPattern で拾う。
  版更新でアセンブリ構成が変わったときに、列挙の取り残しで無言に壊れないようにするため。
  実際に何を配ったかは必ずログに出す。

.PARAMETER DistRoot
  配置先の ngolRoot。既定値は設けない（ホスト固有のパスを埋め込まないため）。

.EXAMPLE
  .\pack-il-extension.ps1 -DistRoot "<ngolRoot>"
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
$ExtensionId = "ngol.ext.il"
# 名前を列挙せずパターンで拾う。版更新でアセンブリ構成が変わっても取り残さないため。
# MonoMod.RuntimeDetour は Utils / Core / Backports / ILHelpers / Mono.Cecil を連れてくる。
$LibraryPatterns = @("MonoMod*.dll", "Mono.Cecil*.dll")

Write-Host "`n=== pack $ExtensionId ===" -ForegroundColor Cyan

$implProj = Join-Path $ExtRoot "NgolExt.Il.Impl\NgolExt.Il.Impl.csproj"
dotnet build $implProj -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Impl build failed" }

# ビルドは netstandard2.0（CoreCLR / Mono のどちらのホストからも読める最小の土台）。
# 置き場所のフォルダ名だけがホストのランタイム名に従う（$Tfm）。
$implOut = Join-Path $ExtRoot "NgolExt.Il.Impl\bin\$Configuration\netstandard2.0"
if (-not (Test-Path $implOut)) { throw "Build output not found: $implOut" }

$extDir = Join-Path $DistRoot "Extensions\$ExtensionId"
$libDir = Join-Path $extDir "lib\$Tfm"

# lib/ は毎回作り直す。上書きだけだと、版更新で不要になったアセンブリが残り続ける。
#   残骸は preload で読まれてしまい、型の衝突として現れる（例: MonoMod.Iced）。
if (Test-Path $libDir) { Remove-Item $libDir -Recurse -Force }
New-Item -ItemType Directory -Path $libDir -Force | Out-Null

Copy-Item (Join-Path $implOut "NgolExt.Il.Impl.dll") $extDir -Force
Write-Host "  NgolExt.Il.Impl.dll" -ForegroundColor DarkCyan

$libs = @()
foreach ($pattern in $LibraryPatterns) {
    $matched = @(Get-ChildItem -Path $implOut -Filter $pattern -File)
    if ($matched.Count -eq 0) { throw "No library matched '$pattern' under $implOut" }
    $libs += $matched
}
foreach ($lib in $libs) {
    Copy-Item $lib.FullName $libDir -Force
    # 版まで出す。同名の別版がホストに居る場合、ここが唯一の突き合わせ材料になる。
    $ver = (Get-Item $lib.FullName).VersionInfo.FileVersion
    Write-Host "  lib/$Tfm/$($lib.Name)  ($ver)" -ForegroundColor DarkCyan
}
Write-Host "  ($($libs.Count) librarie(s) matched $($LibraryPatterns -join ', '))" -ForegroundColor DarkGray

$jsonSrc = Join-Path $ExtRoot "NgolExt.Il.Impl\extension.json"
if (-not (Test-Path $jsonSrc)) { throw "extension.json not found: $jsonSrc" }
Copy-Item $jsonSrc $extDir -Force
Write-Host "  extension.json" -ForegroundColor DarkCyan

Write-Host "  -> $extDir" -ForegroundColor Green
