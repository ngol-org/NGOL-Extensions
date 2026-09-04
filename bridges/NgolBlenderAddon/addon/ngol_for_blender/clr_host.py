"""
CLR を起こす層。

このファイルは ``bpy`` を import しない。Blender を知らないままにしておくこと。
   そうしておくと、Blender の外（素の Python）でも同じコードで CLR を起こせるので、
   「NGOL 側の問題か Blender 側の問題か」を切り分けるオラクルになる。

       "<Blender>/5.2/python/bin/python.exe" clr_host.py <ngolRoot>

やっていることは OBS 版 ``NgolBridge.cpp`` と同一で、呼ぶ関数も順序も変えていない。

    1) hostfxr.dll を見つけて読み込む
    2) hostfxr_initialize_for_runtime_config(NgolActivator.runtimeconfig.json)
    3) hostfxr_get_runtime_delegate(handle, hdt_load_assembly_and_get_function_pointer)
    4) load_assembly_and_get_function_pointer(NgolActivator.dll, EntryPoint, "Init")
    5) Init(ngolRoot)

C++ を書かずに済むのは、同梱の ``NgolActivator.dll`` が ``[UnmanagedCallersOnly]`` で
入口を出しているため。デリゲート型名なしに生の関数ポインタとして取得できる。
"""

from __future__ import annotations

import ctypes
import json
import os
import sys
import traceback

# --------------------------------------------------------------------------------------
# 公式ヘッダーから取った定数（推測しない）
#   hostfxr.h            : enum hostfxr_delegate_type
#   coreclr_delegates.h  : #define UNMANAGEDCALLERSONLY_METHOD ((const char_t*)-1)
# --------------------------------------------------------------------------------------
HDT_LOAD_ASSEMBLY_AND_GET_FUNCTION_POINTER = 5
UNMANAGEDCALLERSONLY_METHOD = ctypes.c_void_p(-1)

# hostfxr の戻り値。0 以外にも「成功」がある。
#   Success                        = 0
#   Success_HostAlreadyInitialized = 1
#   Success_DifferentRuntimeProperties = 2
# 2 回目以降の初期化ではこれらが返るので、0 だけを成功とすると再起動できなくなる。
_HOSTFXR_SUCCESS = (0, 1, 2)

ACTIVATOR_ASSEMBLY = "NgolActivator.dll"
ACTIVATOR_CONFIG = "NgolActivator.runtimeconfig.json"
ACTIVATOR_TYPE = "NgolActivator.EntryPoint, NgolActivator"

# Blender の「Reload Scripts」でこのモジュールは作り直される。
#    CoreCLR はプロセスから降ろせないので、掴んだハンドルと関数ポインタは
#    モジュールより長生きする場所（sys）へ隠して持つ。
_STATE_KEY = "_ngol_for_blender_clr_state"


class ClrHostError(RuntimeError):
    """CLR を起こす途中で失敗したときに投げる。Blender 側はこれを捕まえて表示する。"""


def _state() -> dict:
    st = getattr(sys, _STATE_KEY, None)
    if st is None:
        st = {
            "hostfxr_path": None,   # 読み込んだ hostfxr.dll のパス
            "hostfxr": None,        # ctypes.WinDLL
            "context": None,        # hostfxr_handle
            "init_fn": None,        # NgolActivator.EntryPoint.Init
            "shutdown_fn": None,    # NgolActivator.EntryPoint.Shutdown
            "port_fn": None,        # NgolActivator.EntryPoint.GetServerPort
            "ngol_root": None,      # Init に渡した ngolRoot
            "running": False,       # Init 済みで Shutdown していないか
            "hostfxr_errors": [],   # hostfxr が吐いた診断文
        }
        setattr(sys, _STATE_KEY, st)
    return st


# --------------------------------------------------------------------------------------
# 1) hostfxr を見つける
# --------------------------------------------------------------------------------------

