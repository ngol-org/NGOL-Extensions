# 配布用のパッケージ（Blender の Extension 形式 zip）を組む。
#
# NGOL 一式（scripts/build.ps1 で組み、-NgolPortable で渡したもの）と、
# ここのブリッジ（addon / nodes / data / webui-plugins / graphs）を合わせて 1 本にする。
#
# 組み立ては必ず空の作業フォルダで行う。配置先から直接 zip にすると、
#    その機械で作ったグラフ・ログ・スプールが配布物に混ざる。
#
# 使い方:
#   ./build_package.ps1 -NgolPortable <リポジトリ>\build
#   ./build_package.ps1 -NgolPortable <リポジトリ>\build -OutDir <出力先>
#   ./build_package.ps1 -NgolPortable <リポジトリ>\build -PythonRuntime <Python.Runtime.dll>
#
# -PythonRuntime を渡すと pythonnet を使うノードを同梱する。
# 渡さなければそのノードは入らない（参照が無いままだとコンパイルに失敗するため）。

param(
    [string]$NgolPortable  = "",
    [string]$OutDir        = "",
    [string]$BlenderExe    = "",
    [string]$PythonRuntime = "",
    [switch]$NoPythonnet,
    [switch]$KeepStaging
)

$ErrorActionPreference = "Stop"

$here = Split-Path -Parent $PSCommandPath
$root = Split-Path -Parent $here
if (-not $OutDir) { $OutDir = Join-Path $root "dist" }

# --- 材料がそろっているか ------------------------------------------------------------
$addonSrc  = Join-Path $root "addon\ngol_for_blender"
$manifest   = Join-Path $addonSrc "blender_manifest.toml"

foreach ($p in @($addonSrc, $manifest)) {
    if (-not (Test-Path $p)) { throw "見つかりません: $p" }
}

# NGOL 一式は scripts/build.ps1 で組んだものを渡す（deploy.ps1 と同じ考え方）。
$staged = ""
if ($NgolPortable) {
    $runtimeSrc = Join-Path $NgolPortable "runtime"
    if (-not (Test-Path $runtimeSrc)) { throw "NGOL runtime not found: $runtimeSrc" }
}
else {
    throw ("NGOL 一式が渡されていません。先に組み立ててから渡してください。" +
           [Environment]::NewLine + "  scripts/build.ps1" +
           [Environment]::NewLine + "  build_package.ps1 -NgolPortable <リポジトリ>\build")
}

# 置き場所を決め打ちにしない（ProgramFiles とは限らず、携帯版は登録もされない）。
. (Join-Path $here "_blender.ps1")
$BlenderExe = Resolve-BlenderExe -Hint $BlenderExe
Write-Host ("  Blender  : {0}  ({1})" -f (Get-BlenderUserVersion -Exe $BlenderExe), $BlenderExe) -ForegroundColor DarkGray

# --- 空の作業フォルダ ----------------------------------------------------------------
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("ngol_pkg_" + [System.Guid]::NewGuid().ToString("N").Substring(0,8))
$pkg     = Join-Path $staging "ngol_for_blender"
New-Item -ItemType Directory -Path $pkg -Force | Out-Null
$ngol    = Join-Path $pkg "ngol"

Write-Host "組み立て中: $pkg" -ForegroundColor DarkGray

# --- アドオン本体 --------------------------------------------------------------------
Copy-Item (Join-Path $addonSrc "*.py") $pkg -Force
Copy-Item $manifest (Join-Path $pkg "blender_manifest.toml") -Force

# --- NGOL 一式 -----------------------------------------------------------------------
New-Item -ItemType Directory -Path $ngol -Force | Out-Null
Copy-Item (Join-Path $runtimeSrc "*") $ngol -Recurse -Force

# --- ノード --------------------------------------------------------------------------
$nodesDest = Join-Path $ngol "Nodes\CustomNodes\cs\blender"
New-Item -ItemType Directory -Path $nodesDest -Force | Out-Null
Copy-Item (Join-Path $root "nodes\*") $nodesDest -Recurse -Force

# pythonnet は既定で入れる。
# リポジトリは第三者のものを持たないが、組み立ては利用者の手元で起きるので、
# ここで取ってくれば「資材は綺麗・利用者の Blender には入っている」を両立できる。
# 非同梱で組むとノード自体が入らないため、あとから DLL を置いても使えない。
if (-not $NoPythonnet -and -not $PythonRuntime) {
    $local = Join-Path $root "nodes\lib\Python.Runtime.dll"
    if (Test-Path $local) {
        $PythonRuntime = $local
    }
    else {
        Write-Host "  pythonnet を取得します（-NoPythonnet で省けます）" -ForegroundColor DarkGray
        try {
            & (Join-Path $here "get_pythonnet.ps1") | Out-Null
            if (Test-Path $local) { $PythonRuntime = $local }
        }
        catch {
            Write-Host ("  取得できませんでした: {0}" -f $_.Exception.Message) -ForegroundColor DarkYellow
            Write-Host "  取得済みの DLL があるなら get_pythonnet.ps1 -From <path> で置いてください" -ForegroundColor DarkYellow
        }
    }
}

