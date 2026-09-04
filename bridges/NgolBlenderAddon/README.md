# Blender のアドオンとして NGOL を載せる

Blender が読みに来る利用者側の拡張フォルダへ置くと、NGOL が同じプロセスで起きる。
外せば元に戻る。

```
Blender (blender.exe)
 └── 拡張 ngol_for_blender  (Python)
       └── ctypes → hostfxr → CoreCLR          ← .NET が同じプロセスに載る
             └── NgolActivator.EntryPoint.Init(ngolRoot)
                   └── NGOL 一式（ノード・WebUI・解析拡張）
```

置かれるもの:

```
%APPDATA%\Blender Foundation\Blender\<版>\extensions\user_default\ngol_for_blender\
    blender_manifest.toml
    __init__.py / clr_host.py / mainthread.py / prefs.py
    ngol\                          NGOL 一式（Blender は走査しない）
        Nodes\CustomNodes\cs\blender\   このホスト向けのノード
        Nodes\CustomNodes\py\           bpy に触る処理の土台
        WebUI\plugins\                  制御パネルと console 転送
        Graphs\                         デモのグラフ
        Extensions\                     ネイティブ拡張を含む 3 種
```

---

## 何ができるか

**すべて実測。** 「届く」は、その操作を実際に行い、別の手段で結果を確かめたもの。

| やりたいこと | 届く手 | 実測 |
|---|---|---|
| Blender が生きているか | `blender.ping` | 版・PID・UI の有無。往復 0.3〜0.5ms |
| シーンの状態を読む | `blender.scene.stat` | 名前・ファイル・件数・種類別・現在フレーム |
| 物を並べる | `blender.object.spawn` / `.grid` | 輪・格子。Suzanne 12 体で 5.5ms |
| 動かす | `blender.object.move` | 回転・持ち上げ・拡大。接頭辞でまとめて |
| 片付ける | `blender.object.clear` | 71 個 ＋ マテリアル 71 を 176ms。取り残しは 0 |
| 任意の Python を走らせる | `blender.py.run` | メインスレッドで実行し、`result` を JSON で返す |
| 土台を入れ替える | `blender.py.reload` | Blender を止めずに `.py` を差し替える |
| 画面を撮る | `blender.capture` | PNG。UI が無いときは理由を返して断る |
| プロセスとバイナリを見る | `ngol.code.*` / `ngol.mem.*` / `ngol.hook.*` | blender.exe の中を直接。CPython の関数は 8/8 解決（2.25ms） |

WebUI には制御パネルが出る。`.cs` は 1 行も要らず、`.js` を置くだけで足りる。

**Blender のメインスレッドでしか触れないものと、そうでないものを分けてある。**
`bpy` を変える操作は `bpy.app.timers` を通り、それ以外は NGOL 側のスレッドで走る。
描画の最中に `bpy` を書き換えようとすると Blender が
`can't modify blend data in this state (drawing/rendering)` として断るため、
「速いから」で経路を選ばない。

---

## 中身

| | |
|---|---|
| `addon/ngol_for_blender/` | アドオン本体。**NGOL を起こしてメインスレッドを貸すだけ**で、Blender の機能は持たない |
| `nodes/` | このホスト向けの C# ノード。`_shared/` にブリッジの共通処理 |
| `data/ngol_blender.py` | `bpy` に触る処理の土台。ノードから関数名で呼ぶ |
| `webui-plugins/` | WebUI 拡張。制御パネルと、ブラウザの console を NGOL へ転送するもの |
| `graphs/` | デモのグラフ。押す順と書き換えどころは [`graphs/blender-demo/README.md`](graphs/blender-demo/README.md) |
| `scripts/` | 配置・パッケージ作成・検証 |

**機能はアドオンではなく NGOL 側に置いてある。** どちらもホットリロードで差し替わるので、
機能を足すのに Blender の再起動が要らない。

---

## 要るもの

