# pythonnet (Python.NET) を取得して、ノードが見る場所へ置く。
#
# このリポジトリは Python.Runtime.dll を同梱していない。第三者のもの（MIT）なので、
# 再配布するかは配る人が決めること。ここは「入れたい人が 1 コマンドで入れられる」ための道具。
#
# 置き場所:
#   nodes\lib\Python.Runtime.dll      <- パッケージの内側。導入場所を変えても壊れない
#
# 使い方:
#   ./get_pythonnet.ps1
#   ./get_pythonnet.ps1 -Version 3.1.0
#   ./get_pythonnet.ps1 -From D:\somewhere\Python.Runtime.dll   # 取得済みを使う
#   ./get_pythonnet.ps1 -Deploy                                  # 置いたあと配置まで
#
# 取得は nuget.org から。取りに行くだけで、外へ何かを送ることはしない。
# ネットに繋がない機械では -From を使う。

param(
    [string]$Version = "3.1.0",
    [string]$From = "",
    [switch]$Deploy,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$here    = Split-Path -Parent $PSCommandPath
$root    = Split-Path -Parent $here
$libDir  = Join-Path $root "nodes\lib"
$dest    = Join-Path $libDir "Python.Runtime.dll"

New-Item -ItemType Directory -Path $libDir -Force | Out-Null

if ((Test-Path $dest) -and -not $Force) {
    $existing = (Get-Item $dest).Length
    Write-Host ("既にあります: {0} ({1:N0} bytes)。入れ替えるなら -Force" -f $dest, $existing) -ForegroundColor DarkYellow
    exit 0
}

if ($From) {
    if (-not (Test-Path $From)) { throw "見つかりません: $From" }
    Copy-Item $From $dest -Force
    Write-Host ("控えから置きました: {0}" -f $From) -ForegroundColor DarkGray
}
else {
    # NuGet パッケージは .zip。lib\netstandard2.0\Python.Runtime.dll が入っている
    $url  = "https://www.nuget.org/api/v2/package/pythonnet/$Version"
    $tmp  = Join-Path ([System.IO.Path]::GetTempPath()) ("pythonnet_" + [System.Guid]::NewGuid().ToString("N").Substring(0,8))
    $pkg  = Join-Path $tmp "pythonnet.zip"
    New-Item -ItemType Directory -Path $tmp -Force | Out-Null

    Write-Host ("取得中: {0}" -f $url) -ForegroundColor DarkGray
    try {
        Invoke-WebRequest -Uri $url -OutFile $pkg -UseBasicParsing
        Expand-Archive -Path $pkg -DestinationPath (Join-Path $tmp "x") -Force

        $dll = Get-ChildItem -Path (Join-Path $tmp "x") -Recurse -Filter "Python.Runtime.dll" |
               Where-Object { $_.FullName -match "netstandard2\.0" } |
               Select-Object -First 1
        if (-not $dll) {
            $dll = Get-ChildItem -Path (Join-Path $tmp "x") -Recurse -Filter "Python.Runtime.dll" | Select-Object -First 1
        }
        if (-not $dll) { throw "パッケージの中に Python.Runtime.dll がありません" }

        Copy-Item $dll.FullName $dest -Force

        # ライセンスも一緒に置く。第三者のものを配るなら表示が要る
        $lic = Get-ChildItem -Path (Join-Path $tmp "x") -Recurse -Include "LICENSE*", "*.md" |
               Where-Object { $_.Name -match "^LICENSE" } | Select-Object -First 1
        if ($lic) { Copy-Item $lic.FullName (Join-Path $libDir "Python.Runtime.LICENSE.txt") -Force }
    }
    finally {
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# 置いただけでは何も言っていない。実体を見る
$item = Get-Item $dest
Write-Host ""
Write-Host "placed" -ForegroundColor Green
Write-Host ("  {0}" -f $dest) -ForegroundColor DarkGray
Write-Host ("  {0:N0} bytes / {1}" -f $item.Length, $item.LastWriteTime) -ForegroundColor DarkGray

$asm = [System.Reflection.AssemblyName]::GetAssemblyName($dest)
Write-Host ("  {0}" -f $asm.FullName) -ForegroundColor DarkGray
if ($asm.Name -ne "Python.Runtime") { throw "中身が Python.Runtime ではありません: $($asm.Name)" }

if (Test-Path (Join-Path $libDir "Python.Runtime.LICENSE.txt")) {
    Write-Host "  ライセンスも置きました" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "これで BlenderPyNetNode が配られるようになります。" -ForegroundColor DarkGray
Write-Host "  deploy.ps1 -AddonOnly -Force        # 置き直す" -ForegroundColor DarkGray
Write-Host "  build_package.ps1 -PythonRuntime `"$dest`"   # 配布物へ入れる" -ForegroundColor DarkGray

if ($Deploy) {
    Write-Host ""
    & (Join-Path $here "deploy.ps1") -AddonOnly -Force
}
