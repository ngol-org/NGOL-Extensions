# ノード一覧

**70 本。** どれも `.cs` のソースのまま配られ、導入先でコンパイルされます。手元で書き換えれば、そのまま挙動が変わります。

⭐ **55 本は拡張なしで動きます。** 標準の .NET と P/Invoke だけで書かれているので、NGOL 本体だけあれば使えます。拡張が要るのは残りの 15 本で、下の表に印を付けてあります。

| 印 | 要る拡張 | 無いとどうなるか |
|---|---|---|
| `code` | `ngol.ext.code` | 逆アセンブラが無いのでコンパイルに失敗する |
| `il` | `ngol.ext.il` | 同上（IL 書き換えのライブラリ） |
| `hook` | `ngol.ext.native-hook` | コンパイルは通る。実行すると「サービスが無い」と答えて何もしない |

---

## code — 実行中のコードを読む（17 本）

| ノード | 拡張 | |
|---|---|---|
| `ngol.code.module_list` | | 載っているモジュールを大きい順に。どれが本体か一目で分かる |
| `ngol.code.module_base` | `code` | モジュールの載り位置。絶対アドレスと RVA の変換に要る |
| `ngol.code.export_address` | | export 名を番地へ。**綴りを間違えても番地にはならず「無い」と返る** |
| `ngol.code.disasm` | `code` | RVA から逆アセンブル。呼び出し先と分岐先も返す |
| `ngol.code.disasm_peek` | `code` | 絶対アドレスから数命令だけ覗く |
| `ngol.code.disasm_scan` | `code` | 複数の RVA を走査し、条件に合うものだけ返す |
| `ngol.code.function_bounds` | | ある番地を含む関数の先頭と末尾（PE の例外表から） |
| `ngol.code.aob_scan` | | バイト列を探す（`??` でワイルドカード）。RVA 直書きより版に強い |
| `ngol.code.find_string` | | 文字列を探す。UTF-16LE と ASCII を選べる |
| `ngol.code.xref_find` | `code` | ある番地を指している命令を探す |
| `ngol.code.xref_index_build` | `code` | 参照の索引を背景で作る |
| `ngol.code.xref_lookup` | | 作った索引を即座に引く |
| `ngol.code.xref_dump` | | 索引を丸ごと CSV へ |
| `ngol.code.pe_info` | | ディスク上の PE の素性。**対象は起動していなくてよい** |
| `ngol.code.pe_imports` | | 同じくディスク上の PE の import 表 |
| `ngol.code.pdb_lookup` | | 名前と RVA を PDB で相互に引く。**「一致 0 件」と「そもそも読めていない」を言い分ける** |
| `ngol.code.pdb_type_layout` | | 型の全欄を名前・オフセット・大きさで出す。**触っている関数を逆アセンブルして組み立てなくてよい** |

## memory — メモリを読む・書く（11 本）

| ノード | 拡張 | |
|---|---|---|
| `ngol.mem.region_probe` | | 範囲をページ単位で調べ、どこが読めるかを返す |
| `ngol.mem.read_bytes` | | 生のバイト列。**返る 16 進はそのまま `aob_scan` へ渡せる** |
| `ngol.mem.read_value` | | 型を指定して読む。**整数と浮動小数の両方の読みを常に返す**（4 バイトはどちらにも見えるため） |
| `ngol.mem.read_ptr` | | 8 バイトのポインタ |
| `ngol.mem.read_string` | | ヌル終端の文字列 |
| `ngol.mem.write` | | 型を指定して書く。**対象の状態を変える** |
| `ngol.mem.value_scan` | | 画面に出ている数値の在処を探す |
| `ngol.mem.value_next` | | 変化で候補を絞る。**正確な値を知らなくてよい** |
| `ngol.mem.pointer_path` | | 固定位置からの道順。**再起動後も同じ場所へ辿り着ける** |
| `ngol.mem.scan_ptr_range` | | ある範囲でコードを指していそうな 8 バイト値を探す |
| `ngol.mem.sample_in_function` | | **指定した関数の中に居るときだけ**番地を読む。呼び出しの合間の値が混ざらない |

## hook — 呼び出しに割り込む（9 本）

| ノード | 拡張 | |
|---|---|---|
| `ngol.hook.safety_check` | `code` | **設置する前に、そこへフックしてよいか判定する**。SAFE / WARN / DANGER |
| `ngol.hook.watch_function` | `hook` | 回数と直近の引数を記録。**挙動は変えない** |
| `ngol.hook.trace_calls` | `hook` | 1 件ずつ順に記録し、**取りこぼした件数も返す** |
| `ngol.hook.skip_function` | `hook` | 本体を走らせず、戻り値を決める |
| `ngol.hook.patch_bytes` | `code` | 命令を直接書き換える。**関数の途中にも入る** |
| `ngol.hook.patch_revert` | | 書き換えを戻す。⚠ `patch_bytes` と対で使う |
| `ngol.hook.native_callback` | `il` | ネイティブの番地にマネージドのコールバックを当てる |
| `ngol.hook.managed_skip` | `il` | マネージドメソッドを IL 書き換えで飛ばす |
| `ngol.hook.managed_timing` | `il` | クラスの全メソッドに計測を差し込み、集計する |