| | 必須 | 用途 |
|---|---|---|
| Blender 4.2 以降 ・ win-x64 | ✅ | ホスト。実績は **4.2.0 / 5.0.0**（Python 3.11）と **5.2.0 LTS**（Python 3.13） |
| .NET 8 ランタイム (win-x64) | ✅ | NGOL 本体が .NET のため |
| NGOL 一式 | ✅ | 本体は submodule。`scripts\build.ps1` が組む |
| Node.js 18 以降 | — | MCP を使う場合のみ |

**SDK ではなくランタイムで足りる。** 管理者権限なしで入れるなら:

```powershell
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1 -UseBasicParsing
.\dotnet-install.ps1 -Channel 8.0 -Runtime dotnet -InstallDir "$env:LOCALAPPDATA\Microsoft\dotnet" -NoPath
```

`hostfxr.dll` は `DOTNET_ROOT` → `%ProgramFiles%\dotnet` → `%LOCALAPPDATA%\Microsoft\dotnet` の順に探すので、
どちらの入れ方でも見つかる。

---

## パッケージを作る

```powershell
./scripts/build.ps1
./bridges/NgolBlenderAddon/scripts/build_package.ps1 -NgolPortable build
./bridges/NgolBlenderAddon/scripts/build_package.ps1 -NgolPortable build -OutDir <出力先>
./bridges/NgolBlenderAddon/scripts/build_package.ps1 -NgolPortable build -PythonRuntime <Python.Runtime.dll>
```

空の作業フォルダで組むので、開発機のグラフ・ログ・スプールは混ざらない。
出来た zip を開いて、実行時生成物が無いことと必須ファイルがそろっていることまで確かめてから終わる。

実測: 200 件 / zip 7.6MB / 展開後 22.8MB。

`-PythonRuntime` を渡すと pythonnet のノードが入る。渡さなければそのノードは入らない
（参照が無いままだとコンパイルが 1 件失敗し、他の失敗が埋もれるため）。

pythonnet のノードは、結び付ける CPython を**そのプロセスに載っているものから決める**ので、
どの版の Blender でも指定は要らない（Blender 5.0 以下は Python 3.11、5.1 以降は 3.13）。

---

## 置き方

配布された zip から入れるなら、Blender へドラッグ&ドロップするか:

```powershell
blender --command extension install-file -r user_default -e ngol_for_blender-1.0.0.zip
```

このリポジトリから直接置くなら:

```powershell
./scripts/build.ps1
./bridges/NgolBlenderAddon/scripts/deploy.ps1 -NgolRuntime build\runtime
```

**NGOL 一式の場所だけは渡す。** Blender の側は何も渡さなければ自分で見つける。

