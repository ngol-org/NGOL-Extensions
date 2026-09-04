// NGOL for Blender - 制御パネル（WebUI 拡張プラグイン）
//
// .cs は一切要らない。このファイルを <ngolRoot>/WebUI/plugins/ に置くだけで出る。
//    サーバーは plugins/ を「要求のたびに」走査するので、NGOL の再起動も要らない。
//    URL に ?v=<更新時刻> が付くので、ブラウザの F5 だけで新しい版が読まれる。
//    MCP の reload_webui を使えば F5 すら要らない。
//
// 本体（WebUI/src/ ・ NodeGraphModLab.Core）には手を入れていない。
//    このファイルを plugins/ に置くだけで出る。困っても window.NGOL の中で解く。
//
// 送受信の作りについて（実装を読んで決めたこと。推測ではない）:
//    Core の execute_node_response（ServerDtos.cs）には **相関 ID が無い**。
//    type / success / outputs しか返らないので、2 件を同時に投げると
//    どちらの答えか区別できない。
//    => 要求は 1 件ずつ直列に流す。待ち行列を自前で持つ。

const NGOL = window.NGOL
const { React, html, registerPanel, ws } = NGOL

// ガードの外に 1 行出す。出なければ「読み込まれていない」と即断できる。
//    （出ない＝ファイルが実行されていない／出るのに画面に無い＝登録か描画の問題）
console.log('[ngol-blender] plugin loaded (blender-control-panel.js)')

// ---------------------------------------------------------------------------
// ノードの口（ポート名は BlenderNodes.cs の [NodePort] から写した。推測していない）
// ---------------------------------------------------------------------------

const NODE = {
  ping:  'blender.ping',
  stat:  'blender.scene.stat',
  list:  'blender.object.list',
  spawn: 'blender.object.spawn',
  grid:  'blender.object.grid',
  move:  'blender.object.move',
  clear: 'blender.object.clear',
  shot:  'blender.capture',
}

// 既定値は BlenderNodes.cs のポート既定値と同じ値を書くこと。二重管理になっている。
//    （nodeTypeInfo.ports に既定値は入らないので JS からは読めない）
const DEFAULTS = {
  shape: 'monkey',
  count: 8,
  radius: 4,
  size: 1,
  cols: 6,
  rows: 6,
  gap: 2,
  spin: 15,
  dz: 0,
  scale: 1,
  spawnPrefix: 'NGOL',
  gridPrefix: 'GRID',
  clearPrefix: 'NGOL,GRID,CONE,PY',
}

const SHAPES = ['cube', 'sphere', 'icosphere', 'cone', 'cylinder', 'monkey']

const TIMEOUT_MS = 20000

// ---------------------------------------------------------------------------
// 直列の要求キュー
// 相関 ID が無いので、同時に 1 件しか投げない。
// ---------------------------------------------------------------------------

const queue = []
let inFlight = null
let listenerBound = false

// 実測で踏んだ: プラグインが読み込まれた時点では **WS はまだ繋がっていない**。
//    そのまま送ると本体が `[WS] not connected, message dropped` と警告して
//    **黙って捨てる**（例外は飛ばない）。初回の自動更新がここで消えていた。
//
// ただし「繋がるまで待つ」だけにすると逆の穴が開く--
//    `ws.onConnection` は **購読した時点の状態を教えてくれない**（wsClient.ts:84。
//    変化したときにしか呼ばれない）。既に繋がった後に読み込まれると永久に待つ。
// => 接続通知で開ける ＋ 猶予を過ぎたら開ける、の両方を置く。
let wsReady = false

function openGate(why) {
  if (wsReady) return
  wsReady = true
  pump()
}

ws.onConnection((connected) => {
  if (connected) openGate('onConnection')
  else wsReady = false
})

// 既に接続済みで読み込まれた場合の逃げ道。onConnection は鳴らないので時間で開ける。
setTimeout(() => openGate('grace'), 2000)

function bindListener() {
  if (listenerBound) return
  listenerBound = true
  ws.onMessage((msg) => {
    if (!msg || msg.type !== 'execute_node_response') return
    const cur = inFlight
    if (!cur) return                    // 自分が投げたものではない（WebUI 本体の Run 等）
    inFlight = null
    clearTimeout(cur.timer)
    cur.resolve(msg)
    pump()
  })
}

