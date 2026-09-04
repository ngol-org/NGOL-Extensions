"""メインスレッドで Python を走らせるだけのポンプ。

**このアドオンの仕事は「NGOL を載せること」まで。**
    Blender で何をするかは NGOL のノードとグラフが決める。
    => ここには **Blender の機能を 1 つも置かない。**
      シーンの読み方も、オブジェクトの作り方も、画面の撮り方も知らない。

置いてあるのは、次の 1 つだけ:

    「渡された Python を **Blender のメインスレッドで** 走らせて、結果を返す」

---

## なぜこれだけは Blender 側に要るのか

**`bpy` はメインスレッドからしか触れない。** NGOL は自前スレッドで回るので、
ノードから直接触ると Blender ごと落ちる。
メインループに乗る手段（`bpy.app.timers`）は **Blender の中にしか無い**。

=> **「メインスレッドに乗せる」ことだけがアドオンの責務**で、
   「乗せて何をするか」はノード側の責務。ここが境界。

```
NGOL のノード（別スレッド）
    |  Python の文面を置く      <ngolRoot>/blender_bridge/req/<id>.json
    v
ここ（bpy.app.timers ＝ メインスレッド）
    |  exec して結果を置く      <ngolRoot>/blender_bridge/res/<id>.json
    v
NGOL のノードが拾う
```

**なぜファイルか。** ソケットなら往復は速いが、待ち受け口が 1 つ増える。
   欲しいのは速さではなく「**外へ口を開けないこと**」と「**中身が目で見えること**」。
   詰まったらフォルダを覗けば、何を頼んで何が返ったか分かる。

---

## 信頼境界について

ここは**渡された Python をそのまま実行する**。ガードは無い。

ただし、これで**新しく危険になるものは無い**。
`ngolRoot` に書ける者は、もともと `Nodes/CustomNodes/cs/` に `.cs` を置けば
NGOL がプロセス内でコンパイルして実行する。**信頼境界は元から同じ。**

=> 守るべき線は「`ngolRoot` に書けるのは誰か」であって、ここに関門を作ることではない。
   危険な操作を断るのは**ノード側**（`blender.exec` は `allow` が無ければ通さない）。

---

## 実装上、選択肢が 1 つしかない部分

公式 Blender MCP（`projects.blender.org/lab/blender_mcp`）も、Blender 側は
`bpy.app.timers` でポーリングしている。**これは作法であって発明ではない**--
`bpy` を別スレッドから触れない以上、他に手が無い。
公式実装を読んで、次の 2 点だけ素直に倣った。

| | |
|---|---|
| ポーリング間隔を **active / idle の 2 段**にする | 固定の細かい間隔で回し続けると、暇なときに Blender の邪魔をする |
| タイマー内で例外を必ず握る | 公式が「無いとタイマーが外れてアドオンが壊れる」と書いている。実際そうなる |
"""

from __future__ import annotations

import io
import json
import os
import time
import traceback

import bpy

# 要求が続いている間は細かく、暇なら粗く。
_ACTIVE_INTERVAL = 0.02
_IDLE_INTERVAL = 0.20
_IDLE_AFTER_SECONDS = 1.0

# 1 回の呼び出しがこれを超えたら、次の pump へ譲る。
# 1 回のタイマーで溜まった要求を全部さばくと、その間 Blender が固まって見える。
_BUDGET_PER_PUMP = 0.10

_state = {
    "root": "",
    "running": False,
    "served": 0,
    "failed": 0,
    "last_error": "",
    "last_work_at": 0.0,
}


def bridge_root(ngol_root: str) -> str:
    return os.path.join(ngol_root, "blender_bridge")


def _dir(name: str) -> str:
    return os.path.join(_state["root"], name)


# ======================================================================================
# 唯一の仕事: 渡された Python をメインスレッドで走らせる
# ======================================================================================

def _ensure_python_path() -> str:
    """NGOL 側の Python 置き場を import できるようにする。

    ここは **NGOL の territory であってアドオンの機能ではない**。
       `<ngolRoot>/Nodes/CustomNodes/py/` は `.cs` と同じ扱いで、
       **Blender を再起動せずに書き換えられる**。
       ノードが送ってくる Python は、ここに置いた土台を `import` して使う。

    アドオン側は「置き場を通す」だけで、中身が何をするかは知らない。
    """
    import sys

    ngol_root = os.path.dirname(_state["root"])          # <ngolRoot>/blender_bridge の親
    py_dir = os.path.join(ngol_root, "Nodes", "CustomNodes", "py")
    if os.path.isdir(py_dir) and py_dir not in sys.path:
        sys.path.insert(0, py_dir)
    return py_dir


