"""NGOL の MCP サーバーを Claude Code のユーザースコープ設定へ登録する。

登録先を「プロジェクト直下の .mcp.json」にしない。
   作業対象のリポジトリへファイルを増やさないため、
   ユーザースコープ（~/.claude.json の最上位 mcpServers）へ入れる。

~/.claude.json は Claude Code 本体の設定でもある。**必ず控えを取ってから書く。**

    python register_mcp.py            登録する
    python register_mcp.py --show     いまの登録を見るだけ
    python register_mcp.py --remove   外す
    python register_mcp.py --port 11157   移った先へ登録する
"""

from __future__ import annotations

import io
import json
import os
import re
import shutil
import sys

# Windows のコンソールは CP932。'-' 等でこちらが落ち、
#    「登録に失敗した」と読み違える。出力側を先に直す。
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

SERVER_NAME = "ngol-blender"
DEFAULT_PORT = 11156

CONFIG = os.path.join(os.path.expanduser("~"), ".claude.json")
NODE = os.path.join(os.environ["LOCALAPPDATA"], "Programs", "node", "node.exe")
def _find_ngol_root(version: str = "") -> str:
    """導入済みの ngol フォルダを探す。

    Extension 形式と旧来のアドオン形式で置き場所が違うので、両方見る。
    直書きすると、片方に入れている人には黙って外れる。
    Extension のリポジトリ名は利用者が決められるので、そこも走査して見つける。
    版も決め打ちにしない。Blender は版ごとに別のフォルダを持つので、
      固定するとその版に入れていない人には外れる。=> 在る版を新しい順に見る。
    """
    root = os.path.join(os.environ["APPDATA"], "Blender Foundation", "Blender")
    if version:
        versions = [version]
    else:
        try:
            versions = sorted(
                (d for d in os.listdir(root) if re.fullmatch(r"\d+\.\d+", d)),
                key=lambda s: tuple(int(x) for x in s.split(".")),
                reverse=True,
            )
        except OSError:
            versions = []

    candidates = []
    for ver in versions:
        base = os.path.join(root, ver)
        ext = os.path.join(base, "extensions")
        if os.path.isdir(ext):
            for repo in sorted(os.listdir(ext)):
                candidates.append(os.path.join(ext, repo, "ngol_for_blender", "ngol"))
        candidates.append(os.path.join(base, "scripts", "addons", "ngol_for_blender", "ngol"))

    for path in candidates:
        if os.path.isdir(path):
            return path
    # 見つからなくても、探した場所が分かるように返す
    return candidates[-1] if candidates else os.path.join(root, "(版のフォルダが無い)")


NGOL_ROOT = _find_ngol_root()


def build_entry(port: int = DEFAULT_PORT) -> dict:
    bundle = os.path.join(NGOL_ROOT, "mcp", "dist", "bundle.js")
    return {
        "type": "stdio",
        # node は PATH に入れていない（環境を汚さないため）。絶対パスで指す。
        "command": NODE,
        "args": [bundle],
        "env": {
            # ループバック固定。外部のアドレスは書かない。
            "NGOL_WS_URL": "ws://127.0.0.1:%d/ws" % port,
            "NGOL_SCRIPTS_DIR": os.path.join(NGOL_ROOT, "Nodes", "CustomNodes", "cs"),
            "NGOL_DOCS_DIR": os.path.join(NGOL_ROOT, "mcp", "docs"),
            "NGOL_MAX_RESPONSE_CHARS": "12000",
            "NGOL_MAX_GRAPH_RESPONSE_CHARS": "32000",
        },
    }


def _port_from_args() -> int:
    """--port <番号> があればそれを使う。無ければ既定。

    設定した番号が使用中なら NGOL は空きへ移る。移った先へ登録し直せるように、
    書き換えずに指定できる口を用意しておく。
    """
    args = sys.argv[1:]
    if "--port" in args:
        i = args.index("--port")
        if i + 1 < len(args):
            return int(args[i + 1])
    return DEFAULT_PORT


def load() -> dict:
    with io.open(CONFIG, "r", encoding="utf-8") as f:
        return json.load(f)


def save(data: dict):
    backup = CONFIG + ".bak-ngol"
    shutil.copy2(CONFIG, backup)
    print("backup       : %s" % backup)
    # 同じ場所へ書き戻す前に一度検証する。壊れた JSON を残さない。
    text = json.dumps(data, ensure_ascii=False, indent=2)
    json.loads(text)
    with io.open(CONFIG, "w", encoding="utf-8") as f:
        f.write(text)


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else ""
    data = load()
    servers = data.get("mcpServers") or {}

    if mode == "--show":
        print(json.dumps(servers, ensure_ascii=False, indent=2))
        return

    if mode == "--remove":
        if SERVER_NAME in servers:
            del servers[SERVER_NAME]
            data["mcpServers"] = servers
            save(data)
            print("removed      : %s" % SERVER_NAME)
        else:
            print("not registered: %s" % SERVER_NAME)
        return

    missing = [p for p in (NODE, os.path.join(NGOL_ROOT, "mcp", "dist", "bundle.js")) if not os.path.isfile(p)]
    if missing:
        print("必要なファイルがありません:")
        for p in missing:
            print("   " + p)
        raise SystemExit(2)

    entry = build_entry(_port_from_args())
    servers[SERVER_NAME] = entry
    data["mcpServers"] = servers
    save(data)

    print("registered   : %s (user scope, ~/.claude.json)" % SERVER_NAME)
    print("command      : %s" % entry["command"])
    print("bundle       : %s" % entry["args"][0])
    print("ws url       : %s" % entry["env"]["NGOL_WS_URL"])
    print()
    print("反映には Claude Code の再起動が要る。")
    print("接続先の NGOL（Blender の中）が動いていないと、ツールは出ても呼べば失敗する。")


if __name__ == "__main__":
    main()