function pump() {
  if (inFlight || queue.length === 0) return
  if (!wsReady) return              // 繋がる前に送ると黙って捨てられる。並べて待つ
  const job = queue.shift()
  inFlight = job
  job.timer = setTimeout(() => {
    if (inFlight !== job) return
    inFlight = null
    job.resolve({ success: false, errorMessage: `timeout after ${TIMEOUT_MS} ms`, outputs: {} })
    pump()
  }, TIMEOUT_MS)
  try {
    ws.send({ type: 'execute_node', nodeTypeId: job.nodeTypeId, inputs: job.inputs })
  } catch (e) {
    inFlight = null
    clearTimeout(job.timer)
    job.resolve({ success: false, errorMessage: String(e), outputs: {} })
    pump()
  }
}

function runNode(nodeTypeId, inputs) {
  bindListener()
  return new Promise((resolve) => {
    queue.push({ nodeTypeId, inputs: inputs || {}, resolve, timer: 0 })
    pump()
  })
}

// outputs は execute_node_response で {ポート名: 値} の素の JSON。
function out(resp, port, fallback) {
  const v = resp && resp.outputs ? resp.outputs[port] : undefined
  return v === undefined || v === null ? fallback : v
}

// ---------------------------------------------------------------------------
// 見た目（React の style は「オブジェクト」。文字列を渡すと Plugin error になる）
// ---------------------------------------------------------------------------

const S = {
  root:    { padding: '10px', fontSize: '12px', minWidth: '300px', maxWidth: '380px' },
  section: { marginBottom: '10px', paddingBottom: '8px', borderBottom: '1px solid rgba(255,255,255,0.12)' },
  h:       { fontWeight: 'bold', marginBottom: '6px', opacity: 0.85 },
  row:     { display: 'flex', alignItems: 'center', gap: '6px', marginBottom: '4px' },
  label:   { width: '68px', opacity: 0.7, flexShrink: 0 },
  num:     { width: '62px' },
  text:    { flex: '1 1 auto', minWidth: '0' },
  btnRow:  { display: 'flex', gap: '6px', flexWrap: 'wrap', marginTop: '6px' },
  stat:    { display: 'grid', gridTemplateColumns: 'auto 1fr', gap: '2px 8px', lineHeight: '1.5' },
  key:     { opacity: 0.6 },
  val:     { fontFamily: 'monospace', wordBreak: 'break-all' },
  log:     { fontFamily: 'monospace', fontSize: '11px', lineHeight: '1.45', maxHeight: '150px',
             overflowY: 'auto', background: 'rgba(0,0,0,0.25)', padding: '6px', borderRadius: '4px' },
  ok:      { color: '#6ee7a8' },
  ng:      { color: '#ff8a80' },
  busy:    { opacity: 0.55, pointerEvents: 'none' },
  foot:    { marginTop: '8px', opacity: 0.45, fontSize: '10px' },
}

