"""MCP サーバーを登録せずに、stdio で直接叩いて確かめる。

登録（.mcp.json）とサーバーの動作は別の話。**先に動くことを確かめてから登録する**。
   登録してから動かないと、原因が「設定」か「サーバー」かの二分に手間がかかる。

    python mcp_probe.py <bundle.js> <ws_url> [toolName] [argsJson]

例:
    python mcp_probe.py ...\\mcp\\dist\\bundle.js ws://127.0.0.1:11165/ws
    python mcp_probe.py ... ws://127.0.0.1:11165/ws get_connection_info {}
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import threading

NODE = os.path.join(os.environ["LOCALAPPDATA"], "Programs", "node", "node.exe")

# Windows のコンソールは CP932 なので、ツール説明の '-' 等でこちらが落ちる。
#    サーバーは正常なのに「MCP が失敗した」と読み違えるので、出力側を先に直しておく。
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass


class McpStdio:
    def __init__(self, bundle: str, ws_url: str, scripts_dir: str = ""):
        env = dict(os.environ)
        env["NGOL_WS_URL"] = ws_url
        env["NGOL_MAX_RESPONSE_CHARS"] = "12000"
        if scripts_dir:
            env["NGOL_SCRIPTS_DIR"] = scripts_dir
        self.proc = subprocess.Popen(
            [NODE, bundle],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            env=env, text=True, encoding="utf-8", bufsize=1,
        )
        self._next_id = 0
        self.stderr_lines = []
        # stderr を読まずに放置するとパイプが詰まる。別スレッドで吸い続ける。
        threading.Thread(target=self._drain_stderr, daemon=True).start()

    def _drain_stderr(self):
        for line in self.proc.stderr:
            self.stderr_lines.append(line.rstrip())

    def _send(self, payload: dict):
        self.proc.stdin.write(json.dumps(payload) + "\n")
        self.proc.stdin.flush()

    def request(self, method: str, params: dict = None, timeout: float = 40.0):
        self._next_id += 1
        wanted = self._next_id
        self._send({"jsonrpc": "2.0", "id": wanted, "method": method,
                    "params": params if params is not None else {}})
        # 通知が混ざるので、id が一致する応答だけを答えとする。
        while True:
            line = self.proc.stdout.readline()
            if not line:
                raise ConnectionError(
                    "MCP サーバーが応答せずに終了しました。stderr:\n  "
                    + "\n  ".join(self.stderr_lines[-20:])
                )
            line = line.strip()
            if not line:
                continue
            try:
                message = json.loads(line)
            except json.JSONDecodeError:
                continue
            if message.get("id") == wanted:
                return message

    def notify(self, method: str, params: dict = None):
        self._send({"jsonrpc": "2.0", "method": method,
                    "params": params if params is not None else {}})

    def close(self):
        try:
            self.proc.stdin.close()
        except Exception:
            pass
        try:
            self.proc.wait(timeout=5)
        except Exception:
            self.proc.kill()


def main():
    bundle = sys.argv[1]
    ws_url = sys.argv[2]
    tool = sys.argv[3] if len(sys.argv) > 3 else None
    args = json.loads(sys.argv[4]) if len(sys.argv) > 4 else {}

    mcp = McpStdio(bundle, ws_url)
    try:
        reply = mcp.request("initialize", {
            "protocolVersion": "2024-11-05",
            "capabilities": {},
            "clientInfo": {"name": "mcp_probe", "version": "0.1.0"},
        })
        info = reply.get("result", {}).get("serverInfo", {})
        print("initialize   : %s v%s (protocol %s)" % (
            info.get("name"), info.get("version"),
            reply.get("result", {}).get("protocolVersion")))
        mcp.notify("notifications/initialized")

        tools = mcp.request("tools/list").get("result", {}).get("tools", [])
        print("tools        : %d" % len(tools))
        if tool is None:
            for t in sorted(tools, key=lambda x: x["name"]):
                head = (t.get("description") or "").splitlines()[0][:88]
                print("  %-34s %s" % (t["name"], head))
        else:
            names = [t["name"] for t in tools]
            if tool not in names:
                print("'%s' というツールは無い。使えるのは: %s" % (tool, ", ".join(sorted(names))))
                raise SystemExit(2)
            result = mcp.request("tools/call", {"name": tool, "arguments": args})
            print("---- %s ----" % tool)
            payload = result.get("result", result.get("error"))
            if isinstance(payload, dict) and "content" in payload:
                for block in payload["content"]:
                    print(block.get("text", json.dumps(block, ensure_ascii=False))[:12000])
            else:
                print(json.dumps(payload, ensure_ascii=False, indent=2)[:12000])
    finally:
        if mcp.stderr_lines:
            print("---- server stderr ----")
            for line in mcp.stderr_lines[-12:]:
                print("  " + line)
        mcp.close()


if __name__ == "__main__":
    main()
