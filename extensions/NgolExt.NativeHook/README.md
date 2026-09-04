# ngol.ext.native-hook Extension

`ngol_native.dll`（MinHook ベース、CLR 非依存のネイティブフック・メモリ読書き・スタックトレース機能）を NGOL Extension Host に公開する拡張（Api/Impl のみ）。

実際に使うノード（`ngol.hook.watch_function` / `ngol.hook.skip_function` / `ngol.hook.trace_calls`）は拡張パッケージの外にある。この拡張の `Api.dll` を参照する `.cs` として、Roslyn のホットリロード対象になっている。

## 構成

```
extensions/NgolExt.NativeHook/
  NgolExt.NativeHook.Api/     ← INativeHookService インターフェース定義（netstandard2.0）。ngolRootへもコピーされ、Roslynノードから参照可能
  NgolExt.NativeHook.Impl/    ← 実装（net6.0;net462マルチターゲット）。ngol_native.dllへのP/Invokeブリッジ
  NgolExt.NativeHook.Tests/   ← C#側テスト
  pack-native-hook-extension.ps1  ← ビルド＋配置スクリプト（下記参照）

native/ngol_native/           ← ngol_native.dll のC++ソース（別ディレクトリ、CMake）
  common/ngol_native.cpp      ← 実装本体（バックエンドの実装は知らない）
  common/ngol_hook_backend.h  ← バックエンド境界（差し替え点はここ1枚）
  backend-win-x64/backend_minhook.cpp  ← MinHookによる実装。-DNGOL_BACKEND=<name> で選択
  tests/test_hook.cpp         ← C++側単体テスト
  build/                      ← CMakeビルド出力（ngol_native.dll, test_hook.exe）

hook ノードの .cs（WatchFunctionNode / SkipFunctionNode / TraceCallsNode）  ← 拡張パッケージの外。Roslyn、ホットリロード対象
```

**ノード本体は拡張パッケージの外に置く。** ノードのポート追加・ロジック変更は `.cs` の保存だけでホットリロードされ、拡張自体（Api/Impl）の再ビルドもホストの再起動も要らない。拡張の中へ入れると、ノードを 1 行直すたびに再ビルドと再起動が必要になる。

## ビルド・配置は必ず `pack-native-hook-extension.ps1` を使う

**`dotnet build` を各プロジェクトで個別に叩いて手動コピーしない。** このスクリプトが Api → Impl の順にビルドし、`ngol_native.dll`・`extension.json`も含めて正しい配置先へ一括コピーする。

```powershell
# native/ngol_native 側を変更した場合は先にネイティブDLLをビルドしておくこと（下記参照）
.\pack-native-hook-extension.ps1 -DistRoot <Extensions/ を持つフォルダ>
# -DistRoot は必須。置き先は使う側の環境で違うので既定を持たせていない
```

配置先を他のホストで使う場合は、配置後の `Extensions/ngol.ext.native-hook/` フォルダごとコピーする。

### native/ngol_native（C++側）のビルド

`pack-native-hook-extension.ps1` はC#プロジェクトのビルドのみ行い、`native/ngol_native/build/ngol_native.dll` が存在しない場合は警告を出して**そのままスキップする**（自動ビルドしない）。C++側を変更した場合は先に手動でビルドすること。

Visual Studio 同梱の CMake/Ninja を使う例（`cmake.exe`/`ninja` が PATH に無い場合）。
入れた版・エディション・場所は環境ごとに違うため、位置が保証されている `vswhere` から辿る。
パスはリポジトリのルートからの相対で書いてある。

```powershell
$vswhere  = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath   = & $vswhere -latest -products * -property installationPath
$cmake    = Join-Path $vsPath "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
$ninjaDir = Join-Path $vsPath "Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja"
$vcvars   = Join-Path $vsPath "VC\Auxiliary\Build\vcvars64.bat"
$env:PATH = "$ninjaDir;$env:PATH"

$buildDir = Resolve-Path "native\ngol_native\build"
cmd /c "`"$vcvars`" && `"$cmake`" --build `"$buildDir`""

# C++側単体テスト実行
& (Join-Path $buildDir "test_hook.exe")
```

## アーキテクチャ概要

