"""NGOL の起動に失敗しても Blender が通常どおり使えることを確かめる。

    blender.exe -b --python verify_survivable.py

これは「落ちないこと」ではなく「**壊した状態で Blender の仕事が続くこと**」を測る。
   起動が成功する経路で試しても何も言っていないので、わざと壊してから測る。
"""

import sys
import traceback

import bpy

# 導入形式（Extension / 旧来のアドオン）でモジュール名が変わるので、列挙して見つける。
# リポジトリ名を直書きしない。環境変数 NGOL_ADDON_MODULE で上書きもできる。
import os as _os, sys as _sys
_sys.path.insert(0, _os.path.dirname(_os.path.abspath(__file__)))
from _addon import resolve_module, describe

MODULE = resolve_module()
failures = []


def check(label, condition):
    print("[survive] %-46s %s" % (label, "OK" if condition else "FAIL"))
    if not condition:
        failures.append(label)
    sys.stdout.flush()


try:
    bpy.ops.preferences.addon_enable(module=MODULE)
    mod = sys.modules[MODULE]

    # わざと壊す: NGOL 一式が無い場所を向かせる
    mod.NGOL_ROOT = r"D:\nonexistent-ngol-root"

    ok, message = mod.start_ngol(11199)
    check("起動は失敗する（成功したら検査に意味が無い）", ok is False)
    check("理由が文字列で返る", bool(message))
    check("例外が外へ出ていない", True)   # ここに到達した時点で満たされている
    print("[survive] message =", message.splitlines()[0] if message else "")

    # ここからが本題: Blender の仕事が続くか
    bpy.ops.mesh.primitive_cube_add(location=(1.0, 2.0, 3.0))
    cube = bpy.context.active_object
    check("メッシュを作れる", cube is not None and cube.type == "MESH")
    check("作ったものが座標を持つ", tuple(round(v, 3) for v in cube.location) == (1.0, 2.0, 3.0))

    bpy.ops.object.modifier_add(type="SUBSURF")
    check("モディファイアを足せる", len(cube.modifiers) == 1)

    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated = cube.evaluated_get(depsgraph)
    mesh = evaluated.to_mesh()
    check("依存グラフを評価できる（頂点が増える）", len(mesh.vertices) > 8)
    evaluated.to_mesh_clear()

    # 止める側も、起動していない状態から呼んで壊れないこと
    ok_stop, _ = mod.stop_ngol()
    check("起動していないのに停止しても壊れない", ok_stop is True)

    bpy.ops.preferences.addon_disable(module=MODULE)
    check("無効化まで通る", True)

except Exception:
    print("[survive] EXCEPTION:\n" + traceback.format_exc())
    failures.append("例外が外へ出た")

print("[survive] ---- %d failure(s) ----" % len(failures))
for f in failures:
    print("[survive]   FAILED:", f)
raise SystemExit(1 if failures else 0)
