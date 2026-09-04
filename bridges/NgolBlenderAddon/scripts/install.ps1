# Blender へ入れるところまでを 1 本で行う。
#
#   pythonnet を取る -> パッケージを組む -> Blender へ入れて有効化 -> 入ったことを確かめる
#
# ドラッグ&ドロップでも入るが、それだと有効化が手作業になり、
# 入ったかどうかも目視でしか分からない。ここは最後の確認まで機械で行う。
#
# 使い方:
#   ./install.ps1
#   ./install.ps1 -NgolPortable <配布版>
#   ./install.ps1 -NoPythonnet          # pythonnet を入れない
#   ./install.ps1 -Autostart            # Blender 起動時に NGOL も起こす
#   ./install.ps1 -SkipVerify           # 確認を省く（速いが、入った証拠は残らない）
#
# -Autostart は Blender の環境設定へ書き込む。配布物の既定は切のまま
#    （CoreCLR は降ろせず、待ち受け口も開くので、黙って載る状態を既定にしない）。
#
# Blender が動いていると、終了時に設定が書き戻されて有効化が消えることがある。
#    既定では断る。承知のうえなら -Force。

param(
    [string]$NgolPortable   = "",
    [string]$BlenderExe     = "",
    [string]$BlenderVersion = "",
    [string]$Repo           = "user_default",
    [switch]$NoPythonnet,
    [switch]$Autostart,
    [switch]$SkipVerify,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$here = Split-Path -Parent $PSCommandPath
$root = Split-Path -Parent $here

# どの Blender を相手にするかは 1 箇所に集めてある。
# 置き場所を決め打ちにしない--ProgramFiles とは限らないし、携帯版は登録もされない。
. (Join-Path $here "_blender.ps1")

$BlenderExe = Resolve-BlenderExe -Hint $BlenderExe
$fromExe = Get-BlenderUserVersion -Exe $BlenderExe
if ($BlenderVersion -and $BlenderVersion -ne $fromExe) {
    throw "-BlenderVersion $BlenderVersion と -BlenderExe の版 $fromExe が食い違っています"
}
$BlenderVersion = $fromExe
Write-Host ("  Blender  : {0}  ({1})" -f $BlenderVersion, $BlenderExe) -ForegroundColor DarkGray

# 見るのは相手にする版だけ。別の版が動いていても関係ない。
$running = @(Get-Process -Name blender -ErrorAction SilentlyContinue | Where-Object {
    $path = $null
    try { $path = $_.Path } catch { }
    if (-not $path) { $true }
    else {
        $v = ""
        try { $v = Get-BlenderUserVersion -Exe $path } catch { }
        (-not $v) -or ($v -eq $BlenderVersion)
    }
})
if ($running.Count -gt 0 -and -not $Force) {
    $pids = ($running | ForEach-Object { $_.Id }) -join ", "
    throw ("Blender が動いています (pid: $pids)。終了してから実行してください。" +
           " 有効化の設定が、動いている側の終了時に書き戻されて消えることがあります。" +
           " 承知のうえなら -Force。")
}

# --- 1. 組む（pythonnet は build_package.ps1 が既定で取ってくる）----------------------
Write-Host "[1/3] パッケージを組みます" -ForegroundColor Cyan
$dist = Join-Path $root "dist"
$buildArgs = @{ NgolPortable = $NgolPortable; OutDir = $dist; BlenderExe = $BlenderExe }
if ($NoPythonnet) { $buildArgs["NoPythonnet"] = $true }
& (Join-Path $here "build_package.ps1") @buildArgs
if ($LASTEXITCODE -ne 0) { throw "パッケージの作成に失敗しました" }

$zip = Get-ChildItem -Path $dist -Filter "ngol_for_blender-*.zip" |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $zip) { throw "作った zip が見つかりません" }

# --- 2. 入れて有効にする -------------------------------------------------------------
Write-Host ""
Write-Host "[2/3] Blender へ入れて有効にします" -ForegroundColor Cyan
& $BlenderExe --command extension install-file -r $Repo -e $zip.FullName
if ($LASTEXITCODE -ne 0) { throw "導入に失敗しました" }

# 置き場は環境変数で動かせる。%APPDATA% 決め打ちにしない。
$installed = Join-Path (Get-BlenderExtensionsDir -Exe $BlenderExe -Version $BlenderVersion) "$Repo\ngol_for_blender"
if (-not (Test-Path $installed)) { throw "導入先が見当たりません: $installed" }

$files = @(Get-ChildItem -Path $installed -Recurse -File)
$manifestVersion = (Select-String -Path (Join-Path $installed "blender_manifest.toml") -Pattern '^version\s*=\s*"(.+)"').Matches.Groups[1].Value
$hasPythonnet = Test-Path (Join-Path $installed "ngol\Nodes\CustomNodes\cs\blender\lib\Python.Runtime.dll")

