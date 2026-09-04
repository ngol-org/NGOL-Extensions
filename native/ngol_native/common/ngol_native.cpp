#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <intrin.h>
#include <atomic>
#include "ngol_hook_backend.h"

// ============================================================
// この DLL の方針
// ============================================================
//
// P1. 実行時に確保しない。
//     置き場はすべて固定長（g_hooks / g_floatHooks）。
//     malloc / HeapAlloc / new を使わない。
//
// P2. 実行時に常駐しない。
//     スレッド・タイマー・周期処理を持たない。動くのは呼ばれたときだけ。
//     読み出しは呼ぶ側が覗きに来る形にし、こちらから押し出さない。
//
// P3. 置き場が要る機能は、呼ぶ側が確保して渡す。この DLL は預かるだけ。
//
// P4. 既に公開した口のシグネチャを変えない。欄を足すときは新しい口にする。
//     引数を増やすと、先に配られた版の呼び出し側が壊れる（C ABI の破壊的変更）。
//
// なぜ: フックは確保・同期・スレッド生成の関数にも張られる。ここが確保すると、
// その関数に張った瞬間に自分を呼び戻す。常駐すると、フックを 1 つも張らない
// ホストにも負担を課す。
//
// 機能を足すときは、その機能が上の 4 つを崩していないか先に確かめること。
// 「たかが数百 KB」「たかが 1 スレッド」で崩れる種類の方針である。

// ============================================================
// TLS エラー文字列
// ============================================================
static __declspec(thread) const char* t_lastError = nullptr;

static void SetErr(const char* msg) { t_lastError = msg; }
static void ClearErr()              { t_lastError = nullptr; }

extern "C" __declspec(dllexport)
const char* NGOL_GetLastError() { return t_lastError ? t_lastError : ""; }

// ============================================================
// フックテーブル
// ============================================================
#define MAX_HOOKS 64

// 発火 1 回ぶんの記録。配列を確保するのは呼ぶ側（P3）。
// seq は 1 始まり。0 は書きかけの印で、読み手はこれを見たら中身を採らない。
// volatile は消されないための指定で、順序は前後の壁（atomic_signal_fence）が作る。
//
// 1 件は「決まった 6 欄」＋「呼ぶ側が頼んだ段数ぶんの欄」。段数 0 なら 6 欄だけ。
#define HOOK_RECORD_FIXED_FIELDS 6
#define HOOK_MAX_FRAMES          64

struct HookRecord {
    volatile LONGLONG seq;
    LONGLONG retAddr;
    LONGLONG a0, a1, a2, a3;
    // この後ろに frames 個ぶんの LONGLONG が続くことがある（可変なので型に書けない）
};

struct HookEntry {
    LPVOID   target;
    LPVOID   trampoline;
    LONGLONG count;
    LONGLONG a0, a1, a2, a3;
    BOOL     active;
    BOOL     callOriginal;
    LPVOID   managedCallback; // 任意設定。void(__cdecl*)(LPVOID hook, LPVOID a0, LPVOID a1, LPVOID a2, LPVOID a3)
    int      extraStackArgs;  // レジスタ4個(a0-a3)を超える追加引数の個数(0-8)。call_original時の転送に使う
    LONGLONG extra[8];        // 捕捉した追加引数(スタック経由の第5〜12引数、8バイトポインタサイズ値のみ対応)
    LONGLONG returnValue;     // callOriginal=FALSE のときに呼び出し元へ返す値（RAX）
    int      floatSlotMask;   // bit i が1ならスロットi(0-3)はXMM(浮動小数点)渡し。g_floatHooksのエントリのみ非0
    // 直近の呼び出し元へ戻る番地。差し替えは jmp なので、呼び出し元が積んだ番地が
    // フック本体の入口にそのまま載っている。ここが返すのは 1 段目だけ（それ以上は
    // 呼ぶ側の仕事。方法は 2 つあり、どちらも一長一短。ヘッダの説明を参照）。
    LONGLONG retAddr;

    // 発火のたびに 1 件書く貸し先。既定は null で、そのとき本体は分岐 1 つで抜ける。
    // 件数は 2 の冪に限る（位置決めを割り算ではなく and 1 つで済ませるため）。
    HookRecord* volatile records;
    LONG recordMask;
    LONG recordCapacity;

    // 1 件が LONGLONG 何個ぶんか（6 + frames）。位置決めに使う。
    LONG recordStride;
    // 何段まで辿るか。0 なら巻き戻しの命令は 1 つも走らない。
    LONG frames;
};

static HookEntry      g_hooks[MAX_HOOKS];
static int            g_hookCount = 0;
static CRITICAL_SECTION g_cs;
static bool           g_csInit = false;
static bool           g_mhInit = false;

// ============================================================
// XMM(浮動小数点)引数対応フックテーブル（既存g_hooksとは別枠）
// ============================================================
// レジスタ渡し4引数(スロット0-3)のうちfloatSlotMaskで指定したスロットをXMMレジスタ渡し
// (float/double)として捕捉・転送する。既存g_hooks(常にGPレジスタとして捕捉)とは独立した
// 小規模プール。floatSlotMask=0のフックはNGOLHook_Installで従来通りg_hooksを使うこと。
#define MAX_FLOAT_HOOKS 16

static HookEntry g_floatHooks[MAX_FLOAT_HOOKS];

// ============================================================
// 追加スタック引数（第5〜12引数）の捕捉・転送ヘルパー
// ============================================================
// x64呼び出し規約: 第1〜4引数はrcx/rdx/r8/r9、第5引数以降は呼び出し元スタックに積まれる。
// スタックレイアウト（関数エントリ時点、rspを基準に）:
//   [retAddr]         戻り先アドレス
//   [retAddr+0x08..+0x27] シャドウスペース(32バイト、レジスタ引数の待避用に呼び出し元が確保)
//   [retAddr+0x28]    第5引数
//   [retAddr+0x30]    第6引数 ...(以降8バイト刻み)
// _AddressOfReturnAddress() は必ず HookImpl_N 自身の中で直接呼び出すこと
// （ネストした関数内で呼ぶとその関数自身の戻り先になってしまい、目的のアドレスが取れない）。
static void CaptureExtraArgs(HookEntry* e, void* retAddrPtr) {
    int n = e->extraStackArgs;
    if (n <= 0) return;
    if (n > 8) n = 8;
    BYTE* stackArgs = reinterpret_cast<BYTE*>(retAddrPtr) + 8 + 0x20;
    for (int i = 0; i < n; i++) {
        e->extra[i] = *reinterpret_cast<LONGLONG*>(stackArgs + i * 8);
    }
}

