# 拡張を書く

NGOL 本体（[NodeGraphModLab](https://github.com/ngol-org/NodeGraphModLab)）は、拡張を足す口を 1 つだけ持っています。このリポジトリの [extensions/](../extensions/) にある 3 つは、その口の使い方を 3 通りに分けた実例です。

ここでは**自分の拡張を 1 つ作るまで**を説明します。書いてあることはすべて本体の実装（`NodeGraphModLab.Core/Extensions/ExtensionHost.cs` と `NodeGraphModLab.NodeAPI/IExtensionContext.cs`）から取っています。

---

## 拡張は何をするものか

拡張が本体へ足せるものは 4 つです。**どれか 1 つだけでも拡張として成立します。**

| 足せるもの | どうやって | このリポジトリの例 |
|---|---|---|
| **ライブラリ** | `lib/<tfm>/` へ `.dll` を置く。ノードの `.cs` からそのまま `using` できるようになる | `ngol.ext.code` が逆アセンブラを配る |
| **capability** | `RegisterCapability("code.disasm")` | 3 つとも |
| **サービス** | `RegisterService(typeof(IFoo), impl, scope)`。ノードが `ctx.GetExtensionService<IFoo>()` で受け取る | `ngol.ext.native-hook` が `INativeHookService` を出す |
| **ノード（DLL）** | `extension.json` の `nodes` で宣言し、その下の `.dll` から登録する | 使っていない（このリポジトリのノードは `.cs` のまま配っている） |

⭐ **一番小さい拡張は「ライブラリを配って capability を名乗るだけ」です。** `ngol.ext.code` がそれで、実装は 30 行です。

---

## 置かれる形

本体は起動時に `<ngolRoot>/Extensions/` の直下を 1 段だけ走査し、`extension.json` を持つフォルダを拡張として読みます。

```
<ngolRoot>/
  NodeGraphModLab.Core.dll        本体
  Nodes/ WebUI/ ngol-config.json
  Extensions/
    ngol.ext.code/                <- フォルダ名は何でもよい。id は extension.json が決める
      extension.json
      NgolExt.Code.Impl.dll       <- entryAssembly
      lib/
        net6.0/
          Iced.dll                <- ここに置いたものがノードから using できる
```

---

## `extension.json`

本体が読む項目はこれで全部です。

```json
{
  "id": "example.ext.hello",
  "version": "1.0.0",
  "apiVersion": 1,
  "enabled": true,
  "entryAssembly": "Example.Hello.Impl.dll",
  "entryType": "Example.Hello.HelloExtension",
  "capabilities": ["hello.greet"],
  "platforms": ["win-x64"],
  "libraries": {
    "preload": true,
    "aliases": { "SomeLib.Iced": "somelib_iced" }
  },
  "nodes": { "mode": "dll", "directory": "nodes" }
}
```

| 項目 | 必須 | 意味 |
|---|---|---|
| `id` | 必須 | 空だと読み込みを断られる。ログと capability の出どころに使われる |
| `apiVersion` | 必須 | **`1` 以外は読み込まれない**（合わなければ理由を残して降りる） |
| `entryAssembly` / `entryType` | 必須 | どちらか欠けると断られる。型は `INgolExtension` を実装していること |
| `version` | 任意 | ログと `ngol.proc.ext_info` の表示に出る |
| `enabled` | 任意（既定 true） | `false` にすると読まずに飛ばす |
| `capabilities` | 任意 | 名乗るだけ。本体は中身を検査しない |
| `platforms` | 任意 | 空なら全プラットフォーム。合わなければ**読みに行かずに降りる** |
| `libraries.preload` | 任意（既定 true） | `lib/<tfm>/` の `.dll` を先に読み込む |
| `libraries.aliases` | 任意 | 下記「別名で隔離する」 |
| `nodes.mode` / `nodes.directory` | 任意 | `mode` が `dll` のときだけ、`directory` の `.dll` からノードを登録する |

JSON はコメントと末尾カンマを許容します。

⚠ **`lib/<tfm>` の `<tfm>` は本体が決めます。** CoreCLR で動いていれば `net6.0`、.NET Framework なら `net462`。**この 2 つ以外のフォルダ名は見られません。** ライブラリ自体は `netstandard2.0` でビルドしてよく、置き場所のフォルダ名だけがこの規約に従います。

---

## エントリポイント

```csharp
using NodeGraphModLab.NodeAPI;

namespace Example.Hello;

public sealed class HelloExtension : INgolExtension
{
    public void Load(IExtensionContext context)
    {
        context.RegisterCapability("hello.greet", "1.0.0");
        context.Logger.LogDebug("[hello] loaded");
    }

    public void Unload(IExtensionContext context)
    {
        context.Logger.LogInfo("[hello] unloaded");
    }
}
```

`IExtensionContext` が渡してくるものは次の通りです。

| | |
|---|---|
| `ExtensionId` / `NgolRoot` / `ExtensionDirectory` | 自分の id と、置かれている場所 |
| `Logger` | 本体のログへ書く |
| `RegisterNodes(Assembly)` | 任意のアセンブリからノードを登録する |
| `AddAssemblyResolvePath(string)` | 追加の解決先。`lib/<tfm>` と自分のフォルダは本体が既に足している |
| `RegisterService(Type, object, ExtensionServiceScope)` | ノードから受け取れるようにする。scope は `Extension` か `Singleton` |
| `RegisterPersistentTick(IExtensionPersistentWork)` | 毎フレームの `OnUpdate` と停止時の `OnStop` を受け取る |
| `RegisterCapability(string, string?)` | 名乗る |

⚠ **`Unload` は後始末の唯一の機会です。** ネイティブの資源やフックを持ったなら、ここで必ず外してください。`ngol.ext.native-hook` は `Unload` で全フックを撤去しています。

---

## 読み込みの順序

本体が拡張 1 件を読む順序です。**どこで降りるかが分かると、動かないときの切り分けが速くなります。**

1. `extension.json` を読む（無い・壊れている・`id` が空 → 降りる）
2. `enabled` が false → 降りる
3. `apiVersion` が 1 でない → 降りる
4. `platforms` が今の環境を含まない → 降りる
5. `libraries.aliases` を適用する（**ライブラリを読む前**。読んだ後では既に走ったコンパイルに効かない）
6. `lib/<tfm>/` の `.dll` を先に読む
7. 解決先に `lib/<tfm>` と拡張フォルダを足す
8. `entryAssembly` を読み、`entryType` を作る（無い・`INgolExtension` でない → 降りる）
9. `Load(context)` を呼ぶ
10. `nodes` の宣言があればノード DLL を登録する
11. `capabilities` を登録する

⚠ **降りた理由はログに 1 行出ますが、失敗ではないので止まりません。** 拡張が効いていないときは、まず本体のログでこの 11 段のどこまで進んだかを見てください。

---

## ノードから使う

サービスは型で受け取ります。**無いときは `null` が返るので、それを前提に書きます。**

```csharp
public void Execute(IExecutionContext ctx)
{
    var svc = ctx.GetExtensionService<IHelloService>();
    if (svc == null)
    {
        ctx.SetPortValue("result", "hello extension not loaded");
        return;
    }
    ctx.SetPortValue("result", svc.Greet("world"));
}
```

⚠ **`null` のときの文言は「拡張が読み込まれていない」だけにしないでください。** 拡張は読み込まれていて、その拡張が要るネイティブライブラリだけが欠けている、という状態が実際に起きます。そのとき利用者は間違った場所を探すことになります。

ライブラリのほうは登録も何も要りません。`lib/<tfm>/` に置いた時点で、ノードの `.cs` から `using` できます。

---

## 最小の拡張を作る

ファイルは 4 つです。

```
extensions/Example.Hello/
  Example.Hello.Impl/
    Example.Hello.Impl.csproj
    HelloExtension.cs
    extension.json
  pack-hello-extension.ps1
```

### 1. csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Deterministic>true</Deterministic>
    <PathMap>$(MSBuildThisFileDirectory)=/_/</PathMap>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\NodeGraphModLab\NodeGraphModLab.NodeAPI\NodeGraphModLab.NodeAPI.csproj" />
  </ItemGroup>
</Project>
```

⚠ `Deterministic` と `PathMap` を最初から入れてください。無いと**ビルドした機械のディレクトリ構成が `.dll` に焼き込まれて配られます。** ソースの差分には現れないので、後から気づく手立てがありません。

`netstandard2.0` にしておくと、CoreCLR のホストでも .NET Framework のホストでも同じものが読めます。

⚠ **`LangVersion` を省かないでください。** `netstandard2.0` の既定は C# 7.3 で、上のエントリが使っているファイルスコープの名前空間はそこでは通りません（`error CS8370`）。このリポジトリの 3 つとも `latest` を指定しています。

### 2. エントリ（上の `HelloExtension.cs`）

### 3. `extension.json`（上のとおり）

### 4. pack スクリプト

`scripts/build.ps1` は `extensions/` の下を再帰的に走査し、**`pack-*.ps1` という名前のファイルを見つけて `-DistRoot <出力先>` を付けて呼びます。** 名前を列挙していないので、拡張を足しても `build.ps1` を触る必要はありません。

```powershell
param(
    [Parameter(Mandatory = $true)][string]$DistRoot,
    [string]$Configuration = "Release",
    [ValidateSet("net6.0", "net462")][string]$Tfm = "net6.0"
)
$ErrorActionPreference = "Stop"
$ExtRoot = $PSScriptRoot

dotnet build (Join-Path $ExtRoot "Example.Hello.Impl\Example.Hello.Impl.csproj") -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$implOut = Join-Path $ExtRoot "Example.Hello.Impl\bin\$Configuration\netstandard2.0"
$extDir  = Join-Path $DistRoot "Extensions\example.ext.hello"
New-Item -ItemType Directory -Path $extDir -Force | Out-Null

Copy-Item (Join-Path $implOut "Example.Hello.Impl.dll") $extDir -Force
Copy-Item (Join-Path $ExtRoot "Example.Hello.Impl\extension.json") $extDir -Force
```

⚠ **`-DistRoot` に既定値を持たせないでください。** 渡し忘れたときに黙って別のフォルダへ置かれ、「配置したのに読まれない」という原因の見えない状態になります。

### 動いたかの確かめ方

```
./scripts/build.ps1
```

ホストを起動して、ノードグラフから `ngol.proc.ext_info` を実行します。拡張の一覧・宣言した capability・同梱ライブラリが読めたかどうかが 1 つの表で返ります。

---

## ライブラリを配るとき

`lib/<tfm>/` へ置くだけです。pack スクリプトでそこへコピーしてください。

```powershell
$libDir = Join-Path $extDir "lib\$Tfm"
New-Item -ItemType Directory -Path $libDir -Force | Out-Null
Copy-Item (Join-Path $implOut "SomeLib.dll") $libDir -Force
```

⚠ **ホストが同じ名前のアセンブリを既に持っていると、そちらが勝ちます。** これは異常ではありませんが、**版が違えば挙動も違います。** `ngol.proc.ext_info` は「配ったもの」と「実際に読まれたもの」の版を並べて出すので、食い違いはそこで分かります。

### 別名で隔離する

依存を内包しながらその型を `internal` にしていないライブラリは、本体と同じ名前空間の型をノードの参照集合へ持ち込みます。無修飾での解決先が 2 つになると、ノードのコンパイルが曖昧さで落ちます。

```json
"libraries": { "aliases": { "MonoMod.Iced": "monomod_iced" } }
```

こう宣言すると、そのアセンブリは `extern alias` の下にだけ現れます。`ngol.ext.il` が実際にこれを使っています。

---

## 落とし穴

| | |
|---|---|
| `apiVersion` | `1` 以外は**黙って飛ばされます**（ログには出ます）。増やすときは本体側の対応が要ります |
| `lib` のフォルダ名 | `net6.0` か `net462` だけ。`netstandard2.0` という名前のフォルダは見られません |
| `platforms` | ネイティブ依存があるなら必ず書いてください。合わない環境で読みに行くと、解決の失敗という分かりにくい形で出ます |
| `Unload` | ネイティブの資源を持ったら必ず外す。ホストは終了せずに拡張だけ落ちることがあります |
| `aliases` の順番 | ライブラリを読む前に効かせる必要があります。本体はその順で処理しますが、自分で `Assembly.LoadFrom` する場合は自分で守ってください |
| pack の `-DistRoot` | 既定値を持たせない |

---

## 実例を読む

| 拡張 | 何をしているか | 読みどころ |
|---|---|---|
| [`ngol.ext.code`](../extensions/NgolExt.Code/) | ライブラリを配って capability を名乗るだけ。サービス無し | **最小の形**。30 行 |
| [`ngol.ext.il`](../extensions/NgolExt.Il/) | ライブラリを 14 本配る。`aliases` を使う | 名前がぶつかるライブラリの扱い |
| [`ngol.ext.native-hook`](../extensions/NgolExt.NativeHook/) | ネイティブ DLL を読み、サービスを出し、`Unload` で撤去する | **一番大きい形**。ネイティブを抱える拡張の後始末 |
