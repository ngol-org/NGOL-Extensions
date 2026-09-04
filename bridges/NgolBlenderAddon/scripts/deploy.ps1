# NgolBlenderAddon を Blender の利用者側フォルダへ配置する。
#
# Blender 本体のフォルダ (C:\Program Files\...) には何も置かない。
#    置くのは %APPDATA%\Blender Foundation\Blender\<ver>\ の下だけ。
#
# 既定の置き場は Extension 形式の場所:
#   %APPDATA%\...\<ver>\extensions\user_default\ngol_for_blender\
# 旧来のアドオン形式へ置きたいときは -Legacy を付ける:
#   %APPDATA%\...\<ver>\scripts\addons\ngol_for_blender\
#
# 使い方:
#   ./deploy.ps1 -NgolRuntime <リポジトリ>\build\runtime   # Blender の版は自分で見つける
#   ./deploy.ps1 -BlenderVersion 5.2 -Force
#   ./deploy.ps1 -AddonOnly -Force   # NGOL 一式は触らずノード・土台・拡張だけ入れ替える
#   ./deploy.ps1 -Legacy              # 旧来の scripts\addons\ へ
#   ./deploy.ps1 -NgolPortable <配布版>  # 手元の配布版から配る
#   ./deploy.ps1 -Clean               # 今回配らなかったノードのソースも落とす