// 段数を頼まれたときだけ呼ぶ。別の関数に出してあるのは、頼んでいない人の
// hot path に巻き戻しの命令列を混ぜないため（混ぜると頼まなくても遅くなる。実測済み）。
//
// 手前の 3 段（この関数・WriteRecord・HookImpl_N）は飛ばす。飛ばさないと、
// 頼まれた段数のうち 2 つがこの DLL の中身で埋まってしまう。
// 3 という数はこの 3 つが必ず別の関数であることに乗っている（noinline と
// forceinline で固定してある）。ずれたら test_Frames_reach_the_caller が落ちる。
#define HOOK_FRAMES_TO_SKIP 3

static __declspec(noinline) void CaptureFrames(LONG frames, LONGLONG* out) {
    const DWORD n = RtlCaptureStackBackTrace(HOOK_FRAMES_TO_SKIP, (DWORD)frames,
                                             reinterpret_cast<PVOID*>(out), nullptr);
    for (LONG i = (LONG)n; i < frames; i++) out[i] = 0;
}

// 呼び出しを 1 件残す。貸し先が無ければ分岐 1 つで抜ける。確保も鍵取りもしない（P1）。
// 書く順序は 書きかけの印 -> 中身 -> 通し番号。印を先に落としておかないと、
// 一周して上書きしている最中の中身を「完成している」と誤って読める。
static __declspec(noinline) void WriteRecord(HookEntry* e, LONGLONG* buf, LONGLONG seq,
                                             LONGLONG a0, LONGLONG a1, LONGLONG a2, LONGLONG a3) {
    LONGLONG* r = buf + (SIZE_T)((seq - 1) & e->recordMask) * e->recordStride;
    r[0] = 0;
    std::atomic_signal_fence(std::memory_order_seq_cst);
    r[1] = e->retAddr;
    r[2] = a0;
    r[3] = a1;
    r[4] = a2;
    r[5] = a3;
    // 段数を頼まれていなければ、ここは分岐 1 つで抜ける。
    //
    // 頼まれたときは巻き戻す。差し替えは jmp なので、この関数を巻き戻すと
    // そのまま元の呼び出し元へ着地し、その先は普通の PE で .pdata がある。
    // 実測: ネイティブの呼び出し元まで 7 段（スレッドの起点まで）。
    // 動的に生成されたコード（JIT）が混ざるとそこで止まる。これは方式の限界で、
    // 呼ぶ側が知っておくこと。
    //
    // 費用は 1ns 弱から 3 桁上がる。だから既定は 0。
    if (e->frames > 0) CaptureFrames(e->frames, r + HOOK_RECORD_FIXED_FIELDS);
    std::atomic_signal_fence(std::memory_order_seq_cst);
    r[0] = seq;
}

// 貸し先が無いときに払うのはここだけ。読んで分岐して終わり。
// 書く処理を別関数に出してあるのは、これを展開させ続けるため。
// 一緒にしておくと、機能を足したときにコンパイラが展開をやめ、
// 貸していない人まで call と ret を払う（実測で踏んだ）。
static __forceinline void PushRecord(HookEntry* e, LONGLONG seq,
                                     LONGLONG a0, LONGLONG a1, LONGLONG a2, LONGLONG a3) {
    LONGLONG* buf = reinterpret_cast<LONGLONG*>(e->records);
    if (!buf) return;
    WriteRecord(e, buf, seq, a0, a1, a2, a3);
}

typedef LONGLONG(__cdecl* Fn4)(LPVOID,LPVOID,LPVOID,LPVOID);
typedef LONGLONG(__cdecl* Fn5)(LPVOID,LPVOID,LPVOID,LPVOID,LPVOID);
typedef LONGLONG(__cdecl* Fn6)(LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID);
typedef LONGLONG(__cdecl* Fn7)(LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID);
typedef LONGLONG(__cdecl* Fn8)(LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID);
typedef LONGLONG(__cdecl* Fn9)(LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID);
typedef LONGLONG(__cdecl* Fn10)(LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID);
typedef LONGLONG(__cdecl* Fn11)(LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID);
typedef LONGLONG(__cdecl* Fn12)(LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID,LPVOID);

// extraStackArgs の個数に応じて trampoline(元関数) を正しい引数個数の関数ポインタ型で呼び出す。
// extraStackArgs=0（デフォルト、既存利用箇所）は従来通り4引数のみで呼ぶ。
// 戻り値は元関数が返した値をそのまま返す。
// 呼び出し元は整数・ポインタを RAX で受け取るため、ここで受けて返さないと
//   「たまたま RAX に残っていた値」が返ることになり、保証が無い。
static LONGLONG CallOriginalWithExtra(HookEntry* e, LPVOID a0, LPVOID a1, LPVOID a2, LPVOID a3) {
    LPVOID t = e->trampoline;
    LPVOID* x = reinterpret_cast<LPVOID*>(e->extra);
    switch (e->extraStackArgs) {
        case 0:  return reinterpret_cast<Fn4>(t)(a0,a1,a2,a3);
        case 1:  return reinterpret_cast<Fn5>(t)(a0,a1,a2,a3,x[0]);
        case 2:  return reinterpret_cast<Fn6>(t)(a0,a1,a2,a3,x[0],x[1]);
        case 3:  return reinterpret_cast<Fn7>(t)(a0,a1,a2,a3,x[0],x[1],x[2]);
        case 4:  return reinterpret_cast<Fn8>(t)(a0,a1,a2,a3,x[0],x[1],x[2],x[3]);
        case 5:  return reinterpret_cast<Fn9>(t)(a0,a1,a2,a3,x[0],x[1],x[2],x[3],x[4]);
        case 6:  return reinterpret_cast<Fn10>(t)(a0,a1,a2,a3,x[0],x[1],x[2],x[3],x[4],x[5]);
        case 7:  return reinterpret_cast<Fn11>(t)(a0,a1,a2,a3,x[0],x[1],x[2],x[3],x[4],x[5],x[6]);
        default: return reinterpret_cast<Fn12>(t)(a0,a1,a2,a3,x[0],x[1],x[2],x[3],x[4],x[5],x[6],x[7]);
    }
}