function BlenderPanel() {
  const [busy, setBusy] = React.useState(false)
  const [stat, setStat] = React.useState(null)
  const [logs, setLogs] = React.useState([])

  // 入力欄（値はここが正。ノードには毎回明示的に渡す）
  const [shape, setShape] = React.useState(DEFAULTS.shape)
  const [count, setCount] = React.useState(DEFAULTS.count)
  const [radius, setRadius] = React.useState(DEFAULTS.radius)
  const [size, setSize] = React.useState(DEFAULTS.size)
  const [cols, setCols] = React.useState(DEFAULTS.cols)
  const [rows, setRows] = React.useState(DEFAULTS.rows)
  const [gap, setGap] = React.useState(DEFAULTS.gap)
  const [spin, setSpin] = React.useState(DEFAULTS.spin)
  const [dz, setDz] = React.useState(DEFAULTS.dz)
  const [scale, setScale] = React.useState(DEFAULTS.scale)
  const [spawnPrefix, setSpawnPrefix] = React.useState(DEFAULTS.spawnPrefix)
  const [gridPrefix, setGridPrefix] = React.useState(DEFAULTS.gridPrefix)
  const [clearPrefix, setClearPrefix] = React.useState(DEFAULTS.clearPrefix)

  const say = React.useCallback((ok, text) => {
    setLogs((prev) => [{ ok, text, at: new Date().toLocaleTimeString() }, ...prev].slice(0, 40))
  }, [])

  // 1 回分の呼び出し。成否は success ではなく「result 文言」まで出す。
  const call = React.useCallback(async (label, nodeTypeId, inputs) => {
    setBusy(true)
    try {
      const resp = await runNode(nodeTypeId, inputs)
      const ok = !!resp.success
      const detail = ok
        ? String(out(resp, 'result', '(no result port)'))
        : String(resp.errorMessage || 'failed')
      const ms = resp.durationMs != null ? ` [${Math.round(resp.durationMs)}ms]` : ''
      say(ok, `${label}: ${detail}${ms}`)
      // console にも出す。aa-console-forward.js が NGOL 側へ転送するので、
      //    利用者が何を押して何が返ったかを **エージェント側からも読める**
      //    （get_browser_debug_log）。人に「画面を見てください」と頼む往復が減る。
      const line = `[ngol-blender] ${ok ? 'OK ' : 'NG '} ${nodeTypeId} ${JSON.stringify(inputs)} -> ${detail}${ms}`
      if (ok) console.log(line); else console.warn(line)
      return resp
    } finally {
      setBusy(false)
    }
  }, [say])

  const refresh = React.useCallback(async () => {
    // prefix は空にすると「全体だけ数える」。ここでは spawn の接頭辞で数える。
    const resp = await call('状態', NODE.stat, { prefix: spawnPrefix })
    if (!resp.success) { setStat(null); return }
    setStat({
      scene:   String(out(resp, 'scene_name', '?')),
      file:    String(out(resp, 'blend_file', '?')),
      objects: out(resp, 'object_count', 0),
      meshes:  out(resp, 'mesh_count', 0),
      matched: out(resp, 'matched', 0),
      active:  String(out(resp, 'active_object', '')),
      frame:   out(resp, 'frame', 0),
      byType:  String(out(resp, 'by_type', '{}')),
    })
  }, [call, spawnPrefix])

  // 開いた直後に 1 回だけ状態を取る（ping も兼ねる）
  React.useEffect(() => { refresh() }, [])   // eslint-disable-line react-hooks/exhaustive-deps

  const n = (setter) => (e) => setter(Number(e.target.value))
  const t = (setter) => (e) => setter(e.target.value)

  return html`
    <div style=${{ ...S.root, ...(busy ? S.busy : null) }}>

      <div style=${S.section}>
        <div style=${S.h}>シーンの状態</div>
        ${stat
          ? html`<div style=${S.stat}>
              <span style=${S.key}>scene</span><span style=${S.val}>${stat.scene}</span>
              <span style=${S.key}>file</span><span style=${S.val}>${stat.file}</span>
              <span style=${S.key}>objects</span><span style=${S.val}>${stat.objects} (mesh ${stat.meshes})</span>
              <span style=${S.key}>"${spawnPrefix}"</span><span style=${S.val}>${stat.matched}</span>
              <span style=${S.key}>active</span><span style=${S.val}>${stat.active || '-'}</span>
              <span style=${S.key}>frame</span><span style=${S.val}>${stat.frame}</span>
              <span style=${S.key}>by type</span><span style=${S.val}>${stat.byType}</span>
            </div>`
          : html`<div style=${{ opacity: 0.6 }}>まだ取得していません</div>`}
        <div style=${S.btnRow}>
          <button onClick=${refresh}>更新</button>
          <button onClick=${() => call('ping', NODE.ping, {})}>Ping</button>
          <button onClick=${async () => {
            const r = await call('一覧', NODE.list, { prefix: spawnPrefix, type: '', limit: 20 })
            if (r.success) say(true, '  ' + String(out(r, 'names', '')).split('\n').filter(Boolean).join(' / '))
          }}>一覧</button>
        </div>
      </div>

      <div style=${S.section}>
        <div style=${S.h}>作る - 輪 (blender.object.spawn)</div>
        <div style=${S.row}>
          <span style=${S.label}>shape</span>
          <select value=${shape} onChange=${t(setShape)} style=${S.text}>
            ${SHAPES.map((s) => html`<option key=${s} value=${s}>${s}</option>`)}
          </select>
        </div>
        <div style=${S.row}>
          <span style=${S.label}>count</span>
          <input type="number" min="1" max="500" style=${S.num} value=${count} onChange=${n(setCount)} />
          <span style=${S.label}>radius</span>
          <input type="number" step="0.5" style=${S.num} value=${radius} onChange=${n(setRadius)} />
        </div>
        <div style=${S.row}>
          <span style=${S.label}>size</span>
          <input type="number" step="0.1" style=${S.num} value=${size} onChange=${n(setSize)} />
          <span style=${S.label}>prefix</span>
          <input type="text" style=${S.text} value=${spawnPrefix} onChange=${t(setSpawnPrefix)} />
        </div>
        <div style=${S.btnRow}>
          <button onClick=${async () => {
            await call('輪を作る', NODE.spawn,
              { shape, count, radius, size, prefix: spawnPrefix })
            refresh()
          }}>作る</button>
        </div>
      </div>

      <div style=${S.section}>
        <div style=${S.h}>作る - 格子 (blender.object.grid)</div>
        <div style=${S.row}>
          <span style=${S.label}>cols</span>
          <input type="number" min="1" max="60" style=${S.num} value=${cols} onChange=${n(setCols)} />
          <span style=${S.label}>rows</span>
          <input type="number" min="1" max="60" style=${S.num} value=${rows} onChange=${n(setRows)} />
        </div>
        <div style=${S.row}>
          <span style=${S.label}>gap</span>
          <input type="number" step="0.5" style=${S.num} value=${gap} onChange=${n(setGap)} />
          <span style=${S.label}>prefix</span>
          <input type="text" style=${S.text} value=${gridPrefix} onChange=${t(setGridPrefix)} />
        </div>
        <div style=${S.btnRow}>
          <button onClick=${async () => {
            await call('格子を作る', NODE.grid,
              { shape, cols, rows, gap, size, prefix: gridPrefix })
            refresh()
          }}>作る</button>
        </div>
      </div>

      <div style=${S.section}>
        <div style=${S.h}>動かす (blender.object.move)</div>
        <div style=${S.row}>
          <span style=${S.label}>spin度</span>
          <input type="number" step="5" style=${S.num} value=${spin} onChange=${n(setSpin)} />
          <span style=${S.label}>dz</span>
          <input type="number" step="0.1" style=${S.num} value=${dz} onChange=${n(setDz)} />
        </div>
        <div style=${S.row}>
          <span style=${S.label}>scale</span>
          <input type="number" step="0.05" style=${S.num} value=${scale} onChange=${n(setScale)} />
        </div>
        <div style=${S.btnRow}>
          <button onClick=${() => call('動かす', NODE.move,
            { prefix: spawnPrefix, spin, dz, scale })}>動かす</button>
          <button onClick=${() => call('回すx4', NODE.move,
            { prefix: spawnPrefix, spin: spin * 4, dz: 0, scale: 1 })}>大きく回す</button>
        </div>
      </div>

      <div style=${S.section}>
        <div style=${S.h}>片付ける (blender.object.clear)</div>
        <div style=${S.row}>
          <span style=${S.label}>prefix</span>
          <input type="text" style=${S.text} value=${clearPrefix} onChange=${t(setClearPrefix)} />
        </div>
        <div style=${{ opacity: 0.55, fontSize: '11px', marginTop: '2px' }}>
          カンマ区切りで複数。空にすると断られます（全消しの誤爆を防ぐため）
        </div>
        <div style=${S.btnRow}>
          <button onClick=${async () => {
            const r = await call('片付け', NODE.clear, { prefix: clearPrefix })
            if (r.success) {
              const orphan = out(r, 'orphan_meshes', 0)
              // 0 のままかを見る。増え続けるなら .blend に何かが漏れている。
              say(orphan === 0, `  orphan_meshes = ${orphan}`)
            }
            refresh()
          }}>消す</button>
        </div>
      </div>

      <div style=${S.section}>
        <div style=${S.h}>記録</div>
        <div style=${S.log}>
          ${logs.length === 0
            ? html`<div style=${{ opacity: 0.5 }}>まだ何もしていません</div>`
            : logs.map((l, i) => html`
                <div key=${i} style=${l.ok ? S.ok : S.ng}>
                  ${l.at} ${l.ok ? 'OK' : 'NG'} ${l.text}
                </div>`)}
        </div>
        <div style=${S.btnRow}>
          <button onClick=${() => setLogs([])}>記録を消す</button>
          <button onClick=${() => call('画面を撮る', NODE.shot, { name: 'panel.png' })}>画面を撮る</button>
        </div>
      </div>

      <div style=${S.foot}>
        blender-control-panel.js - WebUI 拡張プラグイン（本体は未変更）
        ${busy ? ' / 実行中...' : ''}
      </div>
    </div>`
}

registerPanel({
  id: 'ngol.blender.control',
  title: 'Blender',
  component: BlenderPanel,
})

console.log('[ngol-blender] panel registered: ngol.blender.control')