| 引数 | |
|---|---|
| `-BlenderVersion 5.2` | 版フォルダを選ぶ。省略時は入っている Blender から決める（複数あれば最新） |
| `-BlenderExe <path>` | **携帯版**（zip を展開しただけのもの）はここで渡す。Windows に登録されないため自動では見つからない |
| `-NgolRuntime <path>` | 必須。`scripts\build.ps1` が組んだ `build\runtime` |
| `-NgolPortable <path>` | 配布版から入れるときはこちら（中に `runtime\` がある想定） |
| `-AddonOnly` | ノード・土台・WebUI 拡張だけ入れ替える（NGOL 一式は触らない） |
| `-Legacy` | 旧来の `scripts\addons\` へ置く |
| `-Force` | 配る先の版が起動中でも実行する |

置き場は `BLENDER_USER_EXTENSIONS` → `BLENDER_USER_RESOURCES` → `%APPDATA%` の順に決まる（Blender の決まりと同じ）。

---

## 使い方

1. Blender を起動
2. `Edit > Preferences` で **NGOL for Blender** を有効化
3. `View3D` のサイドバー（**N** キー）→ **NGOL** タブ、または設定画面で **NGOL を起動**
4. **WebUI を開く** でノードグラフの編集画面がブラウザに出る
5. WebUI の Plugins メニューから **Blender** パネルを開くと、そこから直接操作できる

| 設定 | 既定 | |
|---|---|---|
| Port | `11156` | 変更は次回の起動から効く |
| Blender 起動時に自動で起こす | 切 | 既定で自動起動しない |

動いているかはパネルの **起動中 (pid ... / port ...)** で見る。
**そこに出るのは設定値ではなく実際に開いた口**——使用中なら NGOL は次の番号を探し、
`host.log` へ `configured port 11156 is in use; listening on port 11157` と書く。
**WebUI を開く** も同じ番号を使うので、移っていても押せば正しい先が開く。

---

## 反復のコスト

| 変えたもの | やること | Blender の再起動 |
|---|---|---|
| C# ノード | 置くだけ | 要らない（1 秒未満で `Re-registered dynamic node`） |
| `.py` の土台 | 置いて `blender.py.reload` | 要らない |
| WebUI 拡張 | 置くだけ | 要らない。ブラウザの F5 すら要らない（MCP の `reload_webui`） |
| 拡張本体の `.py` | 置いて `script.reload()` | 要らない |
| NGOL 一式 | 置き換え | 要る |

ブリッジの往復は約 200ms（Blender の中での処理は 29ms）。
**繰り返しは境界の内側に閉じる。** 1000 回 `bpy` を触るなら、ノードから 1000 回呼ばずに
1 回の呼び出しの中でループする。

---

## 待ち受けについて

WebUI はループバック（`127.0.0.1` と `localhost`）でしか応答しない。外へは出ない。

`netstat` には `0.0.0.0:11156` と出るが、これは外部公開ではない。`HttpListener` が
`http.sys`（PID 4 = System）へ登録し、http.sys がポートを一括で持つための見え方で、
LAN の IP で叩くと **HTTP 400 Bad Request - Invalid Hostname** が返る（実測）。

---

## `-b`（UI なし実行）で使う

CI・バッチ・サーバー側の処理向け。

```powershell
blender.exe -b --python scripts\headless_serve.py
blender.exe -b work.blend --python scripts\headless_serve.py
```

環境変数 `NGOL_PORT`（既定 11167）/ `NGOL_SECONDS`（既定 600、0 で無制限）。

⚠ **`-b` では `bpy.app.timers` が回らない。** 実測: 0.05 秒間隔で登録して 3 秒待って発火 0 回。
登録は成功するのに発火しないので、放っておくと
「ノードは繋がるのに Blender 側が一切答えない」という原因の分かりにくい状態になる。

`-b` ではスクリプトがメインスレッドをブリッジに貸す。それが `headless_serve.py` の中身。
自前のスクリプトに組み込むなら:

```python
import bpy, sys
from _addon import resolve_module          # scripts/ にある。導入形式で名前が変わるため
MODULE = resolve_module()
bpy.ops.preferences.addon_enable(module=MODULE)
mod = sys.modules[MODULE]
mod.start_ngol(11167)
mod.mainthread.pump_forever(600)           # 戻ってこない。最後に置く
```

| `-b` で | |
|---|---|
| 出来る | ノードからの `bpy` 操作、NGOL の解析ノード、WebUI / MCP 接続 |
| 出来ない | `blender.capture`（ウィンドウが無い）。理由を返して断るので、黙って失敗はしない |

---

## MCP サーバーの設定（任意・手動）

AI エージェントから NGOL を操作したい場合だけ行う。**拡張の動作には要らない。**

この設定は自動では行わない。登録先はエージェント側の設定ファイルであって、
この拡張の持ち物ではない。ポートも配置先も環境ごとに違う。

MCP サーバーは配置される一式には入っていない。NGOL のソースから作る。

登録の仕方はエージェント側の CLI に依存する。以下は一例（Claude Code の場合）。

```powershell
cd <NGOL のソース>\mcp
npm ci
npm run build              # -> dist\index.js

claude mcp add ngol-blender --scope user -- `
  "<node.exe>" "<NGOL のソース>\mcp\dist\index.js"
```

