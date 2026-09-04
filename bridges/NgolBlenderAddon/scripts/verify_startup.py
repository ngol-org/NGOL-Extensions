"""起動の検証スクリプト。

    blender.exe -b --python verify_startup.py

background モード (-b) では bpy.app.timers が回らないので、ここでは使わない。
   NGOL は EnableDirectMode=true で自前スレッドを持つため、
   メインスレッドが sleep していても待ち受けは生きている。

環境変数:
    NGOL_TEST_PORT     既定 11165（番号で名指しして繋ぐので、既定の 11156 とは分けておく）
    NGOL_TEST_SECONDS  待ち受けを保つ秒数。既定 150
    NGOL_TEST_PIDFILE  Blender の PID を書き出す先（外から突き合わせるため）
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

port = int(os.environ.get("NGOL_TEST_PORT", "11165"))
seconds = float(os.environ.get("NGOL_TEST_SECONDS", "150"))
pidfile = os.environ.get("NGOL_TEST_PIDFILE", "")


def say(*args):
    print("[verify]", *args)
    sys.stdout.flush()


say("blender pid   =", os.getpid())
say("blender ver   =", bpy.app.version_string)
say("background    =", bpy.app.background)

if pidfile:
    with open(pidfile, "w", encoding="utf-8") as f:
        f.write(str(os.getpid()))

try:
    # 1) アドオンが見えているか。見えていないなら置き場所の問題で、注入以前の話。
    import addon_utils
    found = [m.__name__ for m in addon_utils.modules() if m.__name__ == MODULE]
    say("addon visible =", bool(found))
    if not found:
        say("ERROR: アドオンが走査されていません。置き場所を確認してください。")
        raise SystemExit(3)

    # 2) 有効化
    bpy.ops.preferences.addon_enable(module=MODULE)
    say("addon enabled = True")

    mod = sys.modules[MODULE]
    prefs = bpy.context.preferences.addons[MODULE].preferences
    prefs.port = port

    # 3) 起こす
    ok, message = mod.start_ngol(port)
    say("start ok      =", ok)
    say("start message =", message)
    if not ok:
        raise SystemExit(4)

    say("status        =", mod.clr_host.status())
    say("ngolRoot      =", mod.NGOL_ROOT)
    say("holding for %.0fs so the port can be probed from outside" % seconds)
    time.sleep(seconds)

    # 4) 止める（CoreCLR はプロセスに残る）
    mod.stop_ngol()
    say("stopped")

except SystemExit:
    raise
except Exception:
    say("EXCEPTION:\n" + traceback.format_exc())
    raise SystemExit(1)
