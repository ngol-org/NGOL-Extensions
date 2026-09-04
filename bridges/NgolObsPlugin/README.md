# OBS Studio のプラグインとして NGOL を載せる

ホストが読みに来るフォルダへ `.dll` を 1 つ置くと、NGOL が同じプロセスで起動する。
外せば元に戻る。

```
%ProgramData%\obs-studio\plugins\NgolForObs\
    bin\64bit\
        NgolForObs.dll        <- ホストが読むのはこれだけ
    data\locale\en-US.ini
    ngol\                     <- NGOL 一式（ホストは走査しない）
        Nodes\CustomNodes\cs\ <- このホスト向けのノード ＋ ext\（拡張用の汎用サンプル）
        Extensions\           <- ネイティブ拡張を含む 3 種
        WebUI\ / ngol-config.json
```

---

## 何ができるか

**すべて実測。**「届く」は、その操作を実際に行い、別の手段で結果を確かめたもの。

| 人間の操作 | 届く手 | 実測 |
|---|---|---|
| ホストの状態を読む | `obs.info` | 版・出力の大きさ・実際の fps・配信/録画の状態・出ているシーン |
| シーンを切り替える | `obs.scene.set` | 切り替わり、読み戻しで一致 |
| シーンの一覧 | `obs.scene.list` | 名前と大きさ、出ているシーン。日本語の名前もそのまま通る |
| 中身を並べる | `obs.sceneitem.list` | 表示・位置・拡大率・回転まで |
| 置いたものを動かす | `obs.sceneitem.set` | 位置と拡大率を書き換え。**渡さなかった項目は触らない** |
| ソースを足す・外す | `obs.source.edit` | 色ソースとテキストを追加。スロット番号が返る |
| 作れる種類を調べる | `obs.source.list`（`types`） | id と表示名が並ぶ。推測せず引ける（数は入れているプラグインで変わる） |
| ソースの設定を読む・書く | `obs.source.settings` | 文面と色を書き換え、読み戻しで一致 |
| 音量・ミュート | `obs.source.audio` | 0 dB -> -6 dB -> 0 dB を往復 |
| フィルタを足す・外す・入り切り | `obs.source.filter` | 一覧と並び順まで返る |
| 録画・リプレイ・仮想カメラ | `obs.control` | ⚠ **配信の開始だけは既定で断る**（後述） |
| 起きたことを受け取る | `obs.events` | シーン変更・録画開始/停止が並ぶ |
| 描かれた絵を取り出す | `obs.capture` | 1920x1080 をそのまま受け取り、縮めて PNG |

### ⚠ 配信の開始は既定で通さない

`obs.control` は `start_streaming` を、`allow_streaming` を明示的に立てない限りホストへ渡さない。

**他の操作と違って、結果が外へ出ていき取り消せない。**
送信先が設定されていなければ実害は無いが、それは設定に依存する話なので、
ノードの側では設定を当てにしない。停止（`stop_streaming`）は制限しない。

### 描かれた絵を取り出す

**ホストは頼まれれば描画結果の画素をそのまま渡してくる。** 画面を撮る必要は無い。

| | 画面を撮る | この道 |
|---|---|---|
| 大きさ | プレビューの縮小サイズ | **出力する大きさそのまま**（実測 1920x1080） |
| 写り込み | ⚠ 一覧・題・他の窓まで入る | **絵だけ** |
| 他の窓の重なり | ⚠ 影響する | 無関係 |

⚠ **出力の大きさのままだと、確かめるには大きすぎる。**
`max_width` で縮める（既定 640）。⭐ **縮めるときは間引かずに平均を取る**——
間引くと細い線が消えて文字が読めなくなり、「小さくても何が描かれたか分かる」という目的を外す。

シーンだけでなく**個々のソースも名前で描かせられる**ので、
画面取り込みを含まないソースだけを選んで確かめることもできる。

---

## ホストの UI に増えるもの

ノードとは別に、**ソース 1 種とフィルタ 2 種**がホストの一覧に加わる。
ノードもグラフも要らず、いつもの「ソースを追加」「フィルタを追加」から使う。

| 表示名 | 種別 | 何をするか | 設定項目 |
|---|---|---|---|
| **NGOL シェーダー**（NGOL Shader） | フィルタ | 計算式を文字列で受け取り、毎フレーム GPU で下地に掛ける | 計算式・`p1`〜`p4` |
| **NGOL ガラス割れ**（NGOL Glass Break） | フィルタ | 下地を破片に割って飛ばす | 周期・保持・割れる本数・輪の数・飛び方・回り方・落ち方 |
| **NGOL 共有フレーム**（NGOL Shared Frame） | ソース | 別のプロセスが置いた 1 枚をそのまま映す | 共有領域の名前 |

表示名はホストの言語で変わる（`data/locale/`）。上は日本語と英語。

シェーダーのフィルタは [graphs/](graphs/) の見本 4 本が `obs.source.filter` から操る。
書き方（`float4 render(float2 uv)`・使える名前・通らなかったときの見方）は
[graphs/README.md](graphs/README.md) にある。

共有フレームのソースが読む 1 枚は、NGOL 側のノードが書く。
別のアプリで作った絵をファイルにせず直接渡したいときに使う。

---

## 中身