## win32 — 別のアプリの窓を扱う（10 本）

**OS が窓のために用意している口だけで扱います。**

| ノード | |
|---|---|
| `ngol.win32.window_list` | 窓の一覧。ハンドル・プロセス・UI スレッド |
| `ngol.win32.child_windows` | 窓の中のコントロールを木で。クラス・id・位置・文字 |
| `ngol.win32.window_wait` | 窓が現れて応答するまで待つ |
| `ngol.win32.window_move` | 移動・リサイズ。**手でドラッグできない窓にも効く** |
| `ngol.win32.window_close` | 閉じる。押すボタンが無い問い合わせを片付ける |
| `ngol.win32.window_menus` | メニューを読む |
| `ngol.win32.menu_command` | メニュー項目を名前で探して実行する |
| `ngol.win32.modal_state` | 対話箱で止まっているかを判定し、答えることもできる |
| `ngol.win32.set_control_text` | 入力欄へ文字を入れる |
| `ngol.win32.pixel_colors` | 指定した点の色を数値で読む |

## proc — プロセスを触る（5 本）

| ノード | 拡張 | |
|---|---|---|
| `ngol.proc.ext_info` | | **拡張・capability・同梱ライブラリの一覧**。「配ったもの」と「実際に読まれたもの」の版を並べる |
| `ngol.proc.call_fn0` | | 引数なしでネイティブ関数を呼ぶ。戻りは 16 進（64bit 全体）と整数（下位 32bit）の両方で出る |
| `ngol.proc.call_fn1` | | ポインタ 1 つで呼ぶ |
| `ngol.proc.thread_stacks` | | 全スレッドの居場所を一度に。**窓に触らないので、応答が無い相手でも返る** |
| `ngol.proc.thread_activity` | | どのスレッドが実際に走っているか。**CPU 時間では見えない「起きてすぐ寝る」を分ける** |

⚠ **`call_*` は「安全と分かっている関数」にだけ。** 呼ぶ前に `ngol.code.disasm` で引数の数と幅を確かめてください。

## dev — NGOL 自身を測る・当てる（7 本）

**対象がホストのアプリではなく NGOL 本体**、という点だけで括ってあります。
⭐ **どのホストでもそのまま動きます**（ホスト固有の呼び出しを 1 つも持たない）。

| ノード | 拡張 | |
|---|---|---|
| `ngol.dev.exec_thread` | | いま走っているのがホストのメインスレッドかどうかを、id の突き合わせで言う |
| `ngol.dev.persistent_pulse` | | 毎フレームのコールバックが**どのスレッドで・どれくらいの間隔で**回るかを測る |
| `ngol.dev.tick_source` | | NGOL を回しているループを一時的に引き取る。⚠ **期限つきで、必ず自分で返す** |
| `ngol.dev.drive_update` | | NGOL の更新をこのノードから回す。**ホスト側のスレッドが止まったとき**に、実行とホットリロードを戻す |
| `ngol.dev.slow_probe` | | 長い処理が、呼ぶ側の待ちの限界とどう噛み合うかを測る |
| `ngol.kvstore.manage` | | NGOL の永続ストアを見る・出す・戻す・掃除する |
| `ngol.dev.kvstore_transaction_patch` | `il` | **NGOL 自身に当てる**。まとめ書きをトランザクションに束ねる |

⚠ **`exec_thread` は最初に読む価値があります。** NGOL の `MainThreadDispatch` は
「メインスレッド」という名前ですが、実際に届くのは `Tick()` を呼んだスレッドです。
ホストが NGOL を呼ばない構成では、それは NGOL 自前のスレッドであってホストのものではありません。
そこからホストの UI に触るとホストごと落ちます。

## graphics — 描かれた絵を扱う（5 本）

| ノード | |
|---|---|
| `ngol.gfx.present_address` | 提示関数の実アドレス（`Present` / `Present1` の両方）。⚠ **flip モデルのアプリは `Present1` を呼ぶので、片方だけのフックは鳴らない** |
| `ngol.gfx.capture_backbuffer` | 生きた swapchain のバックバッファを読む。D3D11 と D3D12 を自動判別。⚠ **D3D11 経路は対象の即時コンテキストを別スレッドから借りるので、描き続けている対象では競合が残る**（ノードの説明を読むこと） |
| `ngol.gfx.capture_window` | 窓 1 つを画像へ。題名が複数一致したら撮らずに止まる。手前に別の窓があるときも止まる |
| `ngol.gfx.overlay_dice` | 対象が描いた上へ重ねて描く見本（D3D11）。**絵はノードの側で組み立てる**。対象の swapchain から装置を借りるので、**対象が D3D11 で提示していないと断る** |
| `ngol.gfx.draw_cube` | 回る立方体を対象の窓の中へ描く見本（D3D12）。⭐ **対象が描画のコードを持っていなくてよい**——クライアント領域に子ウィンドウを作り、装置も swapchain もノードが自前で持つ。⚠ 全画面排他の相手には出ない。シェーダは実行時に `d3dcompiler_47.dll` で組み立てる |

