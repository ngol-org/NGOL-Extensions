"""アドオンの設定。

ここは「決める」層。ここで CLR を触らない（触るのは clr_host.py だけ）。
"""

from __future__ import annotations

import bpy

from . import clr_host

# NGOL 既定と同じ番号にする。使用中なら NGOL が空きへ移るので、
# ブリッジごとに番号を分ける必要はない。実際の口はパネルに出る。
DEFAULT_PORT = 11156


class NgolForBlenderPreferences(bpy.types.AddonPreferences):
    bl_idname = __package__

    port: bpy.props.IntProperty(
        name="Port",
        description=(
            "WebUI が待ち受けるポート（127.0.0.1 のみ）。"
            "変更は次回の起動から効く。"
            "使用中なら NGOL が次の番号を探すので、実際の番号はログで確認すること"
        ),
        default=DEFAULT_PORT,
        min=1024,
        max=65535,
    )

    autostart: bpy.props.BoolProperty(
        name="Blender 起動時に自動で起こす",
        description=(
            "既定は切。仕込みを自動化しない方針のため、"
            "通常は利用者が明示的に起動する"
        ),
        default=False,
    )

    def draw(self, context):
        layout = self.layout
        info = clr_host.status()

        box = layout.box()
        row = box.row()
        row.label(text="状態", icon="INFO")
        if info["running"]:
            row.label(text="起動中", icon="CHECKMARK")
        elif info["clr_loaded"]:
            row.label(text="停止中（.NET は読み込み済み）", icon="PAUSE")
        else:
            row.label(text="停止中", icon="X")

        col = box.column(align=True)
        col.label(text="プロセス ID: %d  (この Blender の中で動く)" % info["pid"])
        # 下の Port は設定値。実際に開いた口はこちらで、使用中なら NGOL が空きへ移る。
        col.label(text="待ち受け: %s" % (("port %d" % info["port"]) if info["port"] else "（まだ無い）"))
        col.label(text="ngolRoot: %s" % (info["ngol_root"] or _default_root()))
        col.label(text="hostfxr: %s" % (info["hostfxr_path"] or "（未解決）"))

        row = layout.row(align=True)
        row.operator("ngol.start", icon="PLAY")
        row.operator("ngol.stop", icon="PAUSE")
        row.operator("ngol.open_webui", icon="URL")

        layout.prop(self, "port")
        layout.prop(self, "autostart")

        note = layout.box()
        note.label(text=".NET 8 ランタイム (win-x64) が必要です", icon="ERROR")
        note.label(text="待ち受けは 127.0.0.1 のみ。外部へは出しません")


def _default_root() -> str:
    import os
    return os.path.join(os.path.dirname(os.path.abspath(__file__)), "ngol")


def get(context) -> "NgolForBlenderPreferences":
    return context.preferences.addons[__package__].preferences