param(
    [string]$BlenderVersion = "",
    [string]$BlenderExe = "",
    [string]$NgolPortable = "",
    # 配布版を展開したフォルダではなく、組み上がった runtime そのものを渡すとき。
    # 指すものが違うので別の名前にしてある（渡し間違いは静かに通るため）。
    [string]$NgolRuntime = "",
    [switch]$AddonOnly,
    [switch]$Legacy,
    [switch]$Force,
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$here       = Split-Path -Parent $PSCommandPath
$root       = Split-Path -Parent $here
$addonSrc  = Join-Path $root "addon\ngol_for_blender"

if (-not (Test-Path $addonSrc)) { throw "addon source not found: $addonSrc" }

# --- どの Blender へ置くか --------------------------------------------------------
# 版を決め打ちにすると、その版が無い機械では別のフォルダを作って「置けた」と言ってしまう。
# Blender は読まないので、症状は「入れたのに出ない」になり、原因が見えない。
. (Join-Path $here "_blender.ps1")

$exe = ""
if ($BlenderExe) {
    # 携帯版（zip を展開しただけのもの）は Windows に登録されないので、自動では見つからない。
    #    渡されたなら、それが答え。
    $exe = Resolve-BlenderExe -Hint $BlenderExe
    $fromExe = Get-BlenderUserVersion -Exe $exe
    if ($BlenderVersion -and $BlenderVersion -ne $fromExe) {
        throw "-BlenderVersion $BlenderVersion と -BlenderExe の版 $fromExe が食い違っています"
    }
    $BlenderVersion = $fromExe
    Write-Host ("  版       : {0}  ({1})" -f $BlenderVersion, $exe) -ForegroundColor DarkGray
}

if (-not $BlenderVersion) {
    try { $exe = Resolve-BlenderExe } catch { $exe = "" }
    if ($exe) {
        $BlenderVersion = Get-BlenderUserVersion -Exe $exe
        Write-Host ("  版       : {0}  ({1})" -f $BlenderVersion, $exe) -ForegroundColor DarkGray
    }
    else {
        # Blender が入っていなくても、利用者フォルダだけ在ることがある（消したあと等）。
        $appdataRoot = Join-Path $env:APPDATA "Blender Foundation\Blender"
        $found = @(Get-ChildItem $appdataRoot -Directory -ErrorAction SilentlyContinue |
                   Where-Object { $_.Name -match '^\d+\.\d+$' } |
                   Sort-Object { [version]$_.Name } -Descending)
        if ($found.Count -eq 0) {
            throw "Blender が見つかりません。-BlenderVersion か -BlenderExe で指定してください"
        }
        $BlenderVersion = $found[0].Name
        Write-Host ("  版       : {0}  (利用者フォルダから)" -f $BlenderVersion) -ForegroundColor DarkGray
    }
}

# 置き場は環境変数で動かせる（BLENDER_USER_RESOURCES）。%APPDATA% 決め打ちにしない。
$userDir = Get-BlenderUserRoot -Version $BlenderVersion
if (-not (Test-Path $userDir)) {
    # 版だけ渡された場合は作らない。打ち間違いだと、Blender が読まないフォルダだけが残る。
    # 実行ファイルが分かっているなら話が別--版はそこから読んだので確かで、
    #   携帯版は一度も GUI で起動していないと利用者フォルダがまだ無い（-b では作られない）。
    if (-not $exe) {
        $have = @(Get-ChildItem (Split-Path -Parent $userDir) -Directory -ErrorAction SilentlyContinue |
                  Where-Object { $_.Name -match '^\d+\.\d+$' } | ForEach-Object { $_.Name })
        throw ("利用者フォルダがありません: $userDir  " +
               $(if ($have) { "この機械にあるのは: " + ($have -join ", ") } else { "版のフォルダが 1 つもありません" }))
    }
    New-Item -ItemType Directory -Path $userDir -Force | Out-Null
    Write-Host ("  利用者   : {0}  (まだ無かったので作りました)" -f $userDir) -ForegroundColor DarkGray
}

# --- NGOL 一式をどこから取るか ----------------------------------------------------
# scripts/build.ps1 で組んだものを -NgolRuntime で渡す。配布版を持っている人は -NgolPortable で渡せる。
if ($AddonOnly) {
    $runtimeSrc = ""   # 使わない
}
elseif ($NgolRuntime) {
    $runtimeSrc = $NgolRuntime
    if (-not (Test-Path $runtimeSrc)) { throw "NGOL runtime not found: $runtimeSrc" }
}
elseif ($NgolPortable) {
    $runtimeSrc = Join-Path $NgolPortable "runtime"
    if (-not (Test-Path $runtimeSrc)) { throw "NGOL runtime not found: $runtimeSrc" }
}
else {
    throw ("NGOL 一式が渡されていません。先に組み立ててから渡してください。" +
           [Environment]::NewLine + "  scripts/build.ps1" +
           [Environment]::NewLine + "  deploy.ps1 -NgolRuntime <リポジトリ>\build\runtime")
}

if ($Legacy) {
    $dest = Join-Path $userDir "scripts\addons\ngol_for_blender"
} else {
    $dest = Join-Path $userDir "extensions\user_default\ngol_for_blender"
}
$ngolDest = Join-Path $dest "ngol"

# 起動中の Blender は自分のアドオンを掴んだままなので、上書きが途中で失敗し
#    「古くも新しくもないフォルダ」が残る。既定では断る。
# 見るのは「配る先を掴んでいる Blender」だけ。版が違うものが動いていても関係ない
#    （複数の版を並べている機械では、別の版が起動しているのが普通）。
$running = @(Get-Process -Name blender -ErrorAction SilentlyContinue | Where-Object {
    $path = $null
    try { $path = $_.Path } catch { }
    if (-not $path) { $true }   # 版が読めないなら、掴んでいるかもしれないので断る側に倒す
    else {
        $v = ""
        try { $v = Get-BlenderUserVersion -Exe $path } catch { }
        (-not $v) -or ($v -eq $BlenderVersion)
    }
})
if ($running.Count -gt 0 -and -not $Force) {
    $pids = ($running | ForEach-Object { $_.Id }) -join ", "
    throw ("Blender $BlenderVersion が動いています (pid: $pids)。終了してから配置してください。" +
           " ノード・土台・WebUI 拡張だけの差し替えなら -AddonOnly -Force で通せます。")
}

New-Item -ItemType Directory -Path $dest -Force | Out-Null

# --- アドオン本体 (.py) と マニフェスト -------------------------------------------
Get-ChildItem -Path $addonSrc -Filter *.py -File | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $dest $_.Name) -Force
    Write-Host ("  py       : {0}" -f $_.Name) -ForegroundColor DarkGray
}

# Extension 形式ではマニフェストが無いと読み込まれない。
$manifest = Join-Path $addonSrc "blender_manifest.toml"
if (Test-Path $manifest) {
    Copy-Item $manifest (Join-Path $dest "blender_manifest.toml") -Force
    Write-Host "  manifest : blender_manifest.toml" -ForegroundColor DarkGray
}

