"""NGOL へ WebSocket で 1 件頼んで答えを受け取る、最小のクライアント。

外部へは一切出さない。宛先は 127.0.0.1 に固定してある。

NGOL の口は WebSocket (`/ws`) だけで、素の HTTP API は無い
（`NodeGraphModLab.Core/Server/GraphServer.cs` を読んで確認）。
メッセージは `{"type": "..."}` を持つ JSON オブジェクト（`MessageParser.cs`）。

これは MCP サーバーを通らない生の口なので、MCP 側の切り詰め等は再現しない。
   検証用と割り切ること。

使い方:
    python ngol_ws.py <port> welcome
    python ngol_ws.py <port> nodes
    python ngol_ws.py <port> run <nodeTypeId> [inputsJson]
"""

from __future__ import annotations

import base64
import json
import os
import socket
import struct
import sys

HOST = "127.0.0.1"          # 固定。外部のアドレスは受け付けない
DEFAULT_TIMEOUT = 30.0


class NgolWs:
    def __init__(self, port: int, timeout: float = DEFAULT_TIMEOUT):
        self.port = int(port)
        self.sock = socket.create_connection((HOST, self.port), timeout=timeout)
        self.sock.settimeout(timeout)
        self._buffer = b""
        self._handshake()

    # -- WebSocket の最低限 ---------------------------------------------------------
    def _handshake(self):
        key = base64.b64encode(os.urandom(16)).decode()
        request = (
            "GET /ws HTTP/1.1\r\n"
            "Host: %s:%d\r\n"
            "Upgrade: websocket\r\n"
            "Connection: Upgrade\r\n"
            "Sec-WebSocket-Key: %s\r\n"
            "Sec-WebSocket-Version: 13\r\n"
            "\r\n" % (HOST, self.port, key)
        )
        self.sock.sendall(request.encode())

        while b"\r\n\r\n" not in self._buffer:
            chunk = self.sock.recv(4096)
            if not chunk:
                raise ConnectionError("接続が閉じられました（ハンドシェイク中）")
            self._buffer += chunk
        head, self._buffer = self._buffer.split(b"\r\n\r\n", 1)
        status = head.split(b"\r\n", 1)[0].decode(errors="replace")
        if "101" not in status:
            raise ConnectionError("WebSocket へ切り替わりませんでした: " + status)

    def _recv_exactly(self, n: int) -> bytes:
        while len(self._buffer) < n:
            chunk = self.sock.recv(65536)
            if not chunk:
                raise ConnectionError("接続が閉じられました（受信中）")
            self._buffer += chunk
        out, self._buffer = self._buffer[:n], self._buffer[n:]
        return out

    def _recv_frame(self):
        b0, b1 = self._recv_exactly(2)
        fin = bool(b0 & 0x80)
        opcode = b0 & 0x0F
        masked = bool(b1 & 0x80)
        length = b1 & 0x7F
        if length == 126:
            length = struct.unpack(">H", self._recv_exactly(2))[0]
        elif length == 127:
            length = struct.unpack(">Q", self._recv_exactly(8))[0]
        mask = self._recv_exactly(4) if masked else None
        payload = self._recv_exactly(length)
        if mask:
            payload = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
        return fin, opcode, payload

    def _send_frame(self, opcode: int, payload: bytes):
        # クライアントからのフレームは必ずマスクする（RFC 6455）。
        mask = os.urandom(4)
        masked = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
        header = bytes([0x80 | opcode])
        n = len(payload)
        if n < 126:
            header += bytes([0x80 | n])
        elif n < 65536:
            header += bytes([0x80 | 126]) + struct.pack(">H", n)
        else:
            header += bytes([0x80 | 127]) + struct.pack(">Q", n)
        self.sock.sendall(header + mask + masked)

    # -- 使う口 ---------------------------------------------------------------------
    def send(self, message: dict):
        self._send_frame(0x1, json.dumps(message).encode("utf-8"))

    def recv(self) -> dict:
        """テキストメッセージを 1 件受け取る。分割フレームと ping はここで畳む。"""
        parts = []
        opcode_of_message = None
        while True:
            fin, opcode, payload = self._recv_frame()
            if opcode == 0x9:                      # ping -> pong を返す
                self._send_frame(0xA, payload)
                continue
            if opcode == 0xA:                      # pong
                continue
            if opcode == 0x8:                      # close
                raise ConnectionError("サーバーが接続を閉じました")
            if opcode in (0x1, 0x2):
                opcode_of_message = opcode
                parts = [payload]
            elif opcode == 0x0:
                parts.append(payload)
            if fin and opcode_of_message is not None:
                raw = b"".join(parts).decode("utf-8", errors="replace")
                return json.loads(raw)

    def request(self, message: dict, want: str = None, tries: int = 40) -> dict:
        """1 件送って、欲しい type の返事が来るまで読む。

        NGOL は頼んでいない通知（ログ・スナップショット等）も流してくるので、
           最初に来たものを答えだと決めない。
        """
        self.send(message)
        for _ in range(tries):
            reply = self.recv()
            if want is None or reply.get("type") == want:
                return reply
            if reply.get("type") == "error":
                return reply
        raise TimeoutError("'%s' が %d 件読んでも来ませんでした" % (want, tries))

    def close(self):
        try:
            self._send_frame(0x8, b"")
        except Exception:
            pass
        try:
            self.sock.close()
        except Exception:
            pass


def connect(port: int) -> "tuple[NgolWs, dict]":
    """繋いで welcome を受け取る。

    welcome の processId が「本当にその相手か」の判定になる。
       自分が指定した URL ではなくこの値を見ること。
    """
    ws = NgolWs(port)
    welcome = ws.recv()
    return ws, welcome


if __name__ == "__main__":
    if len(sys.argv) < 3:
        print(__doc__)
        raise SystemExit(2)

    port = int(sys.argv[1])
    command = sys.argv[2]
    ws, welcome = connect(port)
    try:
        if command == "welcome":
            print(json.dumps(welcome, ensure_ascii=False, indent=2))

        elif command == "nodes":
            # 応答の type は要求名と違う（ServerDtos.cs で確認: node_list_response）
            reply = ws.request({"type": "get_node_list"}, want="node_list_response")
            nodes = reply.get("nodes", [])
            print("welcome.processId = %s" % welcome.get("processId"))
            print("node count        = %d" % len(nodes))
            for n in sorted(nodes, key=lambda x: x.get("id", "")):
                print("  %-42s %s" % (n.get("id"), n.get("displayName", "")))

        elif command == "run":
            node_type = sys.argv[3]
            inputs = json.loads(sys.argv[4]) if len(sys.argv) > 4 else {}
            reply = ws.request(
                {"type": "execute_node", "nodeTypeId": node_type, "inputs": inputs},
                want="execute_node_response",
            )
            print(json.dumps(reply, ensure_ascii=False, indent=2)[:20000])

        else:
            print("unknown command: " + command)
            raise SystemExit(2)
    finally:
        ws.close()