- ネイティブ側（`common/ngol_native.cpp`）: `MAX_HOOKS=64` の固定スロット。バックエンド境界（`NgolBackend_Create`/`Enable`）経由で RVA/絶対アドレスにフックを設置する。各フックは呼び出し回数・直近引数(a0〜a3)を記録し、`call_original` フラグで元関数呼び出しの有無を制御する。

- **多引数対応**: `NGOLHook_SetExtraStackArgs(hook, count)` で、レジスタ 4 個(a0-a3)を超える追加引数の個数(0-8)を指定できる。
  x64 呼び出し規約では第 5 引数以降はスタック経由で渡される。`_AddressOfReturnAddress()`（MSVC 組み込み、アセンブリ不要）でスタックから捕捉し、`call_original` 時に正しい引数個数の関数ポインタ型で元関数へ転送する。
  追加引数は 8 バイトポインタサイズ値のみ対応（浮動小数点・大きい値型構造体は非対応）。呼ばなければ従来どおり 4 引数のみ転送する（後方互換）。

- **浮動小数点(XMM)引数対応**: `NGOLHook_InstallTyped(pTarget, floatSlotMask, pHook)` で、レジスタ渡し 4 引数(スロット 0-3)のうち XMM(float/double)渡しのスロットをビットマスク(0-15)で指定できる。
  x64 呼び出し規約では、スロットごとに実際に使われる物理レジスタが型依存（整数/ポインタ型なら rcx/rdx/r8/r9、float/double 型なら xmm0-3）。コールバック自身の C++ シグネチャで該当スロットを double 型として宣言しないと正しく捕捉できない。
  既存の 64 スロット（GP 専用、`NGOLHook_Install`）とは別の小規模プール(16 個)を使う。`extraStackArgs` との併用は非対応。

- **マネージドコールバック注入機能**: `NGOLHook_SetManagedCallback` で、フック発火時に同期呼び出しされる C# 静的メソッドを登録できる。`net6.0` は `[UnmanagedCallersOnly]`、`net462` は従来型デリゲート+`GCHandle` で両対応する。
  ⚠ コールバック内で例外を投げるとネイティブ境界を越えて伝播しプロセスがクラッシュする。呼び出し側で必ず捕捉すること。

- **呼び出し元（戻り番地）**: `NGOLHook_ReadReturnAddress(hook, out addr)` で、直近の発火が呼び出し元へ戻る番地を返す。
  差し替えは `jmp` なので、呼び出し元が積んだ戻り番地はフック本体の入口にそのまま載っている。既に第 5 引数以降のために読んでいる場所の手前 8 バイトを控えるだけで済む。
  返すのは 1 段目だけで、それ以上を遡るのは呼ぶ側の仕事（巻き戻しは `.pdata` が要り動的生成コードで止まる／走査は情報が要らないが偽陽性が出る）。カーネル側まで要るなら ETW（WPR）。

- **発火のたびに 1 件ずつ書く貸し先**: `NGOLHook_SetRecordBuffer(hook, buffer, capacity, frames, out firstSeq)` で置き場を貸すと、発火のたびに 1 件（`NGOLHook_RecordSize(frames)` バイト = `[seq, retAddr, a0..a3]` ＋ `frames` 個の段）が書かれる。
  `frames`（0-64）は呼び出し元の連なりを何段まで残すかを決める。既定の 0 なら巻き戻しの命令は 1 つも走らない。**0 より大きくすると 1 件あたりの費用が 2 桁上がる**（実測 7.17ns -> 448ns/発火）。
  段は呼び出し元がネイティブなら深く伸び、動的に生成されたコード（JIT）に当たるとそこで止まる。端まで来たら先頭へ戻る。
  **この DLL が持つのはここまでで、確保も解放も読み出しもしない。** 積み方・読み方・並べ替え・何件消えたと数えるかは呼ぶ側の仕事（後入れ先出しで見たいなら通し番号の降順に読む）。読み出しには既存の `NGOLMem_ReadBytes` が使えるほか、呼ぶ側が確保した領域なので直接読んでよい。
  `seq` は最後に書かれ、0 は書きかけの印。読む側は `seq` を前後 2 回読んで一致したときだけ採る。貸していないときの費用は分岐 1 つ（実測 +0.30ns/発火、貸すと +0.50ns）。
