"""NGOL for Blender - Blender のプロセスの中で NGOL を起こすアドオン。

**このアドオンの仕事は 2 つだけ。**

    1) Blender のプロセスの中で NGOL を起こす           (clr_host.py)
    2) 渡された Python をメインスレッドで走らせる口を開ける (mainthread.py)

**Blender で何をするかは、ここに書かない。** それはノードとグラフが決める。

    ngolRoot/Nodes/CustomNodes/cs/blender/  ... C# ノード（ポート・合成・bpy を要さない処理）
    ngolRoot/Nodes/CustomNodes/py/          ... Python の土台（bpy に触る処理）

  どちらもホットリロードで回るので、**機能を増やすのに Blender の再起動が要らない。**
  => アドオンは固定。増えるのは NGOL 側だけ。

**どちらに書くかを「処理の重さ」で決めないこと。**
   Python 側は **Blender のメインスレッド**で走るので、長い処理を置くと Blender が固まる。
   C# 側は NGOL 自前のスレッドなので、**重い計算はむしろ C# のほうが速く、Blender も止めない。**
   軸は 1)`bpy` が要るか 2)境界を何度跨ぐか 3)メインスレッドをどれだけ占有するか。

このファイルは「決める」層。起動の実体は clr_host.py にあり、
   そちらは ``bpy`` を知らない（Blender の外でも同じ経路を通せる＝オラクルになる）。
"""

# bl_info は置かない。Extension 形式では blender_manifest.toml が正で、
#    両方あると食い違ったときにどちらが効いているか分からなくなる。

import os
import traceback

import bpy

from . import mainthread
from . import clr_host
from . import prefs as _prefs

ADDON_DIR = os.path.dirname(os.path.abspath(__file__))
NGOL_ROOT = os.path.join(ADDON_DIR, "ngol")

# 最後に起きたことを覚えておく。失敗を握りつぶさず、必ず利用者に見える場所へ出すため。
_last_message = ""
_last_error = ""


# --------------------------------------------------------------------------------------
# 「配る」層 - 状態を持ち、結果を上へ返す。ここが Blender と clr_host の間。
# --------------------------------------------------------------------------------------

def start_ngol(port: int):
    """NGOL を起こす。(成功か, 表示する文言) を返す。例外を外へ出さない。"""
    global _last_message, _last_error
    try:
        if not os.path.isdir(NGOL_ROOT):
            raise clr_host.ClrHostError(
                "NGOL 一式が置かれていません:\n  " + NGOL_ROOT
                + "\n\nscripts/deploy.ps1 で配置してください。"
            )
        clr_host.write_config(NGOL_ROOT, port)
        info = clr_host.start(NGOL_ROOT)

        # ノードから bpy を触る受け口。NGOL を起こしたあとに開ける。
        #    ここが失敗しても NGOL 自体は使える（解析ノードは bpy を要らない）ので、
        #       起動そのものは失敗にしない。
        try:
            mainthread.start(NGOL_ROOT)
        except Exception:
            print("[NgolForBlender] main-thread pump failed to start:\n" + traceback.format_exc())

        _last_error = ""
        # 出すのは設定した番号ではなく、実際に開いた口。使用中なら NGOL が空きへ移る。
        _last_message = "NGOL started in this Blender (pid %d): http://127.0.0.1:%d/" % (
            info["pid"], info["port"],
        )
        print("[NgolForBlender] " + _last_message)
        return True, _last_message
    except Exception as ex:      # 何があっても Blender を巻き込まない
        _last_error = str(ex)
        _last_message = ""
        print("[NgolForBlender] failed to start:\n" + traceback.format_exc())
        return False, _last_error


def stop_ngol():
    """NGOL を止める。CoreCLR はプロセスに残る（降ろせない）。"""
    global _last_message, _last_error
    try:
        # unregister は Blender が起動するたびに走る。
        #    何も起きていないのに「止めた」と出すと、その出力で本当の失敗が埋もれる。
        was_running = clr_host.status()["running"]

        mainthread.stop()
        clr_host.stop()
        _last_error = ""

        if not was_running:
            return True, "NGOL is not running"

        _last_message = "NGOL stopped (the .NET runtime stays loaded; it cannot be unloaded)"
        print("[NgolForBlender] " + _last_message)
        return True, _last_message
    except Exception as ex:
        _last_error = str(ex)
        print("[NgolForBlender] failed to stop:\n" + traceback.format_exc())
        return False, _last_error