def _dotnet_roots() -> list:
    """.NET が入っていそうな場所を、見る順に並べる。

    公式の ``nethost.dll`` (``get_hostfxr_path``) は SDK 側の成果物で、
       ランタイムだけ入れた環境には無い（本機で実測: 8.0.30 を入れても nethost.dll は無し）。
       よって自前で探す。
    """
    roots = []

    env = os.environ.get("DOTNET_ROOT")
    if env:
        roots.append(env)

    program_files = os.environ.get("ProgramFiles")
    if program_files:
        roots.append(os.path.join(program_files, "dotnet"))

    local_app_data = os.environ.get("LOCALAPPDATA")
    if local_app_data:
        # dotnet-install.ps1 の既定の導入先（管理者権限なしで入る）
        roots.append(os.path.join(local_app_data, "Microsoft", "dotnet"))

    # 同じ場所を 2 度見ない。順序は保つ。
    seen = set()
    ordered = []
    for r in roots:
        key = os.path.normcase(os.path.abspath(r))
        if key not in seen:
            seen.add(key)
            ordered.append(r)
    return ordered


def _version_key(name: str):
    """"8.0.30" のような版フォルダ名を数の並びにする。文字列比較だと 8.0.9 > 8.0.30 になる。"""
    parts = []
    for chunk in name.replace("-", ".").split("."):
        parts.append((0, int(chunk)) if chunk.isdigit() else (1, 0))
    return parts


def find_hostfxr() -> str:
    """hostfxr.dll のパスを返す。見つからなければ ClrHostError。"""
    looked = []
    for root in _dotnet_roots():
        fxr_dir = os.path.join(root, "host", "fxr")
        looked.append(fxr_dir)
        if not os.path.isdir(fxr_dir):
            continue
        versions = [d for d in os.listdir(fxr_dir) if os.path.isdir(os.path.join(fxr_dir, d))]
        if not versions:
            continue
        for version in sorted(versions, key=_version_key, reverse=True):
            candidate = os.path.join(fxr_dir, version, "hostfxr.dll")
            if os.path.isfile(candidate):
                return candidate

    raise ClrHostError(
        ".NET 8 ランタイムが見つかりません。hostfxr.dll を次の場所で探しました:\n  "
        + "\n  ".join(looked)
        + "\n\n.NET 8 ランタイム (win-x64) を入れてください。管理者権限なしで入れるなら:\n"
        '  dotnet-install.ps1 -Channel 8.0 -Runtime dotnet -InstallDir "%LOCALAPPDATA%\\Microsoft\\dotnet"'
    )


# --------------------------------------------------------------------------------------
# 2)3)4) hostfxr を叩いて Init/Shutdown の関数ポインタを取る
# --------------------------------------------------------------------------------------

_ERROR_WRITER_TYPE = ctypes.WINFUNCTYPE(None, ctypes.c_wchar_p)


def _bind_hostfxr(path: str):
    lib = ctypes.WinDLL(path)

    lib.hostfxr_initialize_for_runtime_config.restype = ctypes.c_int32
    lib.hostfxr_initialize_for_runtime_config.argtypes = [
        ctypes.c_wchar_p,   # runtime_config_path        (char_t = wchar_t on Windows)
        ctypes.c_void_p,    # hostfxr_initialize_parameters* (NULL でよい)
        ctypes.POINTER(ctypes.c_void_p),  # out hostfxr_handle
    ]

    lib.hostfxr_get_runtime_delegate.restype = ctypes.c_int32
    lib.hostfxr_get_runtime_delegate.argtypes = [
        ctypes.c_void_p,    # host_context_handle
        ctypes.c_int32,     # hostfxr_delegate_type
        ctypes.POINTER(ctypes.c_void_p),  # out delegate
    ]

    lib.hostfxr_close.restype = ctypes.c_int32
    lib.hostfxr_close.argtypes = [ctypes.c_void_p]

    lib.hostfxr_set_error_writer.restype = ctypes.c_void_p
    lib.hostfxr_set_error_writer.argtypes = [ctypes.c_void_p]
    return lib


