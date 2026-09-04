# D3DTargetApp — 対象アプリを用意せずに試すための題材

**Direct3D 11 で描くだけの小さなネイティブアプリに、NGOL を最初から載せたもの。**
このリポジトリのノードを、ホストを用意せずに一通り動かせる唯一の入口です。

`bridges/` のブリッジは「他人のアプリに後から載せる」形ですが、こちらは逆で、
自分のアプリへ載せる形の例です。アプリが NGOL に触れるのは `Bridge_Start()` を呼ぶ 1 行だけで、
NGOL の型もヘッダーも持ちません。

## 作って動かす

```powershell
./scripts/build.ps1                            # 先にこちら（NGOL 本体・WebUI・拡張・ネイティブ）
./samples/apps/D3DTargetApp/prepare-dist.ps1   # アプリとブリッジを作り、build/dist へまとめる
./samples/apps/D3DTargetApp/build/dist/D3DTargetApp.exe
```

順番が要ります。`prepare-dist.ps1` が作るのはアプリとブリッジだけで、NGOL 本体・WebUI・
ネイティブフックのバックエンドは `scripts/build.ps1` が作ります。飛ばすと、起動はしますが
ブラウザに何も出ず、フック系のノードが「拡張が読み込まれていない」と答えます。

起動すると同じプロセスで NGOL が立ち上がり、`http://localhost:11156/` でエディタが開きます。

| 引数 | |
|---|---|
| `-Port <n>` | 待ち受けポートを変える（既定 11156） |
| `-SkipNative` | アプリとブリッジのビルドを飛ばし、前回の成果物を使う |
| `-DistRoot <path>` | まとめ先を変える（既定は `build/dist`） |

必要なものは .NET SDK と CMake ＋ MSVC です。CMake は PATH に無くても、Visual Studio 同梱のものを `vswhere` から探します。

## 中身

| | |
|---|---|
| `app/main.cpp` | ウィンドウを出して D3D11 で描く。NGOL を知らない側 |
| `app/analysis_targets.cpp` | 解析の的になる関数 9 本と変数 1 つ。すべて export し、インライン化を止めてある |
| `bridge/bridge.cpp` | `Bridge_Start()` の実体。hostfxr で CLR を起こして NGOL を読み、`Present` をフックして毎フレーム NGOL を回す |
| `bridge/PresentHook.cpp` | ダミーのスワップチェーンから `IDXGISwapChain` の vtable を取り、`Present` を差し替える |
| `bridge/clr_activator/` | ネイティブから .NET へ入る入口。`bridges/NgolActivator/` と同じもの |
| `graphs/` | 添付のグラフ。`prepare-dist.ps1` が `build/dist/Graphs/` へ写す |
| `CMakeLists.txt` | アプリ（EXE）とブリッジ（DLL）。静的 CRT、絶対パスを PDB に焼き込まない設定つき |

## 解析の的

`app/analysis_targets.cpp` の関数は、ノードの答えを外から突き合わせられるように、
意図した形が機械語に残るよう書いてあります。期待値はソースのコメントにあります。

| export 名 | 何の的か | 見るべきもの |
|---|---|---|
| `NgolTarget_Add` | 最小例 | `disasm` / `function_bounds` がそのまま読める。`safety_check` は SAFE |
| `NgolTarget_Sum5` | 5 引数 | x64 では第 5 引数がスタック渡し。`disasm` に `[rsp+28h]` が出る |
| `NgolTarget_Scale` | float / double 引数 | XMM 渡し。`watch_function` の浮動小数点の記録 |
| `NgolTarget_LockedInc` | LOCK 前置命令 | `safety_check` が DANGER と言って止める。呼ぶと `g_ngolAnalysisCounter` が 1 増える |
| `NgolTarget_ReadGlobal` | RIP 相対でグローバルを読む | `xref_find` で変数の参照元として見つかる |
| `NgolTarget_Top` / `_Mid` / `_Leaf` | 3 段の呼び出し | `disasm` の `call_targets` を芋づるに辿る |
| `NgolTarget_Quiet` | ログを出さない関数 | 呼ばれ方を `watch_function` で実測する。`x` を渡すと `(x ^ 0x5a5a) + 7` が返る |
| `g_ngolAnalysisCounter` | 上の 2 本が触る変数 | `read_value` / `xref_find` / `region_probe` の的 |

