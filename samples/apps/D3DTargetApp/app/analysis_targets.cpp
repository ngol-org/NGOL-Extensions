// 自己解析の題材。NGOL の code.* / hook.* を「このホスト自身」に対して実演するための関数群。
//
// すべて __declspec(noinline) で固定し、名前を export する（disasm/xref/hook の的にする）。
// ビルド後に実際に disasm して、意図した形（LOCK 前置・スタック引数・RIP 相対など）が
//   残っていることを確認すること。最適化で消えたら題材にならない。

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <intrin.h>

extern "C"
{
    // RIP 相対参照の題材。関数はこのグローバルを読む。
    __declspec(dllexport) volatile long g_ngolAnalysisCounter = 0;

    // 最小例。disasm / function_bounds の的。
    __declspec(dllexport) __declspec(noinline) int NgolTarget_Add(int a, int b)
    {
        return a + b;
    }

    // 5 引数。x64 では第 5 引数がスタック渡し。引数個数の実測の的。
    __declspec(dllexport) __declspec(noinline) int NgolTarget_Sum5(int a, int b, int c, int d, int e)
    {
        return a + b + c + d + e;
    }

    // float / double 引数。XMM 渡しの的（watch_function の float 対応）。
    __declspec(dllexport) __declspec(noinline) double NgolTarget_Scale(float x, double k)
    {
        return (double)x * k + 1.5;
    }

    // LOCK 前置命令を含む。hook.safety_check が「危険」と判定して止める的。
    __declspec(dllexport) __declspec(noinline) long NgolTarget_LockedInc()
    {
        return _InterlockedIncrement(&g_ngolAnalysisCounter);   // lock xadd/inc
    }

    // RIP 相対でグローバルを読む。xref_find の的。
    __declspec(dllexport) __declspec(noinline) long NgolTarget_ReadGlobal()
    {
        return g_ngolAnalysisCounter;
    }

    // 3 段の呼び出しチェーン。disasm の call_targets 芋づるの的。
    __declspec(dllexport) __declspec(noinline) int NgolTarget_Leaf(int x) { return x * 2; }
    __declspec(dllexport) __declspec(noinline) int NgolTarget_Mid(int x)  { return NgolTarget_Leaf(x) + 1; }
    __declspec(dllexport) __declspec(noinline) int NgolTarget_Top(int x)  { return NgolTarget_Mid(x) + 1; }

    // ログを出さない関数。「ログを出さないライブラリの呼ばれ方を hook で実測する」の的。
    __declspec(dllexport) __declspec(noinline) int NgolTarget_Quiet(int x)
    {
        return (x ^ 0x5a5a) + 7;
    }
}