_LOAD_ASSEMBLY_FN = ctypes.WINFUNCTYPE(
    ctypes.c_int32,
    ctypes.c_wchar_p,   # assembly_path
    ctypes.c_wchar_p,   # type_name
    ctypes.c_wchar_p,   # method_name
    ctypes.c_void_p,    # delegate_type_name / UNMANAGEDCALLERSONLY_METHOD
    ctypes.c_void_p,    # reserved
    ctypes.POINTER(ctypes.c_void_p),  # out delegate
)

# NgolActivator.EntryPoint の 3 つ。UnmanagedCallersOnly なので素の C 関数として呼べる。
_INIT_FN = ctypes.WINFUNCTYPE(ctypes.c_int32, ctypes.c_wchar_p)
_SHUTDOWN_FN = ctypes.WINFUNCTYPE(None)
_PORT_FN = ctypes.WINFUNCTYPE(ctypes.c_int32)


def _ensure_delegates(ngol_root: str) -> dict:
    """hostfxr を初期化し、Init/Shutdown の関数ポインタを取る（1 プロセスに 1 回だけ）。"""
    st = _state()
    if st["init_fn"] is not None:
        return st

    config = os.path.join(ngol_root, ACTIVATOR_CONFIG)
    assembly = os.path.join(ngol_root, ACTIVATOR_ASSEMBLY)
    for required in (config, assembly):
        if not os.path.isfile(required):
            raise ClrHostError(
                "NGOL 一式が見つかりません（このファイルが必要です）:\n  " + required
                + "\n\nngolRoot: " + ngol_root
            )

    path = find_hostfxr()
    lib = _bind_hostfxr(path)
    st["hostfxr_path"] = path
    st["hostfxr"] = lib

    # hostfxr は失敗の理由を戻り値ではなく診断文で出すことがある。取りこぼさないよう受ける。
    errors = []

    def _on_error(message):
        errors.append(message)

    writer = _ERROR_WRITER_TYPE(_on_error)
    previous = lib.hostfxr_set_error_writer(ctypes.cast(writer, ctypes.c_void_p))
    try:
        context = ctypes.c_void_p()
        rc = lib.hostfxr_initialize_for_runtime_config(config, None, ctypes.byref(context))
        if rc not in _HOSTFXR_SUCCESS or not context.value:
            raise ClrHostError(
                "hostfxr_initialize_for_runtime_config が失敗しました (rc=0x%08X)\n"
                "  config: %s\n  hostfxr: %s%s" % (rc & 0xFFFFFFFF, config, path, _joined(errors))
            )
        st["context"] = context

        delegate = ctypes.c_void_p()
        rc = lib.hostfxr_get_runtime_delegate(
            context, HDT_LOAD_ASSEMBLY_AND_GET_FUNCTION_POINTER, ctypes.byref(delegate)
        )
        if rc not in _HOSTFXR_SUCCESS or not delegate.value:
            raise ClrHostError(
                "hostfxr_get_runtime_delegate が失敗しました (rc=0x%08X)%s"
                % (rc & 0xFFFFFFFF, _joined(errors))
            )
        load_assembly = _LOAD_ASSEMBLY_FN(delegate.value)

        def _entry(method_name, prototype):
            fn = ctypes.c_void_p()
            r = load_assembly(
                assembly, ACTIVATOR_TYPE, method_name,
                UNMANAGEDCALLERSONLY_METHOD, None, ctypes.byref(fn),
            )
            if r != 0 or not fn.value:
                raise ClrHostError(
                    "load_assembly_and_get_function_pointer('%s') が失敗しました "
                    "(rc=0x%08X)\n  assembly: %s%s"
                    % (method_name, r & 0xFFFFFFFF, assembly, _joined(errors))
                )
            return prototype(fn.value)

        st["init_fn"] = _entry("Init", _INIT_FN)
        st["shutdown_fn"] = _entry("Shutdown", _SHUTDOWN_FN)
        st["port_fn"] = _entry("GetServerPort", _PORT_FN)
    finally:
        # 入れっぱなしにしない。以降 hostfxr が別スレッドから呼んでくる形を作らない。
        lib.hostfxr_set_error_writer(previous)
        st["hostfxr_errors"] = list(errors)

    return st