$wantPythonnet = [bool]$PythonRuntime

if ($PythonRuntime) {
    if (-not (Test-Path $PythonRuntime)) { throw "見つかりません: $PythonRuntime" }
    $libDest = Join-Path $nodesDest "lib"
    New-Item -ItemType Directory -Path $libDest -Force | Out-Null
    Copy-Item $PythonRuntime (Join-Path $libDest "Python.Runtime.dll") -Force
    $licSrc = Join-Path (Split-Path -Parent $PythonRuntime) "Python.Runtime.LICENSE.txt"
    if (Test-Path $licSrc) { Copy-Item $licSrc (Join-Path $libDest "Python.Runtime.LICENSE.txt") -Force }
    Write-Host "  pythonnet を同梱しました" -ForegroundColor DarkGray
} else {
    # nodes/* を丸ごと複写しているので、開発機の nodes/lib/ が紛れ込む。
    #    ノードだけ除いて DLL が残ると、第三者のものを黙って配ることになる。
    $strayLib = Join-Path $nodesDest "lib"
    if (Test-Path $strayLib) { Remove-Item $strayLib -Recurse -Force }

    # 参照が無いままだとコンパイルが失敗し、ログが赤くなって他の失敗が埋もれる。
    foreach ($f in @("BlenderPyNetNode.cs", "BlenderPyNetNode.rsp")) {
        $p = Join-Path $nodesDest $f
        if (Test-Path $p) { Remove-Item $p -Force }
    }
    Write-Host "  pythonnet は入れません (-PythonRuntime 未指定)。ノードも lib も落とした" -ForegroundColor DarkYellow
}

# --- Python の土台 -------------------------------------------------------------------
$pyDest = Join-Path $ngol "Nodes\CustomNodes\py"
New-Item -ItemType Directory -Path $pyDest -Force | Out-Null
Copy-Item (Join-Path $root "data\*.py") $pyDest -Force

# --- WebUI 拡張 ----------------------------------------------------------------------
$webuiSrc = Join-Path $root "webui-plugins"
if (Test-Path $webuiSrc) {
    $webuiDest = Join-Path $ngol "WebUI\plugins"
    New-Item -ItemType Directory -Path $webuiDest -Force | Out-Null
    Copy-Item (Join-Path $webuiSrc "*.js") $webuiDest -Force
}

# --- グラフ --------------------------------------------------------------------------
$graphSrc = Join-Path $root "graphs"
if (Test-Path $graphSrc) {
    $graphDest = Join-Path $ngol "Graphs"
    New-Item -ItemType Directory -Path $graphDest -Force | Out-Null
    Get-ChildItem -Path $graphSrc -Directory | ForEach-Object {
        $g = Join-Path $_.FullName "graph.json"
        if (Test-Path $g) {
            $id = (Get-Content $g -Raw | ConvertFrom-Json).id
            if (-not $id) { $id = $_.Name }
            Copy-Item $g (Join-Path $graphDest ("{0}.json" -f $id)) -Force
        }
    }
}

# --- 念のための掃除 ------------------------------------------------------------------
# マニフェストの paths_exclude_pattern でも落ちるが、空から組んでいる証拠として数を出す。
$junk = @()
foreach ($pat in @("host.log", "activator-error.log", "kvstore.db")) {
    $junk += Get-ChildItem -Path $pkg -Recurse -File -Filter $pat -ErrorAction SilentlyContinue
}
$junk += Get-ChildItem -Path $pkg -Recurse -Directory -Filter "__pycache__" -ErrorAction SilentlyContinue
$junk += Get-ChildItem -Path $pkg -Recurse -Directory -Filter "blender_bridge" -ErrorAction SilentlyContinue
if ($junk.Count -gt 0) {
    $junk | ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}
Write-Host ("  組み立て時に落とした実行時生成物: {0} 件 (0 が期待値)" -f $junk.Count) -ForegroundColor DarkGray