def reload_python_modules(names) -> list:
    """土台の `.py` を書き換えたときに読み直す。

    Python は一度 import したものを覚えている。書き換えても
       **黙って古い実装が動き続ける**（「直したのに効かない」の典型）。
    """
    import importlib
    import sys

    _ensure_python_path()
    done = []
    for name in names:
        module = sys.modules.get(name)
        try:
            if module is None:
                importlib.import_module(name)
                done.append(name + " (imported)")
            else:
                importlib.reload(module)
                done.append(name + " (reloaded)")
        except Exception as ex:
            done.append("%s FAILED: %s" % (name, ex))
    return done


def _run(source: str, arguments: dict) -> dict:
    """`source` を exec し、`result` に入った値を返す。

    使えるようにしておくもの（毎回 import させない）:
        bpy / bmesh / math / mathutils / Vector / Matrix / Euler / Quaternion / Color
        json / os / args（呼び出し側が渡した dict）/ out（ここへ書いても返る）

    何を作るか・何を読むかは **一切決めない**。それはノード側の仕事。
    """
    import bmesh
    import math
    import mathutils

    _ensure_python_path()

    namespace = {
        "bpy": bpy, "bmesh": bmesh, "math": math, "mathutils": mathutils,
        "Vector": mathutils.Vector, "Matrix": mathutils.Matrix,
        "Euler": mathutils.Euler, "Quaternion": mathutils.Quaternion,
        "Color": mathutils.Color,
        "json": json, "os": os,
        "args": arguments or {},
        "out": {},
        # 土台の .py を書き換えたら、ノード側からこれを呼んで読み直す。
        #    Blender の再起動は要らない（.cs のホットリロードと同じ感覚で回せる）。
        "reload_modules": reload_python_modules,
        "__name__": "__ngol_main_thread__",
    }

    captured = io.StringIO()
    import contextlib
    try:
        with contextlib.redirect_stdout(captured), contextlib.redirect_stderr(captured):
            exec(compile(source, "<ngol.blender>", "exec"), namespace)  # noqa: S102
    except Exception:
        return {"ok": False,
                "error": traceback.format_exc(limit=10),
                "stdout": captured.getvalue()}

    # `result = ...` でも `out[...] = ...` でも受け取れるようにしておく。
    value = namespace.get("result", None)
    if value is None and namespace.get("out"):
        value = namespace["out"]

    try:
        json.dumps(value)
    except (TypeError, ValueError):
        # JSON にできないものを黙って捨てない。何だったかは残す。
        value = {"repr": repr(value), "type": type(value).__name__}

    return {"ok": True, "result": value, "stdout": captured.getvalue()}


# ======================================================================================
# 境界層 - 拾って、走らせて、返すだけ
# ======================================================================================

def _write_atomic(path: str, payload: dict):
    """相手が書き途中のファイルを読まないよう、別名で書いてから置き換える。"""
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(payload, f, ensure_ascii=False)
    os.replace(tmp, path)


def _serve_one(name: str) -> None:
    req_path = os.path.join(_dir("req"), name)
    res_path = os.path.join(_dir("res"), name)
    try:
        with open(req_path, "r", encoding="utf-8") as f:
            request = json.load(f)
    except Exception as ex:
        _write_atomic(res_path, {"ok": False, "error": "要求が読めません: %s" % ex})
        _safe_remove(req_path)
        _state["failed"] += 1
        return

    started = time.perf_counter()
    source = request.get("code") or ""
    if not source.strip():
        reply = {"ok": False, "error": "code が空です"}
    else:
        reply = _run(source, request.get("args") or {})

    reply["ms"] = round((time.perf_counter() - started) * 1000.0, 3)
    if request.get("label"):
        reply["label"] = request["label"]

    _write_atomic(res_path, reply)
    _safe_remove(req_path)
    _state["served"] += 1
    if not reply.get("ok"):
        _state["failed"] += 1
        _state["last_error"] = str(reply.get("error", ""))[:2000]