def _joined(errors) -> str:
    return ("\n  hostfxr: " + "\n  hostfxr: ".join(errors)) if errors else ""


# --------------------------------------------------------------------------------------
# 外から使う口
# --------------------------------------------------------------------------------------

def write_config(ngol_root: str, port: int) -> str:
    """ngol-config.json を書く。起動後に書いても効かない（NGOL は起動時に読む）。"""
    path = os.path.join(ngol_root, "ngol-config.json")
    existing = {}
    if os.path.isfile(path):
        try:
            with open(path, "r", encoding="utf-8") as f:
                existing = json.load(f)
        except Exception:
            existing = {}
    existing["port"] = int(port)
    existing.setdefault("forceDirectMode", False)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(existing, f, indent=2)
    return path


def start(ngol_root: str) -> dict:
    """NGOL をこのプロセスの中で起こす。成功したら状態を返す。失敗は ClrHostError。"""
    ngol_root = os.path.abspath(ngol_root)
    st = _ensure_delegates(ngol_root)

    if st["running"]:
        return status()

    rc = st["init_fn"](ngol_root)
    if rc != 0:
        raise ClrHostError(
            "NgolActivator.EntryPoint.Init が %d を返しました。\n"
            "理由は次のファイルに残っていることがあります:\n"
            "  %s\n  %s"
            % (rc, os.path.join(ngol_root, "activator-error.log"),
               os.path.join(ngol_root, "host.log"))
        )

    st["running"] = True
    st["ngol_root"] = ngol_root
    return status()


def stop() -> None:
    """NGOL を止める。

    CoreCLR はプロセスから降ろせない。ここで止まるのは NgolRuntime だけで、
       .NET 自体は blender.exe の中に残る。=> hostfxr_close は呼ばない
       （呼ぶと次に Init できなくなる）。
    """
    st = _state()
    if st["shutdown_fn"] is not None and st["running"]:
        st["shutdown_fn"]()
    st["running"] = False


def is_running() -> bool:
    return bool(_state()["running"])


def server_port() -> int:
    """実際に待ち受けているポート。待ち受けていなければ 0。

    設定した番号は使わない。その番号が使用中なら NGOL は空きへ移るため、
    設定と実際の待ち受け先は食い違うことがある。控えずにそのつど聞く。
    """
    st = _state()
    if st["port_fn"] is None or not st["running"]:
        return 0
    try:
        return int(st["port_fn"]())
    except Exception:
        return 0


def status() -> dict:
    st = _state()
    return {
        "running": st["running"],
        "hostfxr_path": st["hostfxr_path"],
        "ngol_root": st["ngol_root"],
        "clr_loaded": st["init_fn"] is not None,
        "pid": os.getpid(),
        "port": server_port(),
        "hostfxr_errors": list(st["hostfxr_errors"]),
    }


# --------------------------------------------------------------------------------------
# オラクル用。Blender の外で同じ経路を通す。
#   "<Blender>/5.2/python/bin/python.exe" clr_host.py <ngolRoot>
# --------------------------------------------------------------------------------------
if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("usage: python clr_host.py <ngolRoot> [seconds]")
        raise SystemExit(2)

    root = sys.argv[1]
    seconds = float(sys.argv[2]) if len(sys.argv) > 2 else 20.0
    try:
        print("[clr_host] hostfxr : %s" % find_hostfxr())
        print("[clr_host] ngolRoot: %s" % os.path.abspath(root))
        print("[clr_host] pid     : %d" % os.getpid())
        info = start(root)
        print("[clr_host] started : %r" % (info,))
        import time
        time.sleep(seconds)
        stop()
        print("[clr_host] stopped")
    except Exception:
        traceback.print_exc()
        raise SystemExit(1)
