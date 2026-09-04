"""アドオンとしての **本来の利用経路** を一巡させる。

    blender.exe --python verify_ui_cycle.py

ここまでの検証は毎回 `start_in_blender.py`（検証用スクリプト）で起こしていた。
   つまり **利用者が実際に通る道は一度も通っていない**。
   ボタンが呼ぶのはオペレータなので、それを順に叩いて確かめる。

確かめること:

    1. `addon_enable`            アドオンが有効になり、オペレータが生える
    2. `bpy.ops.ngol.start()`    ボタンと同じ経路で NGOL が起きる
    3. `bpy.ops.ngol.stop()`     待ち受けが畳まれる
    4. `bpy.ops.ngol.start()` **もう一度起きる**
         CoreCLR は降ろせないので、保持した関数ポインタで Init を呼び直す経路。
           ここが通らないと「無効化したら二度と起こせない」ことになる
    5. `bpy.ops.script.reload()` のあとでも起こせる
         モジュールが作り直されるので、`sys` へ隠した状態が生き残る必要がある
    6. `addon_disable`           無効化で待ち受けが畳まれる

判定は「オペレータが FINISHED を返したか」ではなく **ポートが実際に応答するか**で行う。
"""

import socket
import sys
import time
import traceback

import bpy

# 導入形式（Extension / 旧来のアドオン）でモジュール名が変わるので、列挙して見つける。
# リポジトリ名を直書きしない。環境変数 NGOL_ADDON_MODULE で上書きもできる。
import os as _os, sys as _sys
_sys.path.insert(0, _os.path.dirname(_os.path.abspath(__file__)))
from _addon import resolve_module, describe

MODULE = resolve_module()
# 番号で名指しして繋ぐので、既定の 11156 とは分けておく。
PORT = 11165
results = []


def say(*args):
    print("[uicycle]", *args)
    sys.stdout.flush()


def listening(port: int = PORT, timeout: float = 1.0) -> bool:
    """症状そのものを測る: そのポートが実際に受けるか。"""
    try:
        with socket.create_connection(("127.0.0.1", port), timeout=timeout):
            return True
    except OSError:
        return False


def wait_until(expected: bool, seconds: float = 25.0) -> bool:
    """状態が変わるまで待つ。起動は数秒かかるので、一度見て決めない。"""
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if listening() == expected:
            return True
        time.sleep(0.5)
    return listening() == expected


def check(label: str, ok: bool, detail: str = ""):
    results.append((label, ok, detail))
    say("%-52s %s%s" % (label, "OK" if ok else "FAIL", ("  " + detail) if detail else ""))


say("blender pid  =", __import__("os").getpid())
say("background   =", bpy.app.background)
say("port         =", PORT)

try:
    # ---- 前提: 最初は誰も待ち受けていないこと -------------------------------------
    check("開始時点でポートが空いていない", not listening(),
          "" if not listening() else "別の NGOL が動いている。検証にならない")

    # ---- 1. 有効化 -------------------------------------------------------------
    bpy.ops.preferences.addon_enable(module=MODULE)
    addon = bpy.context.preferences.addons.get(MODULE)
    check("1. アドオンが有効になる", addon is not None)
    check("1. オペレータが生えている", hasattr(bpy.ops.ngol, "start"))

    # ボタンと同じ既定値を使う。ここで port を書き換えない（利用者の設定を尊重）
    if addon is not None:
        addon.preferences.port = PORT

    # 有効化しただけで起きてはいけない（自動起動は既定で切）
    check("1. 有効化しただけでは起きない", not listening())

    # ---- 2. 起動ボタン ---------------------------------------------------------
    r = bpy.ops.ngol.start()
    check("2. start オペレータが FINISHED", "FINISHED" in r, str(r))
    check("2. ポートが実際に応答する", wait_until(True))

    # ---- 3. 停止ボタン ---------------------------------------------------------
    r = bpy.ops.ngol.stop()
    check("3. stop オペレータが FINISHED", "FINISHED" in r, str(r))
    check("3. ポートが閉じる", wait_until(False))

    # ---- 4. 再起動（CoreCLR は降ろせない経路） ------------------------------------
    r = bpy.ops.ngol.start()
    check("4. 2 度目の start が FINISHED", "FINISHED" in r, str(r))
    check("4. もう一度ポートが応答する", wait_until(True))

    # ---- 5. Reload Scripts をまたぐ ---------------------------------------------
    # ここでモジュールが作り直される。clr_host は状態を sys に隠しているので
    #   生き残るはず--「はず」を確かめるのがこの節。
    before = listening()
    bpy.ops.script.reload()
    check("5. script.reload を通過できた", True)
    check("5. reload 後もアドオンが居る",
          bpy.context.preferences.addons.get(MODULE) is not None)

    after_reload_listening = listening()
    say("5. reload 直後の待ち受け:", after_reload_listening, "(reload 前:", before, ")")

    # reload で unregister->register が走るので、いったん停止しているはず。
    # 肝心なのは「そのあと起こし直せるか」。
    if after_reload_listening:
        bpy.ops.ngol.stop()
        wait_until(False)
    r = bpy.ops.ngol.start()
    check("5. reload 後でも起こし直せる", "FINISHED" in r and wait_until(True), str(r))

    # ---- 6. 無効化 -------------------------------------------------------------
    bpy.ops.preferences.addon_disable(module=MODULE)
    check("6. 無効化で待ち受けが畳まれる", wait_until(False))

except Exception:
    say("EXCEPTION:\n" + traceback.format_exc())
    results.append(("例外が外へ出た", False, ""))

failed = [r for r in results if not r[1]]
say("---- %d / %d 合格 ----" % (len(results) - len(failed), len(results)))
for label, ok, detail in failed:
    say("  FAILED:", label, detail)

if bpy.app.background:
    raise SystemExit(1 if failed else 0)
say("（GUI モードなのでこのまま残る。画面で確認できます）")
