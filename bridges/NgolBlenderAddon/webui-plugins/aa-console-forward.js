// ブラウザの console を NGOL 側へ転送する（Debug Bridge が OFF でも効く）
//
// ログが読めないときは環境設定を直すより、ログ機構そのものへ自分を挿す。
//
// なぜ要るか:
//   コンソールは人しか読めない。そこにしか出ないと、検証のたびに
//     「コンソールを見てもらえますか」と人に投げることになる。
//   これを置くと、MCP の get_browser_debug_log でエージェント側が読める。
//     => WebUI プラグインの検証が人手を介さず閉じる。
//
// ファイル名が "aa-" で始まっているのは意図的。
//   plugins/ はファイル名順に読み込まれるので、**最初に読ませないと
//   後から読まれるプラグインの読み込み時エラーを拾えない。**
//
// 本体（WebUI/src/）は変更していない。Debug Bridge の設定にも触っていない。

const NGOL = window.NGOL
const ws = NGOL.ws

const MAX_PENDING = 200
let pending = []
let connected = false
let sending = false          // 送信中に console を呼ぶと無限再帰する。門を閉じる

function post(level, message) {
  const entry = { type: 'debug_log_entry', kind: 'console', level, message }
  if (!connected) {
    // 読み込み直後は WS が未接続。ここで捨てると
    //   **一番知りたい「読み込み時のエラー」だけが失われる。**
    pending.push(entry)
    if (pending.length > MAX_PENDING) pending.shift()
    return
  }
  if (sending) return
  sending = true
  try { ws.send(entry) } catch (e) { /* ここで console を使わない */ }
  sending = false
}

function flush() {
  if (!connected || pending.length === 0) return
  const queued = pending
  pending = []
  sending = true
  try { for (const e of queued) ws.send(e) } catch (e) { /* 落とす */ }
  sending = false
}

function stringify(args) {
  const parts = []
  for (const a of args) {
    if (typeof a === 'string') { parts.push(a); continue }
    if (a instanceof Error) { parts.push(a.stack || (a.name + ': ' + a.message)); continue }
    try { parts.push(JSON.stringify(a)) } catch (e) { parts.push(String(a)) }
  }
  return parts.join(' ')
}

for (const level of ['log', 'warn', 'error']) {
  const orig = console[level].bind(console)
  console[level] = (...args) => {
    orig(...args)                       // 元へ必ず転送する。ブラウザ側の表示は殺さない
    try { post(level, stringify(args)) } catch (e) { /* 落とす */ }
  }
}

// 素通りする経路も拾う。プラグインの import 失敗はここに出る。
window.addEventListener('error', (e) => {
  try { post('error', `${e.message} @ ${e.filename}:${e.lineno}:${e.colno}`) } catch (x) {}
})
window.addEventListener('unhandledrejection', (e) => {
  try { post('error', 'unhandledrejection: ' + String(e.reason)) } catch (x) {}
})

ws.onConnection((isConnected) => {
  connected = !!isConnected
  if (connected) flush()
})

console.log('[console-forward] installed (Debug Bridge の設定に依存せず転送します)')
