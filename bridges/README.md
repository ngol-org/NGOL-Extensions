# bridges/ — ホストのプロセスへ NGOL を載せるブリッジ

NGOL は対象アプリと同じプロセスの中で動きます。そこへ**どうやって載せるか**だけを受け持つのが、このディレクトリのブリッジです。

ここに置いてあるものは、どれもホストのプラグイン、またはアドオンとして読み込まれます。ホストが読みに来るフォルダへ置くだけで、外せば元に戻ります。

**載せたあとは、どのブリッジでも同じ NGOL が動きます。** 運ぶもの（`runtime/`）は 1 つで、違うのは入口だけです。だからブリッジ側にホストの機能は置きません。何をするかはノードとグラフが決めます。

## 何が入っているか

| ブリッジ | ホスト | ホスト側の言語 |
|---|---|---|
| [NgolObsPlugin/](NgolObsPlugin/) | OBS Studio 32.x | C++ |
| [NgolAviUtl2Plugin/](NgolAviUtl2Plugin/) | AviUtl ExEdit2 | C++ |
| [NgolBlenderAddon/](NgolBlenderAddon/) | Blender 4.2 以降 | Python（`ctypes` から hostfxr を呼ぶので C++ が要らない） |
| [NgolPaintDotNetPlugin/](NgolPaintDotNetPlugin/) | Paint.NET 5.1 | 無し（ホストが最初から .NET） |

**入口の言語は違っても、運ぶものは同じです。** ホストがネイティブのプラグインしか読まないなら
C++ で、スクリプトを読むならその言語で、ホストが最初から .NET ならホストが構築する型 1 つで、
入口を 1 つ書くだけ。その先の NGOL とノードは 4 本とも同一のものです。

加えて、3 本が共有するものが 1 つあります。

| | 何をするか |
|---|---|
| [NgolActivator/](NgolActivator/) | ネイティブから .NET へ入るエントリポイント。hostfxr が `NgolActivator.EntryPoint` の `Init` を呼び、そこから NGOL 本体が起きる。起きたあとは、同じ入口の `GetServerPort` で**実際に待ち受けているポート**を聞けます（待ち受けていなければ 0）。**どのブリッジもソースを参照せず、ファイル名で呼びます** |

## 置くまでの手順

運ぶものはリポジトリの外から降ってきません。**先に組み立ててから、ブリッジへ渡します。**

```powershell
# 1. NGOL 一式を組む（既定の出力先は build/runtime）
./scripts/build.ps1

# 2. ブリッジへ渡す
./bridges/NgolObsPlugin/scripts/deploy.ps1 -PluginBinary <NgolForObs.dll> -NgolRuntime build/runtime
```

`-NgolRuntime` は省略できません。何を配ったかが分からないまま置かれるのを避けるためです。

各ブリッジの詳細（何ができるか・置き方・外し方・ホスト側のビルド）は、それぞれの `README.md` にあります。

## 新しいブリッジを足すとき

1. **対象アプリがプラグインやアドオンをどう読むか調べます。** ブリッジはその仕組みの上に載ります
2. 構成は既存に揃えます — `README.md` ／ ホスト側の本体 ／ `nodes/` ／ `scripts/deploy.ps1`。
   ホストへ渡す資材があれば `data/`、ノードの使い方を見せるグラフがあれば `graphs/`
3. ホストに触る処理はブリッジではなくノード側へ置きます。ブリッジが受け持つのは起動だけです
4. `runtime/` は自分で組まず、`scripts/build.ps1` が組んだものを受け取ります
