# NGOL x Blender デモ（`blender-demo`）

**このブリッジのノードを一通り押して、効いたことを画面で確かめるグラフ。**
34 ノード・6 グループ。上から順に押していけば、このブリッジで何ができるかが一巡する。

`scripts/deploy.ps1` が `Graphs\ngol-blender-demo.json` として配る。
WebUI の Load Graph から開く。

## このグラフの決まり

**操作ノードには値を持たせていない。値はすべて左端の入力値ノードに置いてある。**

線が来ていないポートは「ノードの既定値」、線が来ているポートは「誰かが決めた値」。
どちらなのかが、キャンバスを見るだけで区別できる。

## 6 つのグループ

| グループ | 押すと何が起きるか | 使うノード |
|---|---|---|
| **0. 場を片付ける** | 接頭辞が一致する物と、使われなくなったメッシュ・材質を消して数え、画面を PNG に | `blender.object.clear` / `scene.stat` / `capture` |
| **1. 作って目で確かめる** | 疎通を見てからリング状に並べ、件数を数え、画面を PNG に | `blender.ping` / `object.spawn` / `scene.stat` / `capture` |
| **2. 動かして読み戻す** | 押すたびに回って上がる。位置を平らな行で返す | `blender.object.move` / `object.list` |
| **3. 格子に並べる** | 行と列を指定して並べ、位置で色を付ける | `blender.object.grid` |
| **4. Python を直接書く** | 文面を書き換えて実行すると、その場でホストに効く | `blender.py.run` / `py.reload` |
| **5. 実行ファイルそのものを見る** | 載っているモジュールと、その公開関数の番地を引く | `ngol.code.module_list` / `export_address` |

グループ 5 だけノードの出どころが違う。**ホストに合わせて書いたものではなく、
どのホストでも使える解析ノードをこの相手に当てたもの。**

## 最初に押すもの

**グループ 0 から押す。** 前回の実行で置いたものが残っていると、数が合わなくなる。

⚠ `prefix` を空にすると `blender.object.clear` は断る（全消しはしない）。
消えるのは `NGOL` / `GRID` / `CONE` / `PY` で始まる物だけで、
既定のカメラ・ライト・キューブは残る。

## 書き換えて遊ぶところ

| 入力値ノード | 効く先 |
|---|---|
| `v-shape` / `v-count` / `v-radius` / `v-wave` | グループ 1 の並べ方。形・個数・半径・上下の揺れ |
| `v-spin` / `v-dz` | グループ 2 の 1 回あたりの回転角と持ち上げ量 |
| `v-grid-cols` / `v-grid-rows` / `v-grid-h` | グループ 3 の格子の大きさ |
| `e1-code`（Text Box） | グループ 4 で走る Python。下記のものが最初から使える |
| `v-mod` / `v-exports` | グループ 5 で調べるモジュールと関数名 |

グループ 4 の Text Box では、`bpy` / `bmesh` / `math` / `mathutils` と
`Vector` / `Matrix` / `Euler` / `Quaternion` / `Color`、`json` / `os` を import なしで使える。
`result` に入れた値が戻る（`out` へ書いても戻る）。

`blender.capture` が書く PNG は `<ngolRoot>\blender_bridge\out\` に入る。
書いた先は `path` ポートに出る。UI の無い状態（`-b`）では撮れないので、理由を返して断る。

## 土台の Python を直したとき

ノードの中身は `data/ngol_blender.py` にある。直したらグループ 4 の
`blender.py.reload` を押す。Blender を再起動しなくても入れ替わる。