// ============================================================
// 静的コールバック配列（マクロ展開）
// ============================================================
// managedCallback が設定されている場合、統計記録の直後・callOriginal分岐の前に同期呼び出しする。
// 呼び出し規約: void(__cdecl*)(LPVOID hook, LPVOID a0, LPVOID a1, LPVOID a2, LPVOID a3)
// managedCallback 側（C#の [UnmanagedCallersOnly] サンク）は、ネイティブ境界を越えて
// 例外を伝播させないことが呼び出し規約上の必須要件。
// 戻り値: callOriginal なら元関数の値、そうでなければ returnValue（既定 0）。
//   元関数を呼ばない場合、呼び出し元は「何も実行されなかった」ことを知らないまま
//   戻り値を受け取る。返す値を決めておかないと不定値が渡るため、必ずここで決める。
#define HOOK_CB(N) \
static LONGLONG __cdecl HookImpl_##N(LPVOID a0, LPVOID a1, LPVOID a2, LPVOID a3) { \
    HookEntry* e = &g_hooks[N]; \
    void* retAddrPtr = _AddressOfReturnAddress(); \
    LONGLONG seq = InterlockedIncrement64(&e->count); \
    e->retAddr = *reinterpret_cast<LONGLONG*>(retAddrPtr); \
    e->a0 = (LONGLONG)a0; \
    e->a1 = (LONGLONG)a1; \
    e->a2 = (LONGLONG)a2; \
    e->a3 = (LONGLONG)a3; \
    PushRecord(e, seq, e->a0, e->a1, e->a2, e->a3); \
    CaptureExtraArgs(e, retAddrPtr); \
    if (e->managedCallback) { \
        reinterpret_cast<void(__cdecl*)(LPVOID,LPVOID,LPVOID,LPVOID,LPVOID)>(e->managedCallback)(e, a0, a1, a2, a3); \
    } \
    if (e->callOriginal && e->trampoline) { \
        return CallOriginalWithExtra(e, a0, a1, a2, a3); \
    } \
    return e->returnValue; \
}

HOOK_CB(0)  HOOK_CB(1)  HOOK_CB(2)  HOOK_CB(3)
HOOK_CB(4)  HOOK_CB(5)  HOOK_CB(6)  HOOK_CB(7)
HOOK_CB(8)  HOOK_CB(9)  HOOK_CB(10) HOOK_CB(11)
HOOK_CB(12) HOOK_CB(13) HOOK_CB(14) HOOK_CB(15)
HOOK_CB(16) HOOK_CB(17) HOOK_CB(18) HOOK_CB(19)
HOOK_CB(20) HOOK_CB(21) HOOK_CB(22) HOOK_CB(23)
HOOK_CB(24) HOOK_CB(25) HOOK_CB(26) HOOK_CB(27)
HOOK_CB(28) HOOK_CB(29) HOOK_CB(30) HOOK_CB(31)
HOOK_CB(32) HOOK_CB(33) HOOK_CB(34) HOOK_CB(35)
HOOK_CB(36) HOOK_CB(37) HOOK_CB(38) HOOK_CB(39)
HOOK_CB(40) HOOK_CB(41) HOOK_CB(42) HOOK_CB(43)
HOOK_CB(44) HOOK_CB(45) HOOK_CB(46) HOOK_CB(47)
HOOK_CB(48) HOOK_CB(49) HOOK_CB(50) HOOK_CB(51)
HOOK_CB(52) HOOK_CB(53) HOOK_CB(54) HOOK_CB(55)
HOOK_CB(56) HOOK_CB(57) HOOK_CB(58) HOOK_CB(59)
HOOK_CB(60) HOOK_CB(61) HOOK_CB(62) HOOK_CB(63)

typedef LONGLONG(__cdecl* HookCb)(LPVOID, LPVOID, LPVOID, LPVOID);

static HookCb g_callbacks[MAX_HOOKS] = {
    HookImpl_0,  HookImpl_1,  HookImpl_2,  HookImpl_3,
    HookImpl_4,  HookImpl_5,  HookImpl_6,  HookImpl_7,
    HookImpl_8,  HookImpl_9,  HookImpl_10, HookImpl_11,
    HookImpl_12, HookImpl_13, HookImpl_14, HookImpl_15,
    HookImpl_16, HookImpl_17, HookImpl_18, HookImpl_19,
    HookImpl_20, HookImpl_21, HookImpl_22, HookImpl_23,
    HookImpl_24, HookImpl_25, HookImpl_26, HookImpl_27,
    HookImpl_28, HookImpl_29, HookImpl_30, HookImpl_31,
    HookImpl_32, HookImpl_33, HookImpl_34, HookImpl_35,
    HookImpl_36, HookImpl_37, HookImpl_38, HookImpl_39,
    HookImpl_40, HookImpl_41, HookImpl_42, HookImpl_43,
    HookImpl_44, HookImpl_45, HookImpl_46, HookImpl_47,
    HookImpl_48, HookImpl_49, HookImpl_50, HookImpl_51,
    HookImpl_52, HookImpl_53, HookImpl_54, HookImpl_55,
    HookImpl_56, HookImpl_57, HookImpl_58, HookImpl_59,
    HookImpl_60, HookImpl_61, HookImpl_62, HookImpl_63,
};

// ============================================================
// XMM(浮動小数点)引数対応コールバック（マクロ展開）
// ============================================================
// x64呼び出し規約では、スロット0-3の実際に使われる物理レジスタは型に依存する
// (整数/ポインタ型ならrcx/rdx/r8/r9、float/double型ならxmm0-3)。コールバック自身の
// C++シグネチャでスロットごとにLPVOID/doubleを正しく宣言しない限りコンパイラは
// 正しいレジスタを読まない。float/doubleは区別せず、XMM渡しの
// スロットは常にdoubleとして捕捉・転送する（下位32/64ビットどちらの実引数でも
// 呼び出し先が読む幅の分だけ正しいビットパターンが伝わるため）。
static LONGLONG BitsOf(LPVOID v) { return (LONGLONG)(LONG_PTR)v; }
static LONGLONG BitsOf(double v) { LONGLONG r; memcpy(&r, &v, sizeof(r)); return r; }

// floatSlotMaskの値ごとの各スロット型（0=GP/LPVOID, 1=XMM/double）
#define FLOAT_HOOK_CB(N, M, T0, T1, T2, T3) \
static void __cdecl FloatHookImpl_##N##_##M(T0 a0, T1 a1, T2 a2, T3 a3) { \
    HookEntry* e = &g_floatHooks[N]; \
    LONGLONG seq = InterlockedIncrement64(&e->count); \
    e->retAddr = *reinterpret_cast<LONGLONG*>(_AddressOfReturnAddress()); \
    e->a0 = BitsOf(a0); \
    e->a1 = BitsOf(a1); \
    e->a2 = BitsOf(a2); \
    e->a3 = BitsOf(a3); \
    PushRecord(e, seq, e->a0, e->a1, e->a2, e->a3); \
    if (e->managedCallback) { \
        reinterpret_cast<void(__cdecl*)(LPVOID,LPVOID,LPVOID,LPVOID,LPVOID)>(e->managedCallback)( \
            e, (LPVOID)(LONG_PTR)e->a0, (LPVOID)(LONG_PTR)e->a1, (LPVOID)(LONG_PTR)e->a2, (LPVOID)(LONG_PTR)e->a3); \
    } \
    if (e->callOriginal && e->trampoline) { \
        reinterpret_cast<void(__cdecl*)(T0,T1,T2,T3)>(e->trampoline)(a0,a1,a2,a3); \
    } \
}

