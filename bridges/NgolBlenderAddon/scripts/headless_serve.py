"""`-b`（UI なし実行）で NGOL を起こし、ブリッジを生かしたまま待つ。

    blender.exe -b --python headless_serve.py
    blender.exe -b <file.blend> --python headless_serve.py

`-b` では `bpy.app.timers` が**回らない**（実測: 0.05 秒間隔で 3 秒待って発火 0 回）。
    タイマーの登録は成功するのに発火しないので、黙っていると
    **「ノードは繋がるのに Blender 側が一切答えない」**という、原因の分かりにくい状態になる。

=> `-b` では **このスクリプトがメインスレッドをブリッジに貸す**。それが `pump_forever()`。

これで出来るようになること:

    - CI・自動テスト（グラフを走らせて結果を突き合わせる）
    - バッチ処理（大量の .blend に同じノードグラフを当てる）
    - サーバー側処理（要求を受けて Blender で生成して返す）

出来ないこと: `blender.capture`（ウィンドウが無い）、
    blender.tick.probe のネイティブフック tick（描画が起きない）。どちらも理由を返して断る。

環境変数:
    NGOL_PORT     待ち受けポート。既定 11167（番号で名指しして繋ぐので、既定の 11156 とは分けておく）
    NGOL_SECONDS  待つ秒数。0 なら止められるまで。既定 600
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
PORT = int(os.environ.get("NGOL_PORT", "11167"))
SECONDS = float(os.environ.get("NGOL_SECONDS", "600"))


def say(*args):
    print("[headless]", *args)
    sys.stdout.flush()


try:
    say("pid        =", os.getpid())
    say("background =", bpy.app.background)
    if not bpy.app.background:
        say("これは -b 用。GUI では通常のタイマーが回るので、このスクリプトは要らない")

    bpy.ops.preferences.addon_enable(module=MODULE)
    addon = bpy.context.preferences.addons.get(MODULE)
    if addon is None:
        say("アドオンが見つかりません。deploy.ps1 で配置してください")
        raise SystemExit(2)
    addon.preferences.port = PORT

    mod = sys.modules[MODULE]
    ok, message = mod.start_ngol(PORT)
    say("start ok   =", ok)
    say("message    =", message)
    if not ok:
        raise SystemExit(3)

    say("待ち受け中: http://127.0.0.1:%d/  （%s）"
        % (PORT, "止められるまで" if SECONDS <= 0 else "%.0f 秒" % SECONDS))

    # ここから戻ってこない。メインスレッドをブリッジに貸している。
    stats = mod.mainthread.pump_forever(SECONDS)
    say("pump 結果  =", stats)

    mod.stop_ngol()
    say("stopped")

except SystemExit:
    raise
except Exception:
    say("EXCEPTION:\n" + traceback.format_exc())
    raise SystemExit(1)
