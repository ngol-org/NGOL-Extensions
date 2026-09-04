"""GUI の Blender で、アドオンを有効にして NGOL を起こす。

    blender.exe --python start_in_blender.py

background モード用の verify_startup.py と違い、ここでは待たない。
   GUI ではこのスクリプトが終わったあとも Blender のイベントループが回り続けるので、
   NGOL は起きたまま残る。

環境変数:
    NGOL_TEST_PORT      既定 11165（番号で名指しして繋ぐので、既定の 11156 とは分けておく）
    NGOL_TEST_PIDFILE   Blender の PID を書き出す先
"""

import os
import sys
import traceback

import bpy

# 導入形式（Extension / 旧来のアドオン）でモジュール名が変わるので、列挙して見つける。
# リポジトリ名を直書きしない。環境変数 NGOL_ADDON_MODULE で上書きもできる。
import os as _os, sys as _sys
_sys.path.insert(0, _os.path.dirname(_os.path.abspath(__file__)))
from _addon import resolve_module, describe

MODULE = resolve_module()

port = int(os.environ.get("NGOL_TEST_PORT", "11165"))
pidfile = os.environ.get("NGOL_TEST_PIDFILE", "")


def say(*args):
    print("[start]", *args)
    sys.stdout.flush()


say("blender pid  =", os.getpid())
say("background   =", bpy.app.background)
if pidfile:
    with open(pidfile, "w", encoding="utf-8") as f:
        f.write(str(os.getpid()))

try:
    bpy.ops.preferences.addon_enable(module=MODULE)
    mod = sys.modules[MODULE]
    bpy.context.preferences.addons[MODULE].preferences.port = port

    ok, message = mod.start_ngol(port)
    say("start ok     =", ok)
    say("message      =", message)
    say("status       =", mod.clr_host.status())
    # 設定は保存しない。利用者の環境を勝手に変えない。
except Exception:
    say("EXCEPTION:\n" + traceback.format_exc())