| 環境変数 | |
|---|---|
| `NGOL_WS_URL` | `ws://127.0.0.1:11156/ws` |
| `NGOL_SCRIPTS_DIR` | `<ngolRoot>\Nodes\CustomNodes\cs` |
| `NGOL_DOCS_DIR` | `<NGOL のソース>\mcp\docs` |
| `NGOL_MAX_TOOL_CALLS` | 呼び出し上限（既定 100） |

⚠ **MCP クライアントは 15 秒で待つのをやめる。** それより長いノードは失敗に見えるが、
Blender の中では完走している。`run_node` に `async` を渡せば、出来上がりは
`check_job_status` から出力ごと受け取れる。

繋がらないときは `get_connection_info` の `Process` が Blender の PID と一致するかを見る。
別のプロセスに繋いでいれば、そこで食い違う。

開発用に `scripts\register_mcp.py` があるが、**配布物ではない**。

---

## 検証スクリプト

いずれも Blender を起こして走らせる。利用者が開いているセッションには触らない。

| | |
|---|---|
| `verify_startup.py` | 起動して待ち受けるところまでの検証 |
| `verify_survivable.py` | **NGOL を壊した状態で Blender が使えるか** |
| `verify_ui_cycle.py` | 利用経路を一巡（有効化 → 起動 → 停止 → 再起動 → `script.reload` → 無効化）。判定はポートが実際に応答するかで行う |
| `verify_background.py` | `-b` でタイマーが回らないことと、手動ポンプでブリッジが成立することを測る |
| `start_in_blender.py` | GUI で起こすだけ |
| `headless_serve.py` | `-b` の実運用入口 |
| `ngol_ws.py` | MCP を通さない生の WebSocket クライアント |
| `mcp_probe.py` | 登録せずに MCP サーバーを stdio で直接叩く |
| `_addon.py` | 導入形式でモジュール名が変わるので、列挙して見つける |

⚠ **エージェントがバックグラウンドで走らせたシェルから Blender を起こすと、セッション終了で道連れになる。**
残したいときは `Start-Process` で切り離す。

---

## 外し方

```powershell
blender --command extension remove user_default.ngol_for_blender
```

または置かれたフォルダを消す。Blender の設定も作った `.blend` も変わらない。
MCP を登録していれば別途外す（`claude mcp remove ngol-blender`）。

---

## ライセンス

| | |
|---|---|
| ソース | MIT（リポジトリの `LICENSE`） |
| 拡張パッケージとしての配布 | GPL-3.0-or-later（`blender_manifest.toml`） |

---

## 既知の注意点

| | |
|---|---|
| ⚠ **更新すると拡張のフォルダの中身は消える** | 自作ノード・WebUI 拡張・保存したグラフ・`kvstore.db` が対象。更新の前に控えを取る |
| ⚠ **CoreCLR はプロセスから降ろせない** | 無効化しても .NET は blender.exe に残る。止まるのは NGOL 側だけ。再度有効化すれば起こし直す |
| ⚠ **`bpy` はメインスレッド専用** | ノードから直接触らない。ブリッジを通す。pythonnet で C# から触る道もあるが、初期化をメインスレッドで行わないと Blender が固まる |
| Blender 起動中の上書き | 掴んだままなので中途半端に失敗する。ノードと `.py` と WebUI 拡張は `-AddonOnly -Force` で差し替えられる |
| 画面のキャプチャ | `blender.capture` と制御パネルのボタンは**利用者が操作するためのもの**。自動で撮らない |
| ポート衝突 | 既定は NGOL と同じ 11156。使用中なら NGOL が空きへ移るので、ブリッジごとに番号を分けていない。**移った先はパネルに出る** |
| 実行時生成物 | `host.log` / `kvstore.db` / `activator-error.log` / `blender_bridge\` は動かすと出来る。配布物には入らない |
| pythonnet の置き場 | 同梱するなら `nodes\lib\Python.Runtime.dll`。NGOL の規約（`ngolRoot` の 2 階層上の `extra-libs`）でも読むが、そこはパッケージの外なので導入場所を変えると解決先が変わる |