// floatSlotMask 0〜15 の16通りの型付きコールバックをスロットNについて生成する
#define FLOAT_HOOK_CB_ALL_MASKS(N) \
    FLOAT_HOOK_CB(N, 0,  LPVOID,LPVOID,LPVOID,LPVOID) \
    FLOAT_HOOK_CB(N, 1,  double,LPVOID,LPVOID,LPVOID) \
    FLOAT_HOOK_CB(N, 2,  LPVOID,double,LPVOID,LPVOID) \
    FLOAT_HOOK_CB(N, 3,  double,double,LPVOID,LPVOID) \
    FLOAT_HOOK_CB(N, 4,  LPVOID,LPVOID,double,LPVOID) \
    FLOAT_HOOK_CB(N, 5,  double,LPVOID,double,LPVOID) \
    FLOAT_HOOK_CB(N, 6,  LPVOID,double,double,LPVOID) \
    FLOAT_HOOK_CB(N, 7,  double,double,double,LPVOID) \
    FLOAT_HOOK_CB(N, 8,  LPVOID,LPVOID,LPVOID,double) \
    FLOAT_HOOK_CB(N, 9,  double,LPVOID,LPVOID,double) \
    FLOAT_HOOK_CB(N, 10, LPVOID,double,LPVOID,double) \
    FLOAT_HOOK_CB(N, 11, double,double,LPVOID,double) \
    FLOAT_HOOK_CB(N, 12, LPVOID,LPVOID,double,double) \
    FLOAT_HOOK_CB(N, 13, double,LPVOID,double,double) \
    FLOAT_HOOK_CB(N, 14, LPVOID,double,double,double) \
    FLOAT_HOOK_CB(N, 15, double,double,double,double)

FLOAT_HOOK_CB_ALL_MASKS(0)  FLOAT_HOOK_CB_ALL_MASKS(1)
FLOAT_HOOK_CB_ALL_MASKS(2)  FLOAT_HOOK_CB_ALL_MASKS(3)
FLOAT_HOOK_CB_ALL_MASKS(4)  FLOAT_HOOK_CB_ALL_MASKS(5)
FLOAT_HOOK_CB_ALL_MASKS(6)  FLOAT_HOOK_CB_ALL_MASKS(7)
FLOAT_HOOK_CB_ALL_MASKS(8)  FLOAT_HOOK_CB_ALL_MASKS(9)
FLOAT_HOOK_CB_ALL_MASKS(10) FLOAT_HOOK_CB_ALL_MASKS(11)
FLOAT_HOOK_CB_ALL_MASKS(12) FLOAT_HOOK_CB_ALL_MASKS(13)
FLOAT_HOOK_CB_ALL_MASKS(14) FLOAT_HOOK_CB_ALL_MASKS(15)

