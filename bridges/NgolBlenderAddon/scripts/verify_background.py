"""`-b`（UI なし実行）で、ノードから Blender を触るブリッジが成立するかを確かめる。

    blender.exe -b --python verify_background.py

「`bpy.app.timers` は `-b` では回らない」は**こちらの未検証の主張**だった。
   一般論として書いていただけなので、**まずそれ自体を測る。**

段取り:

    A. タイマーが本当に回らないのかを測る（回るなら以降は不要）
    B. 回らないなら、`mainthread.pump_once()` を手で回してブリッジが成立するか見る
       このスクリプトがメインスレッドを握っているので、ここで回すしかない
    C. 外から（別プロセスの WS クライアントから）ノードを呼んでもらい、
       **答えが返ることでブリッジの成立を判定する**

判定は「pump_once が例外を出さない」ではなく **「外からの要求に答えが返る」** で行う。

環境変数:
    NGOL_BG_PORT     既定 11167（番号で名指しして繋ぐので、既定の 11156 とは分けておく）
    NGOL_BG_SECONDS  ポンプを回す秒数。既定 120
"""

import os
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
PORT = int(os.environ.get("NGOL_BG_PORT", "11167"))
SECONDS = float(os.environ.get("NGOL_BG_SECONDS", "120"))


def say(*args):
    print("[bg]", *args)
    sys.stdout.flush()


say("blender pid =", os.getpid())
say("background  =", bpy.app.background)
say("port        =", PORT)

timer_fires = [0]


def _tick():
    timer_fires[0] += 1
    return 0.05


try:
    # ---- A. タイマーは本当に回らないのか ------------------------------------------
    bpy.app.timers.register(_tick, first_interval=0.05)
    say("A. timer registered =", bpy.app.timers.is_registered(_tick))
    time.sleep(3.0)
    say("A. 3 秒 sleep したあとの発火回数 =", timer_fires[0])
    timers_run = timer_fires[0] > 0
    say("A. 判定: bpy.app.timers は -b で",
        "回る" if timers_run else "回らない（主張どおり）")

    # ---- 起動 -------------------------------------------------------------------
    bpy.ops.preferences.addon_enable(module=MODULE)
    addon = bpy.context.preferences.addons.get(MODULE)
    addon.preferences.port = PORT
    mod = sys.modules[MODULE]
    ok, message = mod.start_ngol(PORT)
    say("NGOL start ok =", ok)
    say("message       =", message)
    if not ok:
        raise SystemExit(2)

    # ---- B/C. ポンプを手で回しながら、外からの要求を待つ ------------------------------
    # ここでこのスクリプトがメインスレッドを握っている。
    #   タイマーが回らないなら、ブリッジを生かせるのはこのループだけ。
    from ngol_for_blender import mainthread

    say("---- ここから %.0f 秒、pump_once() を回して待つ ----" % SECONDS)
    say("---- 外から blender.ping などを呼んでください（port %d） ----" % PORT)

    served_before = mainthread.status().get("served", 0)
    deadline = time.monotonic() + SECONDS
    last_report = 0.0
    pumps = 0

    while time.monotonic() < deadline:
        # タイマーが回るならこれは要らない。回らない前提でも動くようにしておく。
        mainthread.pump_once()
        pumps += 1
        time.sleep(0.02)

        now = time.monotonic()
        if now - last_report >= 10.0:
            last_report = now
            st = mainthread.status()
            say("... pumps=%d  served=%d  failed=%d  timer_fires=%d"
                % (pumps, st.get("served", 0), st.get("failed", 0), timer_fires[0]))

    st = mainthread.status()
    served = st.get("served", 0)
    say("---- 終了 ----")
    say("pump_once の回数      =", pumps)
    say("捌いた要求の数        =", served, "(開始時 %d)" % served_before)
    say("失敗した要求の数      =", st.get("failed", 0))
    say("timer の発火回数      =", timer_fires[0])
    say("判定: 外からの要求に答えられたか =", served > served_before)

    mod.stop_ngol()
    say("stopped")

except Exception:
    say("EXCEPTION:\n" + traceback.format_exc())
    raise SystemExit(1)