Write-Host ("  {0}" -f $installed) -ForegroundColor DarkGray
Write-Host ("  版 {0} / {1:N0} ファイル / {2:N1} MB / pythonnet {3}" -f `
    $manifestVersion, $files.Count, (($files | Measure-Object -Property Length -Sum).Sum / 1MB),
    $(if ($hasPythonnet) { "有" } else { "無" })) -ForegroundColor DarkGray

# --- 2.5 自動起動（頼まれたときだけ）------------------------------------------------
# 既定は切のまま。CoreCLR は降ろせず、待ち受け口も開くので、
#    Blender を開くたびに黙って載る状態を配布物の既定にはしない。
#    入れる人が明示的に選んだときだけ、環境設定へ書き込む。
if ($Autostart) {
    Write-Host ""
    Write-Host "[2.5] 自動起動を有効にします（Blender の環境設定に書き込みます）" -ForegroundColor Cyan

    $setScript = Join-Path ([System.IO.Path]::GetTempPath()) ("ngol_set_" + [System.Guid]::NewGuid().ToString("N").Substring(0,8) + ".py")
@"
import sys, bpy
sys.stdout.reconfigure(encoding="utf-8")
sys.path.insert(0, r"$here")
from _addon import resolve_module
MOD = resolve_module(prefer="$Repo")
bpy.ops.preferences.addon_enable(module=MOD)
bpy.context.preferences.addons[MOD].preferences.autostart = True
bpy.ops.wm.save_userpref()
print("[set] wrote autostart for", MOD)
"@ | Set-Content -Path $setScript -Encoding UTF8

    $readScript = Join-Path ([System.IO.Path]::GetTempPath()) ("ngol_read_" + [System.Guid]::NewGuid().ToString("N").Substring(0,8) + ".py")
@"
import sys, bpy
sys.stdout.reconfigure(encoding="utf-8")
sys.path.insert(0, r"$here")
from _addon import resolve_module
MOD = resolve_module(prefer="$Repo")
addon = bpy.context.preferences.addons.get(MOD)
print("[read] enabled  =", addon is not None)
print("[read] autostart =", addon.preferences.autostart if addon else None)
"@ | Set-Content -Path $readScript -Encoding UTF8

    try {
        & $BlenderExe -b --python $setScript 2>&1 | Select-String "^\[set\]" |
            ForEach-Object { Write-Host ("  " + $_.ToString().Trim()) -ForegroundColor DarkGray }

        # 書いた本人が読み返しても証明にならない。別のプロセスで読み直す。
        $read = & $BlenderExe -b --python $readScript 2>&1
        $read | Select-String "^\[read\]" |
            ForEach-Object { Write-Host ("  " + $_.ToString().Trim()) -ForegroundColor DarkGray }
        if (-not ($read | Select-String "\[read\] autostart = True")) {
            throw "自動起動の設定が残っていません（環境設定への書き込みに失敗した可能性）"
        }
    }
    finally {
        Remove-Item $setScript, $readScript -Force -ErrorAction SilentlyContinue
    }
}

# --- 3. 本当に動くかを確かめる -------------------------------------------------------
# 「入れた」は「入った」ではない。UI を持たない Blender で実際に起こして、
# ノードが登録されるところまで見る。
if ($SkipVerify) {
    Write-Host ""
    Write-Host "確認は省きました (-SkipVerify)" -ForegroundColor DarkYellow
}
else {
    Write-Host ""
    Write-Host "[3/3] 入ったことを確かめます（UI なしで起こして、ノードが登録されるまで見ます）" -ForegroundColor Cyan

    $probe = Join-Path ([System.IO.Path]::GetTempPath()) ("ngol_verify_" + [System.Guid]::NewGuid().ToString("N").Substring(0,8) + ".py")
    $port = 11190
@"
import os, sys, bpy
sys.stdout.reconfigure(encoding="utf-8")
sys.path.insert(0, r"$here")
from _addon import resolve_module
MOD = resolve_module(prefer="$Repo")
print("[verify] module   =", MOD)
bpy.ops.preferences.addon_enable(module=MOD)
print("[verify] enabled  =", MOD in bpy.context.preferences.addons)
mod = sys.modules[MOD]
ok, message = mod.start_ngol($port)
print("[verify] started  =", ok)
print("[verify] message  =", message)
mod.mainthread.pump_forever(25)
mod.stop_ngol()
print("[verify] stopped")
"@ | Set-Content -Path $probe -Encoding UTF8

    try {
        $out = & $BlenderExe -b --python $probe 2>&1
        $lines = $out | Select-String "^\[verify\]|registered .* node"
        $lines | ForEach-Object { Write-Host ("  " + $_.ToString().Trim()) -ForegroundColor DarkGray }

        $enabled = $out | Select-String "\[verify\] enabled  = True"
        $started = $out | Select-String "\[verify\] started  = True"
        $nodes   = $out | Select-String "registered (\d+) node\(s\).*?(\d+) failed"

        if (-not $enabled) { throw "有効化できませんでした" }
        if (-not $started) { throw "NGOL が起動しませんでした" }
        if ($nodes) {
            $n = [int]$nodes.Matches[0].Groups[1].Value
            $f = [int]$nodes.Matches[0].Groups[2].Value
            if ($f -gt 0) { throw ("ノードのコンパイルが {0} 件失敗しています" -f $f) }
            Write-Host ("  ノード {0} 件 / 失敗 0" -f $n) -ForegroundColor DarkGray
        }
    }
    finally {
        Remove-Item $probe -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ""
Write-Host "installed" -ForegroundColor Green
if ($Autostart) {
    Write-Host "  Blender を起動すれば NGOL も一緒に起きます。" -ForegroundColor DarkGray
    Write-Host "  やめるとき: Preferences > NGOL for Blender の「Blender 起動時に自動で起こす」を外す" -ForegroundColor DarkGray
} else {
    Write-Host "  Blender を起動して、View3D のサイドバー (N) > NGOL から起動してください。" -ForegroundColor DarkGray
    Write-Host "  毎回押すのが面倒なら install.ps1 -Autostart で入れ直すか、" -ForegroundColor DarkGray
    Write-Host "  Preferences > NGOL for Blender の「Blender 起動時に自動で起こす」を入れてください。" -ForegroundColor DarkGray
}
Write-Host "  外すとき: blender --command extension remove $Repo.ngol_for_blender" -ForegroundColor DarkGray