## 鎖を手で辿る

このリポジトリの主張は「文字列から参照元、関数、逆アセンブル、フック、呼び出し、メモリの読み戻しまでが
1 プロセスの中で閉じる」ことです。題材アプリの的で、上から順に押すとそれが確かめられます。
右の列はソースから決まる答えなので、手元の結果と突き合わせてください。

| # | ノード | 入力 | 期待 |
|---|---|---|---|
| 1 | `ngol.proc.ext_info` | | 拡張 3 件がすべて読み込まれている |
| 2 | `ngol.code.export_address` | `module` に `D3DTargetApp.exe`、`names` に上の export 名 | すべて番地に解決する。綴りを間違えると「無い」と返る。このノードだけは `module` を省略できない |
| 3 | `ngol.code.disasm` | `NgolTarget_Sum5` の RVA（`module` は空でよい。以下同じ） | `add eax,[rsp+28h]`。第 5 引数がスタックにある |
| 4 | `ngol.hook.safety_check` | `NgolTarget_LockedInc` | DANGER（`lock` 前置を検出） |
| 5 | 同上 | `NgolTarget_Add` | SAFE。判定器の両側を見る |
| 6 | `ngol.hook.watch_function` | `NgolTarget_Quiet` | フックが入る |
| 7 | `ngol.proc.call_fn1` | 同関数へ `0x1234` | `0x4875` |
| 8 | もう一度 `watch_function` | | `hit_count` が 1、記録された引数が `0x1234` |
| 9 | `ngol.code.xref_find` | `g_ngolAnalysisCounter` の番地 | 2 件。`lock xadd`（LockedInc）と `mov eax`（ReadGlobal） |
| 10 | `ngol.mem.read_value` | 同変数 | 0 |
| 11 | `ngol.proc.call_fn0` | `NgolTarget_LockedInc` | 1 |
| 12 | もう一度 `read_value` | 同変数 | 1。呼んだ結果がメモリに残っている |
| 13 | `ngol.code.find_string` | `NgolTarget_LockedInc`（ASCII） | export 名の文字列が見つかる |
| 14 | `ngol.code.aob_scan` | #3 で読んだバイト列 | #3 と同じ番地。鎖が閉じる |

手順 7 と 11 は、押す前に答えを決めてから押してください。予測と一致して初めて「読めている」と言えます。

## 添付のグラフ

`graphs/overlay-dice-on-target.json` は、アプリが描いた絵の上に回転するサイコロを重ねます。
準備の側で画面へ出す呼び出しを見つけてスワップチェーンを捕まえ、描画の側で絵を出します。
準備を 2 回、そのあと描画を 1 回押してください。サイコロが出ている間に準備を走らせないでください。

WebUI の Load Graph から開けます。`samples/graphs/` には同じ題材の別の組み方もあります。

## デバッグレイヤ

D3D11 のデバッグレイヤは既定で無効です。`-d3ddebug` を付けて起動すると有効になります（無い環境では自動で無効に落ちます）。

常時有効にはしないでください。デバッグレイヤは、別スレッドから同じデバイスやコンテキストを触った瞬間に
例外を投げてプロセスごと落とします。対象の即時コンテキストを借りるノード（`ngol.gfx.capture_backbuffer` など）は
まさにその形なので、有効なままだと取り込みのたびにこのアプリが死にます。
逆に、ノードの側の不具合を追うときは有効にする価値があります。「黙って壊れる」が「その場で落ちて番地が残る」に変わります。

## ノードを書き換えて試す

配られた `.cs` はホットリロードの対象です。アプリを起動したまま書き換えると、そのまま反映されます。

```
build/dist/Nodes/CustomNodes/cs/code/DisasmNode.cs
```

ブリッジの配置先と違い、ここは `ext/` を挟みません。ホスト向けのノードが無いので、そのまま `cs/` の下に置かれます。
アプリのログに `Re-registered dynamic node` が出れば成功です。失敗しても古いコードが動き続けるので、変わらないときはまずログを見てください。