### 重ね方は 2 通りあり、得意な相手が逆

**名前が似ていても仕組みは別物です。** どちらを使うかは対象で決まります。

| | `ngol.gfx.overlay_dice` | `ngol.gfx.draw_cube` |
|---|---|---|
| 描く先 | **対象のバックバッファ**（`Present` に割り込んで、その中で描く） | **自前の子ウィンドウ**（対象のクライアント領域に重ねて作る） |
| 使う装置 | **対象のものを借りる**（`GetDevice` に `IID_ID3D11Device`） | **自前で作る**（`D3D12CreateDevice`） |
| 対象への要求 | **D3D11 で提示していること**。そうでなければ自分で外して報告する | ⭐ **無し。** 描画のコードを持たない相手でもよい |
| 弱点 | 対象の提示経路に割り込むので、⚠ **先客のオーバーレイと取り合いになる** | 対象のフレームと合成しないので、⚠ **全画面排他だと隠れる** |
| ソース | `graphics/dice/` | `graphics/cube/` |

## il — .NET アセンブリを見る（2 本）

| ノード | |
|---|---|
| `ngol.il.assembly_inspect` | 読み込み済みアセンブリの素性と場所。「DLL は在るのに読まれない」の切り分けに |
| `ngol.il.assembly_surface` | アセンブリ数と内訳。`GetTypes()` に失敗したものも本数と理由を返す |

## link — 別の NGOL とつなぐ（2 本）

| ノード | |
|---|---|
| `ngol.link.probe` | **この機械で待ち受けている NGOL を見つける**。ポート番号をグラフに書かずに済む（設定した番号と実際の番号は食い違いうる） |
| `ngol.link.run_node` | 別の NGOL でノードを 1 つ実行し、出力を持ち帰る。**2 つのアプリをまたぐ作業の最小の 1 歩** |

## shm / qt（2 本）

| ノード | |
|---|---|
| `ngol.shm.image_info` | 名前の付いた場所に置かれた絵を見る。置く側と拾う側のどちらが止まったかを、場所だけ見て判断する |
| `qt.menu.tree` | Qt アプリのメニュー。Qt は自分でメニューを描くので Win32 の列挙では何も返らない。Qt に直接聞く |

---

## ノードの構成ファイル

ノードは `.cs` 1 本とは限りません。

| 拡張子 | 何か |
|---|---|
| `.srclist` | **そのノードを構成するファイルの一覧**（53 本にある）。`_shared/` の共有部品を取り込むのに使う |
| `.rsp` | 追加の参照（3 本にある）。`/r:` で名前を足す |

```
# DisasmNode.srclist
DisasmNode.cs
..\_shared\NgolModuleDefault.cs
..\_shared\NgolSafeMemory.cs
```

⭐ **`.srclist` と `.rsp` はノードの一部です。** ノードを配るときは `.cs` と一緒に運んでください。

`.rsp` が要るのは、`netstandard` が型を転送するだけで実体を持たない場合です。

```
# ShmImageInfoNode.rsp
/r:System.IO.MemoryMappedFiles.dll
```

---

## 置かれ方

`scripts/build.ps1` は、この `samples/nodes/` を丸ごと
`<出力先>/Nodes/CustomNodes/cs/ext/` へ運びます。拡張のライブラリだけ配ってもノードが無ければ使えないので、両方が一緒に動きます。

ブリッジの `deploy.ps1` も、そのホスト向けのノードの隣へこの一式を置きます。

---

## 書き換えて試す

配られた `.cs` はホットリロードの対象です。**ホストを起動したまま書き換えると、そのまま反映されます。**

```
<ブリッジの配置先>/Nodes/CustomNodes/cs/ext/code/DisasmNode.cs
<題材アプリの dist>/Nodes/CustomNodes/cs/code/DisasmNode.cs
```

題材アプリだけは `ext/` を挟みません。ホスト向けのノードが無いので、そのまま `cs/` の下に置かれます。

ホストのログに次の行が出れば成功です。

```
[RoslynCompiler] Re-registered dynamic node: ngol.code.disasm
```

⚠ **失敗しても古いコードが動き続けます。** 「直したのに変わらない」ときは、まずログでコンパイルが通ったかを見てください。
