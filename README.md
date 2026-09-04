# NGOL Extensions

**NGOL (NodeGraphModLab) を「動いているアプリを中から解析する道具」にするための拡張・ノード・ブリッジ サンプル集**

> [NGOL](https://github.com/ngol-org/NodeGraphModLab) 本体は、.NET ホストアプリケーションに組み込むノードグラフ実行環境です。このリポジトリはそれを**取り込んで使う側**でNGOLの拡張方法やノードの使い方等を示すサンプル集です。ネイティブ解析のための外部ライブラリとの連携やそれらを利用した解析を便利にする**ノード 70 本を `.cs` のソースのまま**掲載。またプラグイン機能を持つアプリ内にNGOLを起動するためのプラグイン例も4ホスト分をソースで掲載しています。

> **In English:** NGOL (NodeGraphModLab) is a node-graph runtime that runs inside a .NET host process. This repository is its analysis side: 70 nodes shipped as `.cs` source (55 of them run on the core alone) that read code, memory, hooks and the swap chain of the process NGOL lives in; three extension packages that add a disassembler (iced), managed detours (MonoMod) and native hooks (MinHook); four bridges that load the same runtime through each host's own plugin mechanism (OBS Studio, AviUtl ExEdit2, Blender, Paint.NET); and a small Direct3D target app for trying everything without a host. Nodes are recompiled on save without restarting the host and can be written and run by an AI agent over MCP, so a missing tool can be added while the target keeps running. Nodes and bridges are verified on Windows x64 only. The documentation is in Japanese.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

> **本リポジトリは、AI コーディングツールを使用して作成・検証しています。**  
解析ノードは複数のホストで検証していますが、導入環境によっては動作しなかったり想定の結果を返さないことがあります。.cs のソースのまま配られていますので、お使いの環境で動かないときは、AI コーディングエージェントにその場で直して利用していただけるのが、ホットリロード環境ですぐに試せるNGOLの長所だと考えています。エージェントに読ませる材料はリポジトリ内に揃っています（ノード一覧・拡張の作り方）。  
NGOL自体がベータ公開期間中のライブラリです、このサンプル集もご自身でも挙動を十分に検証したうえで、自己責任にてお取り扱いください。


> [!CAUTION]
> ここにあるノードは、**動いているプロセスのメモリを読み書きし、関数の呼び出しに割り込みます。** 対象を壊す・落とすことがあります。**自分が権利を持つソフトウェア、または明示的に許可を得たものに対してのみ使ってください。** オンライン接続を持つアプリケーションは対象にしないでください。

## このリポジトリで出来ること

**解析の鎖が 1 プロセスの中で閉じます。** 文字列を探し、それを参照する命令を見つけ、関数の先頭を割り出し、逆アセンブルし、フックを置き、呼んで、結果をメモリから読み戻す。この一連を対象のプロセスの中で、同じノード一式で辿れます。手で辿る手順は [題材アプリの README](samples/apps/D3DTargetApp/README.md) にあり、的の期待値がソースに書いてあるので答え合わせができます。

**足りない道具はその場で書けます。** ノードは数十行から数百行の C# で、`.cs` を保存するとホストを再起動せずに再コンパイルされます。MCP を通せば、AI エージェントがノードを書いて実行するところまで自分で回せます。対象を止めずに、調べながら道具を足していけます。

**入口が違っても中身は同じです。** C++ のプラグイン、Python のアドオン、.NET の型 1 つ、自前アプリの 1 行。4 通りの入口の先で動くのは同一の NGOL とノードです。ホストが増えても、書くものは入口の分しか増えません。本体は Windows のほか Linux と Android でも動作を確認しています（[本体の README](NodeGraphModLab/README.md)）。

**対象には手を入れません。** 対象のソース・設定・ディスク上のファイルは変えず、載せるのも外すのも配置だけです。題材アプリが NGOL に触れるのは `Bridge_Start()` の 1 行で、NGOL の型もヘッダーも持ちません。

**ノード自身が、どこまで信用してよいかを言います。** 走査を途中で打ち切ったか（`candidates_truncated`）、見た量と見るべき量（`scanned_mb` と `total_writable_mb`）、配ったものと実際に読まれたものの版（`ngol.proc.ext_info`）、提示関数に先客が居るか（`present_hook_chain`）、記録を取りこぼした件数（`lost_total`）、走査結果から自分自身を除いた数（`self_dropped`）。0 件が「無い」なのか「まだ見ていない」なのかを、出力だけで区別できます。

**止まった相手にも手があります。** 窓に触らずに全スレッドの居場所を取る `ngol.proc.thread_stacks`、ホスト側のスレッドが止まったときに NGOL の更新をノードから回す `ngol.dev.drive_update`、別の NGOL からノードを実行する `ngol.link.run_node`。応答の無いプロセスの中でも、道具を足しながら調べられます。

### 作法

- `ngol.hook.safety_check` を先に押す。DANGER と言われた番地にはフックを置かない。
- 見るだけの `watch_function` から始め、挙動を変える `skip_function` は後にする。
- `patch_bytes` は `patch_revert` と対で使う。戻す側を先に用意する。
- `call_fn0` / `call_fn1` の前に `disasm` で引数の個数と幅を確かめる。個数を間違えると例外は出ずにプロセスごと落ちる。
- 走査が 0 件なら、先に `scanned_mb` と `total_writable_mb` を見比べる。既定の走査量は対象全体より小さいことがある。
- オンライン接続を持つアプリケーションは対象にしない。

### 無いもの

- 疑似 C は出ません。出るのは逆アセンブルまでです。外部ライブラリと連携して出すノードを実装することも可能ですが、大きな関数を読み下す、型やデータ構造をまとめて起こすといった仕事は、それを専門にするツールのほうが速く確かです。動いているプロセスの中でしか分からないこと（実際に呼ばれた番地、引数、載っている版）をこちらで掴み、読み下しはそちらへ渡す。そういう使い分けがお勧めです。
- 対象の外からは読みません。同じプロセスに載ることが前提で、対象が落ちれば一緒に落ちます。ただし固まっただけなら、NGOL の側が動いているかぎり中から調べる手は残ります。
- フックは関数の先頭だけです。途中への介入やレジスタの書き換えはありません。
- ノードとブリッジは Windows x64 で確認したものだけです。

### 確かめ方

ノードの答えは、別プロセスから同じ番地を読んだ結果、ディスク上の PE、リンカ付属のダンプツール、別の逆アセンブラの参照ダンプと突き合わせて確かめています。題材アプリの的は期待値がソースにあるので、手元でも同じ突き合わせができます。

---

## リポジトリの構成

| | |
|---|---|
| [extensions/](extensions/) | NGOL の拡張パッケージ 3 種。ノードから使えるライブラリと capability を足す |
| [samples/nodes/](samples/nodes/) | 解析のためのノード 70 本（`.cs` のソースのまま配る）。**55 本は NGOL 本体だけで動く**。[一覧](samples/nodes/README.md) |
| [bridges/](bridges/) | ホストのプラグイン／アドオン／スクリプトとして NGOL を載せるブリッジ 4 本 |
| [samples/apps/](samples/apps/) | 対象アプリを用意しなくても一通り試せる題材アプリ |
| [native/](native/) | ネイティブフックのバックエンド（C++） |
| [NodeGraphModLab/](NodeGraphModLab/) | NGOL 本体（submodule） |

## 拡張パッケージ

拡張は「ライブラリを配る」ことと「何ができるか(capability)を名乗る」ことだけを行います。実際の処理はノードの側にあります。

**自分の拡張を作るなら** [docs/writing-an-extension.md](docs/writing-an-extension.md) を読んでください。
`extension.json` の全項目・`INgolExtension` の契約・読み込みの順序・最小の 4 ファイルが書いてあります。

| id | capability | 何が使えるようになるか | 使っているもの |
|---|---|---|---|
| `ngol.ext.code` | `code.disasm` / `code.xref` | x86/x64 の逆アセンブル、参照元の探索 | [iced](https://github.com/icedland/iced) |
| `ngol.ext.il` | `managed.detour` / `il.inspect` | マネージドメソッドへの割り込み、.NET アセンブリの読み取り | [MonoMod](https://github.com/MonoMod/MonoMod)（Mono.Cecil） |
| `ngol.ext.native-hook` | `native.hook` | ネイティブ関数のフック・呼び出しの記録・差し替え | [MinHook](https://github.com/TsudaKageyu/minhook)（`native/` 経由） |

## ノード

`samples/nodes/` のノードの多くは、ホストのエンジンにも OS の GUI にも依存しない書き方をしています。導入先で `.cs` のままコンパイルされるので、手元で書き換えて挙動を変えられます。

メモリの読み書きや逆アセンブルだけでなく、**解析の途中で要るもの**も揃えてあります——D3D のバックバッファや提示関数へ届く手段、対象の窓やダイアログの扱い、NGOL 自身の挙動を測るもの。

**70 本のうち 55 本は拡張なしで動きます。** ID と 1 行説明・どれがどの拡張を要るかは
[samples/nodes/README.md](samples/nodes/README.md) にあります。

| 領域 | 例 |
|---|---|
| `ngol.code.*` | `disasm` / `disasm_scan` / `aob_scan` / `find_string` / `xref_find` / `module_list` / `pe_info` |
| `ngol.mem.*` | `read_bytes` / `read_value` / `write` / `region_probe` / `value_scan` / `pointer_path` |
| `ngol.hook.*` | `watch_function` / `trace_calls` / `skip_function` / `patch_bytes` / `safety_check` |
| `ngol.il.*` | `assembly_inspect` / `assembly_surface` |
| `ngol.gfx.*` | `capture_backbuffer` / `present_address` |
| `ngol.win32.*` | `window_close` / `menu_command` / `set_control_text` / `modal_state` |

## ブリッジ

それぞれのアプリのプラグイン、アドオン、またはスクリプトとして NGOL を同じプロセスで起こします。置くのはホストが読みに来るフォルダの中だけで、外せば元に戻ります。

| ブリッジ | ホスト | ホスト側の言語 |
|---|---|---|
| [bridges/NgolObsPlugin/](bridges/NgolObsPlugin/) | OBS Studio 32.x | C++ |
| [bridges/NgolAviUtl2Plugin/](bridges/NgolAviUtl2Plugin/) | AviUtl ExEdit2 | C++ |
| [bridges/NgolBlenderAddon/](bridges/NgolBlenderAddon/) | Blender 4.2 以降 | Python |
| [bridges/NgolPaintDotNetPlugin/](bridges/NgolPaintDotNetPlugin/) | Paint.NET 5.1 | 無し（ホストが最初から .NET） |

**ブリッジが受け持つのは起動だけです。** 運ぶもの（`build/runtime`）は 4 本とも同じで、
違うのは「ホストが自分をどう読むか」の 1 点。プラグインやアドオンを読む仕組みを持つアプリなら、
**入口を 1 つ書けば、同じ NGOL とノードがそのまま動きます。**

何をするかはブリッジではなくノードとグラフが決めるので、ホストが増えても書くものは増えません。
**ホストが C/C++ を求めないなら、コンパイラも要りません** — Blender のものは Python だけ
（`ctypes` から hostfxr を呼ぶ）で、C++ が 1 行もなく、ビルドも要りません。

足すときの手順は [bridges/README.md](bridges/README.md) にあります。

---

## はじめかた

### 1. 取ってくる

本体は submodule なので `--recursive` が要ります。付け忘れた場合は 2 行目で足せます。

```bash
git clone --recursive https://github.com/ngol-org/NGOL-Extensions.git
git submodule update --init --recursive
```

### 2. 組み立てる

ソースだけを配っているので、NGOL 本体・WebUI・拡張・ネイティブを 1 回のスクリプトでまとめてビルドします。

```powershell
./scripts/build.ps1
# -> build/runtime/ に、NGOL が実行時に読むもの一式ができる
```

C++ のツールチェーンが無い機械では `-SkipNative` を付けてください。ネイティブフックの拡張は、ネイティブ側が無い状態で読み込まれます。

```powershell
./scripts/build.ps1 -SkipNative
```

### 3. 載せる

組み立てたものをブリッジへ渡します。ブリッジ側のホストプラグイン本体（C++）のビルドは、各ブリッジの `README.md` にあります。

```powershell
./bridges/NgolObsPlugin/scripts/deploy.ps1 `
     -PluginBinary <NgolForObs.dll> -NgolRuntime build/runtime
```

ホストを起動すると NGOL が同じプロセスで立ち上がります。ブラウザで `http://localhost:11156/` を開くとノードグラフのエディタが出ます（ポートはブリッジごとに変えられます）。

### 対象アプリを用意せずに試す

[samples/apps/D3DTargetApp/](samples/apps/D3DTargetApp/) は、この一式を試すためだけの小さなアプリです。Direct3D で描画し、逆アセンブルやフックの的になる関数を意図して置いてあります。**上のブリッジとは逆で、こちらは自分のアプリへ最初から載せる形の例です。**アプリが NGOL に触れるのは `Bridge_Start()` を呼ぶ 1 行だけで、NGOL の型もヘッダーも持ちません。

```powershell
./scripts/build.ps1                            # 先にこちら
./samples/apps/D3DTargetApp/prepare-dist.ps1
```

⚠ **順番が要ります。** 後者が作るのはアプリとブリッジだけで、NGOL 本体・WebUI・ネイティブフックの
バックエンドは前者が作ります。飛ばすと、出来上がったものは起動こそしますが、
ブラウザに何も出ず、フック系のノードが「拡張が読み込まれていない」と答えます。

出来上がったフォルダの `.exe` を起動すれば、逆アセンブル・メモリ走査・フック・画面の取り込みまで、ノードを実際に動かして確かめられます。 押す順と期待値は [題材アプリの README](samples/apps/D3DTargetApp/README.md) にあります。

---

## 必要なもの

| | |
|---|---|
| .NET SDK 8.0 以降 | 本体・拡張・ノードのビルド |
| Node.js | WebUI のビルド |
| CMake ＋ MSVC | ネイティブ（省くこともできる） |
| PowerShell | 付属のスクリプト。Windows 同梱の 5.1 でも 7 でも動きます |

現在ビルド・動作を確認しているのは Windows x64 です。

## ライセンス

MIT。同梱・再配布している第三者コードについては [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) を参照してください。

Blender のブリッジを**拡張パッケージとして組んだもの**だけは GPL-3.0-or-later です
（[bridges/NgolBlenderAddon/](bridges/NgolBlenderAddon/)）。