# 前回の .pyc が残っていると古いコードが動くことがある。消しておく。
$pycache = Join-Path $dest "__pycache__"
if (Test-Path $pycache) { Remove-Item $pycache -Recurse -Force }

# --- NGOL 一式 ----------------------------------------------------------------------
if (-not $AddonOnly) {
    New-Item -ItemType Directory -Path $ngolDest -Force | Out-Null
    Copy-Item (Join-Path $runtimeSrc "*") $ngolDest -Recurse -Force

    # 実行時に出来るものは配布物ではない。混ざっていたら落とす。
    foreach ($junk in @("host.log", "kvstore.db", "activator-error.log")) {
        $p = Join-Path $ngolDest $junk
        if (Test-Path $p) { Remove-Item $p -Force }
    }
    Write-Host ("  runtime  : {0}" -f $ngolDest) -ForegroundColor DarkGray
}

# --- NGOL 側の中身 -------------------------------------------------------------------
# ここはアドオンではなく NGOL の territory。ホットリロードで回るので、
# Blender が起動したままでも入れ替えてよい（-AddonOnly でもここは配る）。
$nodesSrc = Join-Path $root "nodes"
$dataSrc  = Join-Path $root "data"
$webuiSrc = Join-Path $root "webui-plugins"
$graphSrc = Join-Path $root "graphs"