// g_floatCallbacks[N][M]: スロットNのfloatSlotMask=Mに対応する型付きコールバック。
// 関数ポインタ型がマスクごとに異なるためLPVOID(void*)にreinterpret_castして格納する
// (MinHookの既存利用箇所・g_callbacksと同じ手法)。
#define FLOAT_CB_ROW(N) { \
    reinterpret_cast<LPVOID>(FloatHookImpl_##N##_0),  reinterpret_cast<LPVOID>(FloatHookImpl_##N##_1), \
    reinterpret_cast<LPVOID>(FloatHookImpl_##N##_2),  reinterpret_cast<LPVOID>(FloatHookImpl_##N##_3), \
    reinterpret_cast<LPVOID>(FloatHookImpl_##N##_4),  reinterpret_cast<LPVOID>(FloatHookImpl_##N##_5), \
    reinterpret_cast<LPVOID>(FloatHookImpl_##N##_6),  reinterpret_cast<LPVOID>(FloatHookImpl_##N##_7), \
    reinterpret_cast<LPVOID>(FloatHookImpl_##N##_8),  reinterpret_cast<LPVOID>(FloatHookImpl_##N##_9), \
    reinterpret_cast<LPVOID>(FloatHookImpl_##N##_10), reinterpret_cast<LPVOID>(FloatHookImpl_##N##_11), \
    reinterpret_cast<LPVOID>(FloatHookImpl_##N##_12), reinterpret_cast<LPVOID>(FloatHookImpl_##N##_13), \
    reinterpret_cast<LPVOID>(FloatHookImpl_##N##_14), reinterpret_cast<LPVOID>(FloatHookImpl_##N##_15) \
}

static LPVOID g_floatCallbacks[MAX_FLOAT_HOOKS][16] = {
    FLOAT_CB_ROW(0),  FLOAT_CB_ROW(1),  FLOAT_CB_ROW(2),  FLOAT_CB_ROW(3),
    FLOAT_CB_ROW(4),  FLOAT_CB_ROW(5),  FLOAT_CB_ROW(6),  FLOAT_CB_ROW(7),
    FLOAT_CB_ROW(8),  FLOAT_CB_ROW(9),  FLOAT_CB_ROW(10), FLOAT_CB_ROW(11),
    FLOAT_CB_ROW(12), FLOAT_CB_ROW(13), FLOAT_CB_ROW(14), FLOAT_CB_ROW(15),
};

// ============================================================
// 初期化ヘルパー
// ============================================================
static bool EnsureInit() {
    if (!g_csInit) {
        InitializeCriticalSection(&g_cs);
        g_csInit = true;
    }
    if (!g_mhInit) {
        if (const char* err = NgolBackend_Init()) {
            SetErr(err);
            return false;
        }
        g_mhInit = true;
    }
    return true;
}

// エントリ検索（CS 内から呼ぶこと）。g_hooks(GP専用)・g_floatHooks(XMM対応)の両方を見る。
static HookEntry* FindByHandle(LPVOID hook) {
    HookEntry* e = reinterpret_cast<HookEntry*>(hook);
    if (!e) return nullptr;
    bool inMain  = e >= g_hooks      && e < g_hooks      + MAX_HOOKS;
    bool inFloat = e >= g_floatHooks && e < g_floatHooks + MAX_FLOAT_HOOKS;
    if (!inMain && !inFloat) return nullptr;
    if (!e->active) return nullptr;
    return e;
}

// hook が g_floatHooks（XMM対応プール）のエントリかどうか
static bool IsFloatHookEntry(HookEntry* e) {
    return e >= g_floatHooks && e < g_floatHooks + MAX_FLOAT_HOOKS;
}

// ============================================================
// フック管理
// ============================================================
extern "C" __declspec(dllexport)
BOOL NGOLHook_Install(LPVOID pTarget, LPVOID* pHook) {
    ClearErr();
    if (!pTarget) { SetErr("ERR: TARGET_NULL"); return FALSE; }
    if (!pHook)   { SetErr("ERR: PHOOK_NULL");  return FALSE; }
    *pHook = nullptr;

    // 0x00 パディング検出
    BYTE firstByte = 0;
    __try { firstByte = *reinterpret_cast<BYTE*>(pTarget); }
    __except(EXCEPTION_EXECUTE_HANDLER) {
        SetErr("ERR: TARGET_UNREADABLE");
        return FALSE;
    }
    if (firstByte == 0x00) {
        SetErr("ERR: RVA_PADDING (first byte is 0x00, try target+1)");
        return FALSE;
    }

    if (!EnsureInit()) return FALSE;

    EnterCriticalSection(&g_cs);

    // 重複チェック
    for (int i = 0; i < MAX_HOOKS; i++) {
        if (g_hooks[i].active && g_hooks[i].target == pTarget) {
            LeaveCriticalSection(&g_cs);
            SetErr("ERR: ALREADY_HOOKED");
            return FALSE;
        }
    }

    // 空きスロット検索
    int idx = -1;
    for (int i = 0; i < MAX_HOOKS; i++) {
        if (!g_hooks[i].active) { idx = i; break; }
    }
    if (idx < 0) {
        LeaveCriticalSection(&g_cs);
        SetErr("ERR: TABLE_FULL");
        return FALSE;
    }

    LPVOID trampoline = nullptr;
    if (const char* err = NgolBackend_Create(pTarget, reinterpret_cast<LPVOID>(g_callbacks[idx]), &trampoline)) {
        LeaveCriticalSection(&g_cs);
        SetErr(err);
        return FALSE;
    }
    // 差し替えを有効にするのは、欄を全部書いたあと。
    // 先に有効にすると、書き終わるまでの間に入ってきた呼び出しが前の住人の値を読む。
    // 初回はどの欄も 0 なので、trampoline も 0 で「元関数を呼ばずに 0 を返す」になる。
    //
    // 既定は「元関数を呼ぶ」。見張るだけのつもりの設置が対象を止めてしまうのは危険側で、
    // 確保系の関数に張ると呼び出し元が NULL を受け取って落ちる（実測）。
    // 止めたい側は設置のあとに明示して切り替える。
    HookEntry* e   = &g_hooks[idx];
    e->target       = pTarget;
    e->trampoline   = trampoline;
    e->count        = 0;
    e->a0 = e->a1 = e->a2 = e->a3 = 0;
    e->retAddr      = 0;
    e->callOriginal = TRUE;
    e->managedCallback = nullptr;
    e->extraStackArgs  = 0;
    e->returnValue     = 0;
    for (int i = 0; i < 8; i++) e->extra[i] = 0;
    e->floatSlotMask = 0;
    e->records        = nullptr;
    e->recordMask     = 0;
    e->recordCapacity = 0;
    e->recordStride   = HOOK_RECORD_FIXED_FIELDS;
    e->frames         = 0;
    e->active       = TRUE;   // 空きスロット判定に使うので最後に立てる

    if (const char* err = NgolBackend_Enable(pTarget)) {
        e->active = FALSE;
        NgolBackend_Remove(pTarget);
        LeaveCriticalSection(&g_cs);
        SetErr(err);
        return FALSE;
    }

    LeaveCriticalSection(&g_cs);
    *pHook = reinterpret_cast<LPVOID>(e);
    return TRUE;
}

// XMM(浮動小数点)対応版フック設置。floatSlotMaskのbit i が1ならスロットi(0-3)を
// XMMレジスタ渡し(float/double)として捕捉・転送する。既存NGOLHook_Install(g_hooks、64個)
// とは別枠の小規模プール(g_floatHooks、16個)を使う。extraStackArgsとの併用は非対応
// (NGOLHook_SetExtraStackArgsで拒否される)。
extern "C" __declspec(dllexport)
BOOL NGOLHook_InstallTyped(LPVOID pTarget, int floatSlotMask, LPVOID* pHook) {
    ClearErr();
    if (!pTarget) { SetErr("ERR: TARGET_NULL"); return FALSE; }
    if (!pHook)   { SetErr("ERR: PHOOK_NULL");  return FALSE; }
    *pHook = nullptr;
    if (floatSlotMask < 0 || floatSlotMask > 15) {
        SetErr("ERR: INVALID_FLOAT_SLOT_MASK (must be 0-15)");
        return FALSE;
    }

    // 0x00 パディング検出
    BYTE firstByte = 0;
    __try { firstByte = *reinterpret_cast<BYTE*>(pTarget); }
    __except(EXCEPTION_EXECUTE_HANDLER) {
        SetErr("ERR: TARGET_UNREADABLE");
        return FALSE;
    }
    if (firstByte == 0x00) {
        SetErr("ERR: RVA_PADDING (first byte is 0x00, try target+1)");
        return FALSE;
    }

    if (!EnsureInit()) return FALSE;

    EnterCriticalSection(&g_cs);

    // 重複チェック（既存g_hooks・g_floatHooksの両方を見る）
    for (int i = 0; i < MAX_HOOKS; i++) {
        if (g_hooks[i].active && g_hooks[i].target == pTarget) {
            LeaveCriticalSection(&g_cs);
            SetErr("ERR: ALREADY_HOOKED");
            return FALSE;
        }
    }
    for (int i = 0; i < MAX_FLOAT_HOOKS; i++) {
        if (g_floatHooks[i].active && g_floatHooks[i].target == pTarget) {
            LeaveCriticalSection(&g_cs);
            SetErr("ERR: ALREADY_HOOKED");
            return FALSE;
        }
    }

    // 空きスロット検索
    int idx = -1;
    for (int i = 0; i < MAX_FLOAT_HOOKS; i++) {
        if (!g_floatHooks[i].active) { idx = i; break; }
    }
    if (idx < 0) {
        LeaveCriticalSection(&g_cs);
        SetErr("ERR: FLOAT_HOOK_TABLE_FULL");
        return FALSE;
    }

    LPVOID trampoline = nullptr;
    if (const char* err = NgolBackend_Create(pTarget, g_floatCallbacks[idx][floatSlotMask], &trampoline)) {
        LeaveCriticalSection(&g_cs);
        SetErr(err);
        return FALSE;
    }
    // 差し替えを有効にするのは、欄を全部書いたあと。
    // 先に有効にすると、書き終わるまでの間に入ってきた呼び出しが前の住人の値を読む。
    // 初回はどの欄も 0 なので、trampoline も 0 で「元関数を呼ばずに 0 を返す」になる。
    //
    // 既定は「元関数を呼ぶ」。見張るだけのつもりの設置が対象を止めてしまうのは危険側で、
    // 確保系の関数に張ると呼び出し元が NULL を受け取って落ちる（実測）。
    // 止めたい側は設置のあとに明示して切り替える。
    HookEntry* e   = &g_floatHooks[idx];
    e->target       = pTarget;
    e->trampoline   = trampoline;
    e->count        = 0;
    e->a0 = e->a1 = e->a2 = e->a3 = 0;
    e->retAddr      = 0;
    e->callOriginal = TRUE;
    e->managedCallback = nullptr;
    e->extraStackArgs  = 0;
    e->returnValue     = 0;
    for (int i = 0; i < 8; i++) e->extra[i] = 0;
    e->floatSlotMask = floatSlotMask;
    e->records        = nullptr;
    e->recordMask     = 0;
    e->recordCapacity = 0;
    e->recordStride   = HOOK_RECORD_FIXED_FIELDS;
    e->frames         = 0;
    e->active       = TRUE;   // 空きスロット判定に使うので最後に立てる

    if (const char* err = NgolBackend_Enable(pTarget)) {
        e->active = FALSE;
        NgolBackend_Remove(pTarget);
        LeaveCriticalSection(&g_cs);
        SetErr(err);
        return FALSE;
    }

    LeaveCriticalSection(&g_cs);
    *pHook = reinterpret_cast<LPVOID>(e);
    return TRUE;
}

extern "C" __declspec(dllexport)
BOOL NGOLHook_Uninstall(LPVOID hook) {
    ClearErr();
    if (!g_csInit) { SetErr("ERR: NOT_INITIALIZED"); return FALSE; }
    EnterCriticalSection(&g_cs);
    HookEntry* e = FindByHandle(hook);
    if (!e) {
        LeaveCriticalSection(&g_cs);
        SetErr("ERR: INVALID_HANDLE");
        return FALSE;
    }
    LPVOID target = e->target;
    // 先にコールバックをクリアしてからフックを無効化する（解除中の発火との競合を避ける）。
    e->managedCallback = nullptr;
    e->records        = nullptr;
    e->active = FALSE;
    LeaveCriticalSection(&g_cs);

    NgolBackend_Disable(target);
    NgolBackend_Remove(target);
    return TRUE;
}

extern "C" __declspec(dllexport)
void NGOLHook_UninstallAll() {
    if (!g_mhInit) return;
    EnterCriticalSection(&g_cs);
    for (int i = 0; i < MAX_HOOKS; i++) {
        if (g_hooks[i].active) {
            g_hooks[i].managedCallback = nullptr;
            g_hooks[i].records = nullptr;
            NgolBackend_Disable(g_hooks[i].target);
            NgolBackend_Remove(g_hooks[i].target);
            g_hooks[i].active = FALSE;
        }
    }
    for (int i = 0; i < MAX_FLOAT_HOOKS; i++) {
        if (g_floatHooks[i].active) {
            g_floatHooks[i].managedCallback = nullptr;
            g_floatHooks[i].records = nullptr;
            NgolBackend_Disable(g_floatHooks[i].target);
            NgolBackend_Remove(g_floatHooks[i].target);
            g_floatHooks[i].active = FALSE;
        }
    }
    LeaveCriticalSection(&g_cs);
}

extern "C" __declspec(dllexport)
void NGOLHook_Read(LPVOID hook,
                   LONGLONG* pCount,
                   LONGLONG* pA0, LONGLONG* pA1,
                   LONGLONG* pA2, LONGLONG* pA3) {
    HookEntry* e = FindByHandle(hook);
    if (!e) {
        if (pCount) *pCount = 0;
        if (pA0) *pA0 = 0; if (pA1) *pA1 = 0;
        if (pA2) *pA2 = 0; if (pA3) *pA3 = 0;
        return;
    }
    if (pCount) *pCount = e->count;
    if (pA0)   *pA0   = e->a0;
    if (pA1)   *pA1   = e->a1;
    if (pA2)   *pA2   = e->a2;
    if (pA3)   *pA3   = e->a3;
}

// 直近の呼び出し元へ戻る番地を返す。モジュールの載り位置を引けばモジュール名 + RVA になる。
// この口が返すのは 1 段目だけ。それ以上を遡る方法は 2 つあり、どちらも呼ぶ側で行う:
//   巻き戻し（RtlCaptureStackBackTrace 等）: 正確だが .pdata が要る。動的に生成された
//     コード（JIT・トランポリン）では使えない。実測: マネージドのフック本体から呼ぶと 18 回すべて 0 段
//   走査: スタックを見て戻り番地らしき値を拾う。巻き戻しの情報を必要としないが偽陽性が出る
extern "C" __declspec(dllexport)
void NGOLHook_ReadReturnAddress(LPVOID hook, LONGLONG* pReturnAddress) {
    if (!pReturnAddress) return;
    HookEntry* e = FindByHandle(hook);
    *pReturnAddress = e ? e->retAddr : 0;
}

extern "C" __declspec(dllexport)
void NGOLHook_ResetCount(LPVOID hook) {
    HookEntry* e = FindByHandle(hook);
    if (e) InterlockedExchange64(&e->count, 0);
}

extern "C" __declspec(dllexport)
LPVOID NGOLHook_GetTrampoline(LPVOID hook) {
    HookEntry* e = FindByHandle(hook);
    return e ? e->trampoline : nullptr;
}

extern "C" __declspec(dllexport)
BOOL NGOLHook_IsActive(LPVOID hook) {
    HookEntry* e = reinterpret_cast<HookEntry*>(hook);
    if (!e) return FALSE;
    bool inMain  = e >= g_hooks      && e < g_hooks      + MAX_HOOKS;
    bool inFloat = e >= g_floatHooks && e < g_floatHooks + MAX_FLOAT_HOOKS;
    if (!inMain && !inFloat) return FALSE;
    return e->active;
}

extern "C" __declspec(dllexport)
BOOL NGOLHook_SetCallOriginal(LPVOID hook, BOOL callOriginal) {
    ClearErr();
    HookEntry* e = FindByHandle(hook);
    if (!e) { SetErr("ERR: INVALID_HANDLE"); return FALSE; }
    e->callOriginal = callOriginal;
    return TRUE;
}

// 元関数を呼ばない設定のときに、呼び出し元へ返す値を決める。
// 整数・ポインタ（RAX で返る値）だけが対象。
//   浮動小数点は別のレジスタで返るため、この値は使われない。
extern "C" __declspec(dllexport)
BOOL NGOLHook_SetReturnValue(LPVOID hook, LONGLONG value) {
    ClearErr();
    HookEntry* e = FindByHandle(hook);
    if (!e) { SetErr("ERR: INVALID_HANDLE"); return FALSE; }
    e->returnValue = value;
    return TRUE;
}

// count: レジスタ4個(a0-a3)を超える追加引数の個数(0-8)。x64ではこの分はスタック経由で渡される。
// call_original=true 時、trampoline呼び出しをこの個数に応じた正しい引数個数の関数ポインタ型で行うようになる。
// 呼ばなければ従来通り extraStackArgs=0（4引数のみ転送、既存動作と完全互換）。
extern "C" __declspec(dllexport)
BOOL NGOLHook_SetExtraStackArgs(LPVOID hook, int count) {
    ClearErr();
    if (count < 0 || count > 8) { SetErr("ERR: INVALID_EXTRA_COUNT (must be 0-8)"); return FALSE; }
    HookEntry* e = FindByHandle(hook);
    if (!e) { SetErr("ERR: INVALID_HANDLE"); return FALSE; }
    // XMM対応フック(floatSlotMask!=0)は追加スタック引数と併用非対応（設計上の制約）
    if (count > 0 && e->floatSlotMask != 0) {
        SetErr("ERR: FLOAT_HOOK_NO_EXTRA_STACK_ARGS (floatSlotMask and extraStackArgs cannot combine)");
        return FALSE;
    }
    e->extraStackArgs = count;
    return TRUE;
}

// 直近フック発火時に捕捉した追加引数（第5引数以降）を pBuf へ読み出す。
// bufCount は pBuf の要素数上限（最大8まで読む）。無効ハンドルの場合は全て0で埋める。
extern "C" __declspec(dllexport)
void NGOLHook_ReadExtra(LPVOID hook, LONGLONG* pBuf, int bufCount) {
    if (!pBuf || bufCount <= 0) return;
    int n = bufCount > 8 ? 8 : bufCount;
    HookEntry* e = FindByHandle(hook);
    for (int i = 0; i < n; i++) {
        pBuf[i] = e ? e->extra[i] : 0;
    }
}

// callbackFnPtr の呼び出し規約: void(__cdecl*)(LPVOID hook, LPVOID a0, LPVOID a1, LPVOID a2, LPVOID a3)
// nullptr を渡すと解除。呼び出し元（C#側）は例外をこの境界の外へ絶対に伝播させないこと。
extern "C" __declspec(dllexport)
BOOL NGOLHook_SetManagedCallback(LPVOID hook, LPVOID callbackFnPtr) {
    ClearErr();
    HookEntry* e = FindByHandle(hook);
    if (!e) { SetErr("ERR: INVALID_HANDLE"); return FALSE; }
    e->managedCallback = callbackFnPtr;
    return TRUE;
}

// ------------------------------------------------------------
// 発火のたびに 1 件ずつ書く貸し先
// ------------------------------------------------------------
// この DLL が持つのは「貸された所へ 1 件ずつ順に書く」ことだけ。端まで来たら先頭へ戻る
// （hot path で確保できない以上これしか選べない。止まる方式にすると止まった後が全部消える）。
// 積み方・読み方・並べ替え・何件消えたと数えるかは呼ぶ側の仕事で、後入れ先出しで見たい
// なら通し番号の降順に読めばよく、この DLL に足すものは無い。読む口も持たない。

// 段数を頼んだときの 1 件のバイト数。呼ぶ側が置き場の寸法を出すのに使う。
// frames=0 なら決まった 6 欄ぶん。
extern "C" __declspec(dllexport)
int NGOLHook_RecordSize(int frames) {
    if (frames < 0) frames = 0;
    if (frames > HOOK_MAX_FRAMES) frames = HOOK_MAX_FRAMES;
    return (int)((HOOK_RECORD_FIXED_FIELDS + frames) * sizeof(LONGLONG));
}

// 置き場を貸す。buffer が null または capacity が 0 なら貸すのをやめる。
// capacity は 2 の冪に限る（hot path の位置決めを and 1 つで済ませるため）。
//
// pFirstSeq には「これ以降に書かれる最初の通し番号」を返す。設置から貸すまでの間に
// 発火した分は記録されていないので、呼ぶ側はこの番号から読み始める。
// 貸すのと同じ鍵の中で決めるので、その間に発火してもずれない。
extern "C" __declspec(dllexport)
BOOL NGOLHook_SetRecordBuffer(LPVOID hook, LPVOID buffer, int capacity, int frames, LONGLONG* pFirstSeq) {
    ClearErr();
    if (pFirstSeq) *pFirstSeq = 0;
    if (!g_csInit) { SetErr("ERR: NOT_INITIALIZED"); return FALSE; }
    EnterCriticalSection(&g_cs);
    HookEntry* e = FindByHandle(hook);
    if (!e) {
        LeaveCriticalSection(&g_cs);
        SetErr("ERR: INVALID_HANDLE");
        return FALSE;
    }
    if (!buffer || capacity <= 0) {
        e->records        = nullptr;
        e->recordMask     = 0;
        e->recordCapacity = 0;
        e->recordStride   = HOOK_RECORD_FIXED_FIELDS;
        e->frames         = 0;
        LeaveCriticalSection(&g_cs);
        return TRUE;
    }
    if (frames < 0) frames = 0;
    if (frames > HOOK_MAX_FRAMES) {
        LeaveCriticalSection(&g_cs);
        SetErr("ERR: TOO_MANY_FRAMES");
        return FALSE;
    }
    if ((capacity & (capacity - 1)) != 0) {
        LeaveCriticalSection(&g_cs);
        SetErr("ERR: CAPACITY_NOT_POW2");
        return FALSE;
    }
    if (capacity > (1 << 20)) {
        LeaveCriticalSection(&g_cs);
        SetErr("ERR: CAPACITY_TOO_LARGE");
        return FALSE;
    }
    SIZE_T bytes = (SIZE_T)capacity * (SIZE_T)NGOLHook_RecordSize(frames);
    // 書ける置き場かをここで一度だけ確かめる。hot path では確かめない。
    // 端まで通しで書けることを見る（VirtualQuery の RegionSize は領域の先頭からなので、
    // buffer の位置ぶんを差し引く）。
    MEMORY_BASIC_INFORMATION mbi;
    if (!VirtualQuery(buffer, &mbi, sizeof(mbi)) || mbi.State != MEM_COMMIT) {
        LeaveCriticalSection(&g_cs);
        SetErr("ERR: BUFFER_NOT_COMMITTED");
        return FALSE;
    }
    DWORD prot = mbi.Protect & ~(DWORD)(PAGE_GUARD | PAGE_NOCACHE | PAGE_WRITECOMBINE);
    if (prot != PAGE_READWRITE && prot != PAGE_EXECUTE_READWRITE) {
        LeaveCriticalSection(&g_cs);
        SetErr("ERR: BUFFER_NOT_WRITABLE");
        return FALSE;
    }
    SIZE_T avail = (SIZE_T)((BYTE*)mbi.BaseAddress + mbi.RegionSize - (BYTE*)buffer);
    if (avail < bytes) {
        LeaveCriticalSection(&g_cs);
        SetErr("ERR: BUFFER_TOO_SMALL");
        return FALSE;
    }
    // 先に外してから均す。均している最中に書かれると通し番号が混ざる。
    e->records = nullptr;
    memset(buffer, 0, bytes);
    e->recordCapacity = capacity;
    e->recordMask     = capacity - 1;
    e->recordStride   = HOOK_RECORD_FIXED_FIELDS + frames;
    e->frames         = frames;
    e->records        = reinterpret_cast<HookRecord*>(buffer);
    if (pFirstSeq) *pFirstSeq = e->count + 1;
    LeaveCriticalSection(&g_cs);
    return TRUE;
}

// ============================================================
// SEH 保護メモリ読み取り
// ============================================================
extern "C" __declspec(dllexport)
BOOL NGOLMem_ReadQWORD(LPVOID pAddr, LONGLONG* pValue) {
    if (!pAddr || !pValue) return FALSE;
    __try {
        *pValue = *reinterpret_cast<LONGLONG*>(pAddr);
        return TRUE;
    }
    __except(EXCEPTION_EXECUTE_HANDLER) { return FALSE; }
}

extern "C" __declspec(dllexport)
BOOL NGOLMem_ReadDWORD(LPVOID pAddr, DWORD* pValue) {
    if (!pAddr || !pValue) return FALSE;
    __try {
        *pValue = *reinterpret_cast<DWORD*>(pAddr);
        return TRUE;
    }
    __except(EXCEPTION_EXECUTE_HANDLER) { return FALSE; }
}

extern "C" __declspec(dllexport)
BOOL NGOLMem_ReadBytes(LPVOID pAddr, BYTE* pBuf, SIZE_T len) {
    if (!pAddr || !pBuf || len == 0) return FALSE;
    __try {
        memcpy(pBuf, pAddr, len);
        return TRUE;
    }
    __except(EXCEPTION_EXECUTE_HANDLER) { return FALSE; }
}

extern "C" __declspec(dllexport)
BOOL NGOLMem_WriteQWORD(LPVOID pAddr, LONGLONG value) {
    if (!pAddr) return FALSE;
    __try {
        *reinterpret_cast<LONGLONG*>(pAddr) = value;
        return TRUE;
    }
    __except(EXCEPTION_EXECUTE_HANDLER) { return FALSE; }
}

extern "C" __declspec(dllexport)
BOOL NGOLMem_WriteBytes(LPVOID pAddr, const BYTE* pBuf, SIZE_T len) {
    if (!pAddr || !pBuf || len == 0) return FALSE;
    __try {
        memcpy(pAddr, pBuf, len);
        return TRUE;
    }
    __except(EXCEPTION_EXECUTE_HANDLER) { return FALSE; }
}

extern "C" __declspec(dllexport)
BOOL NGOLMem_IsReadable(LPVOID pAddr, SIZE_T len) {
    if (!pAddr || len == 0) return FALSE;
    MEMORY_BASIC_INFORMATION mbi;
    if (!VirtualQuery(pAddr, &mbi, sizeof(mbi))) return FALSE;
    if (mbi.State != MEM_COMMIT) return FALSE;
    DWORD protect = mbi.Protect & ~(PAGE_GUARD | PAGE_NOCACHE | PAGE_WRITECOMBINE);
    return (protect == PAGE_READONLY        ||
            protect == PAGE_READWRITE       ||
            protect == PAGE_WRITECOPY       ||
            protect == PAGE_EXECUTE_READ    ||
            protect == PAGE_EXECUTE_READWRITE ||
            protect == PAGE_EXECUTE_WRITECOPY);
}

// ============================================================
// バックトレース
// ============================================================
extern "C" __declspec(dllexport)
DWORD NGOLDbg_StackTrace(LPVOID* pFrames, DWORD maxFrames) {
    if (!pFrames || maxFrames == 0) return 0;
    return RtlCaptureStackBackTrace(1, maxFrames, pFrames, nullptr);
}

// ============================================================
// klass 名読み取り
// ============================================================
extern "C" __declspec(dllexport)
BOOL NGOLKlass_GetName(LPVOID pObj,
                        char* nameBuf,  SIZE_T nameBufLen,
                        char* nsBuf,    SIZE_T nsBufLen) {
    if (!pObj || !nameBuf || !nsBuf) return FALSE;
    if (nameBufLen > 0) nameBuf[0] = '\0';
    if (nsBufLen   > 0) nsBuf[0]   = '\0';
    __try {
        // object: [0x00]=klass*, klass:[0x10]=name(char*), [0x18]=namespace(char*)
        LPVOID klass = *reinterpret_cast<LPVOID*>(pObj);
        if (!klass) return FALSE;
        const char* name = *reinterpret_cast<const char**>(reinterpret_cast<BYTE*>(klass) + 0x10);
        const char* ns   = *reinterpret_cast<const char**>(reinterpret_cast<BYTE*>(klass) + 0x18);
        if (name && nameBufLen > 0) strncpy_s(nameBuf, nameBufLen, name, _TRUNCATE);
        if (ns   && nsBufLen   > 0) strncpy_s(nsBuf,   nsBufLen,   ns,   _TRUNCATE);
        return TRUE;
    }
    __except(EXCEPTION_EXECUTE_HANDLER) { return FALSE; }
}

// ============================================================
// DllMain
// ============================================================
BOOL APIENTRY DllMain(HMODULE, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_DETACH && g_mhInit) {
        NGOLHook_UninstallAll();
        NgolBackend_Shutdown();
    }
    return TRUE;
}