| ファイル | |
|---|---|
| `plugin/plugin.cpp` | ホストの型を知っている唯一の場所。入口・メニュー・通知の受け取り・NGOL へ渡す口 |
| `plugin/ObsOps.cpp` | 操作の実装。要求 1 件を受けて答えを組む |
| `plugin/NgolBridge.*` | .NET を起こす。**ホストの型を持ち込まない** |
| `plugin/ObsShaderFilter.*` ・ `ObsGlassFilter.*` ・ `ObsFrameSource.*` | ホストの UI に増える 3 種 |
| `nodes/` | このホスト向けのノード |
| `graphs/` | 計算式を渡してフィルタを操る見本 4 本 |
| `scripts/deploy.ps1` | 配置する道具（**開発用。配布物ではない**） |

### NGOL へ渡す口は 3 つだけ

```c
int Ngol_Obs_Call(const char* requestJson, char* outUtf8, int outLen);
int Ngol_Obs_TakeResult(char* outUtf8, int outLen);
int Ngol_Obs_TakeFrame(unsigned char* out, int outLen, int* w, int* h, int* pitch);
```

**操作ごとに export を生やさない。** 要求も答えも JSON にして `op` で分ける。

* **JSON の読み書きはホスト自身が持っている**（`obs_data`）ので、外部ライブラリが要らない
* 操作を増やしても**この 3 つの宣言が動かない**ので、C# 側を作り直さなくてよい

⚠ **答えが入りきらなかったときに op を走らせ直さない。** 控えを `Ngol_Obs_TakeResult` で引き取る。
2 度目のシーン切り替えや録画開始は、頼まれていない操作になる。

### スレッド

NGOL は自前のスレッドで動く。ホストの前面 API は UI スレッドのものなので、
**要求 1 件をまるごと UI スレッドへ渡して終わるまで待つ**（`obs_queue_task`）。
自分が既に UI スレッドに居るかは `obs_in_task_thread` で分ける。

⚠ **絵の取り出しだけは UI スレッドへ渡さない。** 描画スレッドの錠を取るので、
UI スレッドを止めたまま待つ形を作らない。

---

## ビルド

### 用意するもの

| | |
|---|---|
| Visual Studio 2022（C++） | C++20 を使う |
| CMake 3.22 以上 | VS に同梱のもので足りる |
| **ホストのソース** | ヘッダーが配布版に入っていない。**置いてある版と同じタグ**を取る |
| .NET SDK（win-x64） | `libnethost.lib` を使う。入っていれば自動で見つける |

```
git clone --depth 1 --branch <置いてある版> https://github.com/obsproject/obs-studio.git <取得先>

cmake -S plugin -B <ビルド先> -DOBS_SOURCE_DIR=<取得先> -DOBS_BINARY_DIR="<ホスト>\bin\64bit"
cmake --build <ビルド先> --config Release
```

⚠ **ソースの版を置いてある版と揃える。** ずれるとモジュールの版が合わず読み込まれない。

### インポートライブラリは自分で作る

配布版に `.lib` は入っていない。**置いてある DLL のエクスポート表から作る**
（`dumpbin -exports` -> `.def` -> `lib -def:`）。CMake が構成時に自動で行うので、
手で用意するものは無い。ホスト側は C のエクスポートなので名前の装飾が無く素直に通る。

`obs-config.h` が読む `obsconfig.h` はビルド構成で作られるものでソースに入っていない。
Windows で意味を持つのは 2 つだけなので CMake が生成する。

---

## 置き方

```
./scripts/build.ps1
./bridges/NgolObsPlugin/scripts/deploy.ps1 `
     -PluginBinary <ビルド先>\Release\NgolForObs.dll -NgolRuntime build\runtime
```

⚠ **ホストを終了してから行う。** 起動中は自分のプラグインを掴んだままなので、
上書きが途中で失敗し、古くも新しくもないフォルダが残る。

ノードの `.cs` だけを差し替えるなら `-NodesOnly` を付ければホストは起動したままでよい
（ホットリロードで拾われる）。

### ⚠ 置き場所は `%ProgramData%` であって `%APPDATA%` ではない

導入記事には `%APPDATA%\obs-studio\plugins\` と書くものがあるが、**それは古い版の話**。
いまのホストは `CSIDL_COMMON_APPDATA`（= `%ProgramData%`）を見る。

⚠ **間違えても何も起こらないので気づきにくい。** 走査すらされないため、
モジュール一覧に載らず、ログにも「読み込みに失敗した」とすら出ない。

### 動いているかの確かめ方

ホストのログ（`%APPDATA%\obs-studio\logs\`）に、起動のたびに記録が残る。

```
[NgolForObs] runtime folder: ...\NgolForObs\ngol
[NgolForObs] started; open the node graph from the Tools menu
```

ホストの Tools メニューに **NGOL Node Graph** が増える。押すと編集画面が開く。

**待ち受け先はここに聞かない。** 押した時点で NGOL に聞いた先が開くので、
設定したポートが使用中で NGOL が別の空きへ移っていても、開く先はそちらになる。
番号そのものが要るときは `ngol\host.log` に出ている
（`[Node Graph Mod Lab] Graph Editor: http://localhost:<ポート>`）。

---

## 反復のコスト

| 変えたもの | 必要な操作 |
|---|---|
| ノードの `.cs` | ホットリロードのみ（数秒） |
| `plugin.cpp` / `ObsOps.cpp` | ホスト終了 -> ビルド -> 配置 -> 起動 |
| `ngol-config.json` | ホストの再起動 |

---

## 外し方

`%ProgramData%\obs-studio\plugins\NgolForObs\` を消す。
ホストの設定も、作ったシーンも変わらない。