def last_error() -> str:
    return _last_error


# --------------------------------------------------------------------------------------
# 「決める」層 - 操作と画面
# --------------------------------------------------------------------------------------

class NGOL_OT_start(bpy.types.Operator):
    bl_idname = "ngol.start"
    bl_label = "NGOL を起動"
    bl_description = "この Blender のプロセスの中で NGOL を起こす"

    def execute(self, context):
        port = _prefs.get(context).port
        ok, message = start_ngol(port)
        if ok:
            self.report({"INFO"}, message)
            return {"FINISHED"}
        # 失敗を黙らせない。1 行目だけだと理由が切れるので全文を出す。
        self.report({"ERROR"}, message)
        return {"CANCELLED"}


class NGOL_OT_stop(bpy.types.Operator):
    bl_idname = "ngol.stop"
    bl_label = "NGOL を停止"
    bl_description = "NGOL を止める（.NET ランタイム自体はプロセスに残る）"

    def execute(self, context):
        ok, message = stop_ngol()
        self.report({"INFO"} if ok else {"ERROR"}, message)
        return {"FINISHED"} if ok else {"CANCELLED"}


class NGOL_OT_open_webui(bpy.types.Operator):
    bl_idname = "ngol.open_webui"
    bl_label = "WebUI を開く"
    bl_description = "ノードグラフの編集画面をブラウザで開く（127.0.0.1）"

    def execute(self, context):
        # 設定した番号ではなく、NGOL が実際に開いた口を聞く。
        # その番号が使用中なら NGOL は空きへ移るので、設定値では繋がらないことがある。
        port = clr_host.server_port()
        if port <= 0:
            self.report({"ERROR"}, "NGOL はまだポートを待ち受けていません")
            return {"CANCELLED"}
        # ループバックのみ。外部のアドレスは開かない。
        bpy.ops.wm.url_open(url="http://127.0.0.1:%d/" % port)
        return {"FINISHED"}


class NGOL_PT_panel(bpy.types.Panel):
    bl_label = "NGOL"
    bl_idname = "NGOL_PT_panel"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "NGOL"

    def draw(self, context):
        layout = self.layout
        info = clr_host.status()

        if info["running"]:
            # 設定値ではなく実際に開いた口。使用中なら NGOL が空きへ移るので食い違いうる。
            layout.label(text="起動中 (pid %d / port %d)" % (info["pid"], info["port"]),
                         icon="CHECKMARK")
        else:
            layout.label(text="停止中", icon="X")

        col = layout.column(align=True)
        col.operator("ngol.start", icon="PLAY")
        col.operator("ngol.stop", icon="PAUSE")
        col.operator("ngol.open_webui", icon="URL")

        if _last_error:
            box = layout.box()
            box.label(text="起動できませんでした", icon="ERROR")
            for line in _last_error.splitlines()[:6]:
                box.label(text=line)


_classes = (
    _prefs.NgolForBlenderPreferences,
    NGOL_OT_start,
    NGOL_OT_stop,
    NGOL_OT_open_webui,
    NGOL_PT_panel,
)


def register():
    for cls in _classes:
        bpy.utils.register_class(cls)

    # 自動起動は既定で切。入れている人だけ起こす。
    #    register の中で重い処理を走らせないよう、1 回だけのタイマーへ逃がす。
    try:
        addon = bpy.context.preferences.addons.get(__package__)
        if addon and addon.preferences.autostart:
            port = addon.preferences.port
            bpy.app.timers.register(lambda: (start_ngol(port), None)[1], first_interval=0.1)
    except Exception:
        # ここで失敗しても register は成功させる。アドオンが入らないほうが困る。
        print("[NgolForBlender] autostart check failed:\n" + traceback.format_exc())


def unregister():
    # 無効化したら待ち受けも畳む。ポートを開けたまま消えない。
    try:
        stop_ngol()
    except Exception:
        print("[NgolForBlender] stop on unregister failed:\n" + traceback.format_exc())

    for cls in reversed(_classes):
        try:
            bpy.utils.unregister_class(cls)
        except Exception:
            pass