def _pump():
    if not _state["running"]:
        return None          # None を返すとタイマーは登録解除される

    did_work = False
    try:
        deadline = time.perf_counter() + _BUDGET_PER_PUMP
        for name in sorted(n for n in os.listdir(_dir("req")) if n.endswith(".json")):
            _serve_one(name)
            did_work = True
            if time.perf_counter() > deadline:
                break        # 残りは次の pump へ。Blender を止めたままにしない
    except Exception:
        # ここで例外を外へ出すとタイマーが外れ、受け口が黙って死ぬ。
        _state["last_error"] = traceback.format_exc(limit=6)
        print("[NgolForBlender/mainthread] pump error:\n" + _state["last_error"])
        did_work = True

    now = time.monotonic()
    if did_work:
        _state["last_work_at"] = now
    if now - _state["last_work_at"] < _IDLE_AFTER_SECONDS:
        return _ACTIVE_INTERVAL
    return _IDLE_INTERVAL


def _safe_remove(path: str):
    try:
        os.remove(path)
    except OSError:
        pass


def start(ngol_root: str):
    """受け口を開ける。NGOL を起こしたあとに呼ぶこと。"""
    root = bridge_root(ngol_root)
    for sub in ("req", "res", "out"):
        os.makedirs(os.path.join(root, sub), exist_ok=True)

    _state["root"] = root
    # 前回の残骸を持ち越さない。古い答えを新しい要求の答えだと読む事故を防ぐ。
    for sub in ("req", "res"):
        directory = _dir(sub)
        for name in os.listdir(directory):
            _safe_remove(os.path.join(directory, name))

    _state.update({"running": True, "served": 0, "failed": 0,
                   "last_error": "", "last_work_at": time.monotonic()})

    if not bpy.app.timers.is_registered(_pump):
        bpy.app.timers.register(_pump, first_interval=_ACTIVE_INTERVAL, persistent=True)
    print("[NgolForBlender] main-thread pump listening at %s" % root)

    # `-b`（UI なし実行）ではタイマーが回らない。**登録は成功するのに発火しない。**
    #    黙っていると「ノードは繋がるのに Blender 側が一切答えない」という
    #    原因の分かりにくい状態になるので、起動時に言っておく。
    #    実測（2026-08-22）: 0.05 秒間隔で 3 秒待って発火 0 回。
    if bpy.app.background:
        print("[NgolForBlender] background mode: bpy.app.timers does not fire here.\n"
              "[NgolForBlender]   Nothing will answer until this thread is lent to the bridge:\n"
              "[NgolForBlender]     import sys; "
              "sys.modules['ngol_for_blender'].mainthread.pump_forever(600)")


def stop():
    _state["running"] = False
    try:
        if bpy.app.timers.is_registered(_pump):
            bpy.app.timers.unregister(_pump)
    except Exception:
        pass


def status() -> dict:
    return dict(_state)


def pump_once():
    """`-b`（UI なし実行）ではタイマーが回らない。手で 1 回だけ回す口。

    実測（2026-08-22）: `bpy.app.timers` を 0.05 秒間隔で登録して 3 秒待っても
       **発火 0 回**。`-b` にはイベントループが無いので、回す主体が居ない。
    """
    return _pump()


def pump_forever(seconds: float = 0.0, interval: float = 0.02) -> dict:
    """`-b`（UI なし実行）で、ブリッジを生かしたまま待つ。

    `-b` では `bpy.app.timers` が回らないので、**呼び出し側がメインスレッドを
    貸してやらないと、ノードからの要求は永遠に捌かれない**。
    自分でループを書かずに済むよう、ここに置いておく。

        import bpy, sys
        bpy.ops.preferences.addon_enable(module="ngol_for_blender")
        mod = sys.modules["ngol_for_blender"]
        mod.start_ngol(11167)
        sys.modules["ngol_for_blender"].mainthread.pump_forever(600)

    この関数は **戻ってこない間、他に何もできない**（メインスレッドを占有する）。
      `-b` のスクリプトは最後に置くこと。

    :param seconds: 回す秒数。0 以下なら止められるまで回り続ける
    :param interval: 1 周あたりの待ち。小さくすると CPU を食う
    :return: 何回回して何件捌いたか
    """
    started = time.monotonic()
    served_before = _state["served"]
    deadline = (started + seconds) if seconds > 0 else None
    rounds = 0

    print("[NgolForBlender] pumping the bridge on this thread%s"
          % ("" if deadline is None else " for %.0fs" % seconds))
    try:
        while _state["running"]:
            _pump()
            rounds += 1
            if deadline is not None and time.monotonic() >= deadline:
                break
            time.sleep(interval)
    except KeyboardInterrupt:
        print("[NgolForBlender] pumping interrupted")

    return {
        "rounds": rounds,
        "served": _state["served"] - served_before,
        "failed": _state["failed"],
        "seconds": round(time.monotonic() - started, 3),
    }