# --- 検証してから詰める --------------------------------------------------------------
& $BlenderExe --command extension validate $pkg
if ($LASTEXITCODE -ne 0) { throw "マニフェストの検証に失敗しました" }

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
& $BlenderExe --command extension build --source-dir $pkg --output-dir $OutDir
if ($LASTEXITCODE -ne 0) { throw "パッケージの作成に失敗しました" }

$zip = Get-ChildItem -Path $OutDir -Filter "ngol_for_blender-*.zip" |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1

# --- 出来たものを開いて確かめる ------------------------------------------------------
# 「作れた」だけでは何も言っていない。中身を見て、要るものが有り要らないものが無いことまで見る。
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip.FullName)
try {
    $names = $archive.Entries | ForEach-Object { $_.FullName }
    $bytes = ($archive.Entries | Measure-Object -Property Length -Sum).Sum

    $forbidden = @("blender_bridge/", "kvstore.db", ".log", "__pycache__")
    $bad = @()
    foreach ($f in $forbidden) { $bad += $names | Where-Object { $_ -like ("*" + $f + "*") } }

    $required = @("blender_manifest.toml", "__init__.py", "clr_host.py", "mainthread.py", "prefs.py")
    $missing = @()
    foreach ($r in $required) { if (-not ($names | Where-Object { $_ -like ("*" + $r) })) { $missing += $r } }

    # 頼んでいない第三者のものが入っていないか。
    #    実行時生成物だけ見ていて、一度これを見落として同梱版を配りかけた。
    $thirdParty = @($names | Where-Object { $_ -like "*Python.Runtime*" })
    $unexpected = @()
    if (-not $wantPythonnet -and $thirdParty.Count -gt 0) { $unexpected = $thirdParty }
}
finally { $archive.Dispose() }

Write-Host ""
if ($bad.Count -gt 0) {
    Write-Host "混入があります:" -ForegroundColor Red
    $bad | Select-Object -First 10 | ForEach-Object { Write-Host ("  " + $_) -ForegroundColor Red }
    throw "配布物に実行時生成物が混ざっています"
}
if ($missing.Count -gt 0) {
    throw ("必須ファイルが足りません: " + ($missing -join ", "))
}
if ($unexpected.Count -gt 0) {
    Write-Host "頼んでいない第三者のものが入っています:" -ForegroundColor Red
    $unexpected | ForEach-Object { Write-Host ("  " + $_) -ForegroundColor Red }
    throw "-PythonRuntime を指定していないのに pythonnet が混ざっています"
}

Write-Host "built" -ForegroundColor Green
Write-Host ("  {0}" -f $zip.FullName) -ForegroundColor DarkGray
Write-Host ("  {0:N0} 件 / zip {1:N1} MB / 展開後 {2:N1} MB" -f `
    $names.Count, ($zip.Length / 1MB), ($bytes / 1MB)) -ForegroundColor DarkGray
Write-Host "  混入 0 / 必須ファイルそろい" -ForegroundColor DarkGray

# 禁止リストは「禁止すると思いつかなかったもの」を見つけない。
#    一度それで第三者の DLL を見落として「混入 0」と報告した。
#    => 中身を並べて出す。想定外があれば読んだ人が気づける。
Write-Host ""
Write-Host "  中身:" -ForegroundColor DarkGray
$names | Where-Object { $_ -notlike "*/" } |
    Group-Object { $e = [System.IO.Path]::GetExtension($_); if ($e) { $e.ToLower() } else { "(拡張子なし)" } } |
    Sort-Object Count -Descending | Select-Object -First 8 |
    ForEach-Object { Write-Host ("    {0,-14} {1,4}" -f $_.Name, $_.Count) -ForegroundColor DarkGray }

Write-Host "  大きいもの:" -ForegroundColor DarkGray
$archive2 = [System.IO.Compression.ZipFile]::OpenRead($zip.FullName)
try {
    $archive2.Entries | Where-Object { $_.Length -gt 0 } |
        Sort-Object Length -Descending | Select-Object -First 5 |
        ForEach-Object { Write-Host ("    {0,8:N0} KB  {1}" -f ($_.Length / 1KB), $_.FullName) -ForegroundColor DarkGray }
}
finally { $archive2.Dispose() }

if ($KeepStaging) {
    Write-Host ("  staging: {0}" -f $staging) -ForegroundColor DarkGray
} else {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}

# 自分で組んだ NGOL 一式は自分で片付ける。渡されたものには触らない。
if ($staged -and (Test-Path $staged)) { Remove-Item $staged -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ""
Write-Host "入れ方: Blender へ zip をドラッグ&ドロップ、または" -ForegroundColor DarkGray
Write-Host ("  blender --command extension install-file -r user_default -e `"{0}`"" -f $zip.Name) -ForegroundColor DarkGray