if (Test-Path $nodesSrc) {
    $nodesDest = Join-Path $ngolDest "Nodes\CustomNodes\cs\blender"
    New-Item -ItemType Directory -Path $nodesDest -Force | Out-Null

    # lib\ は動いている NGOL が掴んでいる。Roslyn が .rsp の /r: で参照アセンブリとして
    #    開くため、コンパイルに失敗した後でも握られたままになる。
    #    ここで一緒に上書きしようとすると、掴まれた 1 個のせいで .cs の差し替えごと失敗する
    #      --ホットリロードのために用意した -AddonOnly が、いちばん使いたい場面で通らなくなる。
    # => .cs と .rsp は毎回配る。lib\ は無いときだけ置く（中身を変えるなら Blender を止める）。
    Get-ChildItem $nodesSrc -Exclude 'lib' | ForEach-Object {
        Copy-Item $_.FullName $nodesDest -Recurse -Force
    }
    Write-Host ("  nodes    : {0}" -f $nodesDest) -ForegroundColor DarkGray

    $libSrc = Join-Path $nodesSrc "lib"
    if (Test-Path $libSrc) {
        $libDest = Join-Path $nodesDest "lib"
        if (Test-Path (Join-Path $libDest "Python.Runtime.dll")) {
            Write-Host "  lib      : 既にあるので触りません（差し替えるなら Blender を終了して実行）" -ForegroundColor DarkGray
        } else {
            Copy-Item $libSrc $nodesDest -Recurse -Force
            Write-Host ("  lib      : {0}" -f $libDest) -ForegroundColor DarkGray
        }
    }

    # pythonnet を使うノードは Python.Runtime.dll が要る。
    # 無いまま配ると「参照が見つからない」でコンパイルが 1 件失敗し、
    #    ログが赤くなって他の失敗が埋もれる。要るものが無いなら、そのノードは配らない。
    $pyNetDll = Join-Path $nodesDest "lib\Python.Runtime.dll"
    if (-not (Test-Path $pyNetDll)) {
        foreach ($f in @("BlenderPyNetNode.cs", "BlenderPyNetNode.rsp")) {
            $p = Join-Path $nodesDest $f
            if (Test-Path $p) { Remove-Item $p -Force }
        }
        Write-Host "  (skip)   : BlenderPyNetNode - lib\Python.Runtime.dll が無いため配りません" -ForegroundColor DarkYellow
    }

    # リポジトリから消したノードは、上書きでは消えないので配備先で動き続ける。
    #    畳んで消したノードが、消したあともずっと登録され続けていた実例がある。
    # 見るのはノードのソースだけ。グラフ・WebUI 拡張・実行時に出来るものは
    #    同じ木の中にあるが、動かしている人のものなので触らない。
    # lib\ は「無いときだけ置く」運用なので、置いていなくても残骸ではない。
    # 既定では消さない。この場所には利用者が自分のノードを置くこともあり、
    #    git の外なので、消し間違えると戻せない。
    $placed = @(Get-ChildItem $nodesSrc -Recurse -File |
                ForEach-Object { $_.FullName.Substring($nodesSrc.Length + 1) })
    $ndLen = (Get-Item $nodesDest).FullName.TrimEnd('\').Length + 1
    $stale = @(Get-ChildItem $nodesDest -Recurse -File |
               ForEach-Object { $_.FullName.Substring($ndLen) } |
               Where-Object { $_ -notlike 'lib\*' -and $placed -notcontains $_ })

    if ($stale.Count -gt 0) {
        $note = if ($Clean) { "消します" } else { "-Clean で消せます" }
        Write-Host ("  残骸     : このスクリプトが置いていないもの {0} 件（{1}）" -f $stale.Count, $note) -ForegroundColor DarkYellow
        foreach ($s in $stale) { Write-Host ("             {0}" -f $s) -ForegroundColor DarkYellow }
        if ($Clean) {
            # 挙げたものだけを消す。ここで走査し直すと、2 回の走査の間に増えたものまで
            #    消してしまい、利用者に見せた一覧と違うことをすることになる。
            foreach ($s in $stale) { Remove-Item (Join-Path $nodesDest $s) -Force -ErrorAction SilentlyContinue }
        }
    }
}

if (Test-Path $dataSrc) {
    $pyDest = Join-Path $ngolDest "Nodes\CustomNodes\py"
    New-Item -ItemType Directory -Path $pyDest -Force | Out-Null
    Copy-Item (Join-Path $dataSrc "*.py") $pyDest -Force
    # Python は import 済みを覚えている。前回の .pyc が残っていると
    #    古い実装が動き続ける（「直したのに効かない」の典型）。
    $pyCache = Join-Path $pyDest "__pycache__"
    if (Test-Path $pyCache) { Remove-Item $pyCache -Recurse -Force }
    Write-Host ("  data     : {0}" -f $pyDest) -ForegroundColor DarkGray
}

# WebUI 拡張は .cs が要らない。ここへ置くだけで出る。
# サーバーは plugins/ を要求のたびに走査するので NGOL の再起動も不要
# （NodeGraphModLab.Core/Server/WebUiPluginManifest.cs を読んで確認）。
# 本体（WebUI 直下の src / assets）には触らない。公式ガイドが禁止している。
if (Test-Path $webuiSrc) {
    $webuiDest = Join-Path $ngolDest "WebUI\plugins"
    New-Item -ItemType Directory -Path $webuiDest -Force | Out-Null
    Copy-Item (Join-Path $webuiSrc "*.js") $webuiDest -Force
    Write-Host ("  webui    : {0}" -f $webuiDest) -ForegroundColor DarkGray
}

# graphs\<名前>\graph.json を NGOL の Graphs\<id>.json として配る。
# 同じ id のものは上書きする。利用者が同じ id で編集していれば、その内容は戻せない。
#    自動化から呼ばれるので確認は求めない。代わりに、上書きしたことを必ず出す。
#    残したいものは、この見本と違う id を付けて別に運ぶこと。
if (Test-Path $graphSrc) {
    $graphDest = Join-Path $ngolDest "Graphs"
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
                Write-Host ("  graph    : {0}  (上書き)" -f $id) -ForegroundColor DarkYellow
            } else {
                Write-Host ("  graph    : {0}" -f $id) -ForegroundColor DarkGray
            }
        }
    }
}

# 自分で組んだ一時フォルダは自分で片付ける。渡されたものには触らない。
if ($staged -and (Test-Path $staged)) { Remove-Item $staged -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ""
Write-Host "deployed" -ForegroundColor Green
Write-Host ("  {0}" -f $dest) -ForegroundColor DarkGray
Write-Host ""
if ($Legacy) {
    Write-Host "Blender で Edit > Preferences > Add-ons から 'NGOL for Blender' を有効にし、" -ForegroundColor DarkGray
} else {
    Write-Host "Blender で Edit > Preferences > Get Extensions（または Add-ons）から有効にし、" -ForegroundColor DarkGray
}
Write-Host "'NGOL を起動' を押してください。" -ForegroundColor DarkGray
