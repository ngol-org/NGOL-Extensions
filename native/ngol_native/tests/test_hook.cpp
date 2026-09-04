#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdio>
#include <cstring>

extern "C" {
    const char* NGOL_GetLastError();
    BOOL  NGOLHook_Install(LPVOID pTarget, LPVOID* pHook);
    BOOL  NGOLHook_Uninstall(LPVOID hook);
    void  NGOLHook_UninstallAll();
    void  NGOLHook_Read(LPVOID hook, LONGLONG* pCount,
                        LONGLONG* pA0, LONGLONG* pA1, LONGLONG* pA2, LONGLONG* pA3);
    void  NGOLHook_ResetCount(LPVOID hook);
    LPVOID NGOLHook_GetTrampoline(LPVOID hook);
    BOOL  NGOLHook_IsActive(LPVOID hook);
    BOOL  NGOLHook_SetCallOriginal(LPVOID hook, BOOL callOriginal);
    BOOL  NGOLHook_SetReturnValue(LPVOID hook, LONGLONG value);
    BOOL  NGOLHook_SetManagedCallback(LPVOID hook, LPVOID callbackFnPtr);
    BOOL  NGOLHook_SetExtraStackArgs(LPVOID hook, int count);
    void  NGOLHook_ReadExtra(LPVOID hook, LONGLONG* pBuf, int bufCount);
    BOOL  NGOLHook_InstallTyped(LPVOID pTarget, int floatSlotMask, LPVOID* pHook);
    void  NGOLHook_ReadReturnAddress(LPVOID hook, LONGLONG* pReturnAddress);
    DWORD NGOLDbg_StackTrace(LPVOID* pFrames, DWORD maxFrames);
    int   NGOLHook_RecordSize(int frames);
    BOOL  NGOLHook_SetRecordBuffer(LPVOID hook, LPVOID buffer, int capacity, int frames, LONGLONG* pFirstSeq);
}

static int g_pass = 0, g_fail = 0;

#define ASSERT_TRUE(expr) \
    do { if (!(expr)) { printf("  FAIL: %s (line %d)\n", #expr, __LINE__); g_fail++; } \
         else         { printf("  pass: %s\n", #expr); g_pass++; } } while(0)

#define ASSERT_STR_CONTAINS(haystack, needle) \
    do { if (!strstr((haystack), (needle))) { \
             printf("  FAIL: \"%s\" not in \"%s\" (line %d)\n", needle, haystack, __LINE__); g_fail++; } \
         else { printf("  pass: GetLastError contains \"%s\"\n", needle); g_pass++; } } while(0)

// フックの標的にする関数は必ずインライン化を禁止する。
//    最適化が有効だと呼び出しが展開されて消え、その番地にフックを張っても発火しない。
//    症状は「フックの設置は成功するのにヒット数が増えない」で、実装の不具合と見分けがつかない。
static volatile int g_originalCalled = 0;

static __declspec(noinline) int __cdecl DummyFunc(int a, int b) {
    g_originalCalled++;
    return a + b;
}

// 8引数（レジスタ4個+スタック4個）のダミー関数。
// extraStackArgs はポインタサイズ値のみを前提とするため引数もLONGLONGで揃える。
static volatile int g_dummy8CallCount = 0;
static volatile LONGLONG g_dummy8Sum = 0;

static __declspec(noinline) LONGLONG __cdecl DummyFunc8(LONGLONG a, LONGLONG b, LONGLONG c, LONGLONG d,
                                    LONGLONG e, LONGLONG f, LONGLONG g, LONGLONG h) {
    g_dummy8CallCount++;
    g_dummy8Sum = a + b + c + d + e + f + g + h;
    return g_dummy8Sum;
}

// GP/XMM混在引数のダミー関数（スロット0=GP, スロット1=XMM, スロット2=GP, スロット3=XMM
// floatSlotMask=0b1010(=10) に対応）。
static volatile int      g_dummyMixedCallCount = 0;
static volatile LONGLONG g_dummyMixedA0 = 0;
static volatile double   g_dummyMixedB1 = 0;
static volatile LONGLONG g_dummyMixedA2 = 0;
static volatile double   g_dummyMixedB3 = 0;

static __declspec(noinline) void __cdecl DummyFuncMixed(LPVOID a0, double b1, LPVOID a2, double b3) {
    g_dummyMixedCallCount++;
    g_dummyMixedA0 = (LONGLONG)a0;
    g_dummyMixedB1 = b1;
    g_dummyMixedA2 = (LONGLONG)a2;
    g_dummyMixedB3 = b3;
}

static void test_GetLastError_initial() {
    printf("[test] NGOL_GetLastError initial state\n");
    ASSERT_TRUE(strcmp(NGOL_GetLastError(), "") == 0);
}

static void test_Install_success() {
    printf("[test] NGOLHook_Install success\n");
    LPVOID hook = nullptr;
    BOOL ok = NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook);
    ASSERT_TRUE(ok == TRUE);
    ASSERT_TRUE(hook != nullptr);
    ASSERT_TRUE(NGOLHook_IsActive(hook) == TRUE);
    NGOLHook_Uninstall(hook);
}

static void test_Install_null_fails() {
    printf("[test] NGOLHook_Install null address\n");
    LPVOID hook = nullptr;
    BOOL ok = NGOLHook_Install(nullptr, &hook);
    ASSERT_TRUE(ok == FALSE);
    ASSERT_STR_CONTAINS(NGOL_GetLastError(), "ERR:");
}

static void test_Install_padding_detected() {
    printf("[test] NGOLHook_Install 0x00 padding detection\n");
    static BYTE paddingBuf[16] = { 0x00, 0x90, 0x90, 0xC3 };
    LPVOID hook = nullptr;
    BOOL ok = NGOLHook_Install(reinterpret_cast<LPVOID>(paddingBuf), &hook);
    ASSERT_TRUE(ok == FALSE);
    ASSERT_STR_CONTAINS(NGOL_GetLastError(), "RVA_PADDING");
}

static void test_IsActive_after_uninstall_false() {
    printf("[test] NGOLHook_IsActive after uninstall\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook);
    NGOLHook_Uninstall(hook);
    ASSERT_TRUE(NGOLHook_IsActive(hook) == FALSE);
}

static void test_Read_count_after_fire() {
    printf("[test] NGOLHook_Read count after hook fire\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook);

    DummyFunc(10, 20);
    DummyFunc(30, 40);

    LONGLONG count = 0, a0 = 0, a1 = 0, a2 = 0, a3 = 0;
    NGOLHook_Read(hook, &count, &a0, &a1, &a2, &a3);
    ASSERT_TRUE(count == 2);

    NGOLHook_Uninstall(hook);
}

// 設置しただけの状態で対象を呼ぶと、元関数が実行されること。
//
// 見張るだけのつもりの設置が、既定で対象を止めてしまってはいけない。
// 呼ぶ側が SetCallOriginal(TRUE) を呼ぶまでの間にも対象は呼ばれる。
// 確保系の関数なら、その間の呼び出し元は NULL を受け取って落ちる。
static void test_Install_defaults_to_calling_the_original() {
    printf("[test] 設置しただけで元関数が呼ばれる（既定は止めない）\n");
    LPVOID hook = nullptr;
    ASSERT_TRUE(NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook) == TRUE);

    g_originalCalled = 0;
    const LONGLONG got = DummyFunc(3, 4);
    printf("  -> 元関数が呼ばれた回数: %d / 戻り値: %lld\n", g_originalCalled, got);
    ASSERT_TRUE(g_originalCalled == 1);
    ASSERT_TRUE(got == 3 + 4);      // 戻り値も素通しであること

    NGOLHook_Uninstall(hook);
}

static void test_SetCallOriginal_false_blocks() {
    printf("[test] NGOLHook_SetCallOriginal FALSE blocks original\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook);
    NGOLHook_SetCallOriginal(hook, FALSE);

    g_originalCalled = 0;
    DummyFunc(1, 2);
    ASSERT_TRUE(g_originalCalled == 0);

    NGOLHook_Uninstall(hook);
}

static void test_SetCallOriginal_true_calls_original() {
    printf("[test] NGOLHook_SetCallOriginal TRUE calls original\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook);
    NGOLHook_SetCallOriginal(hook, TRUE);

    g_originalCalled = 0;
    DummyFunc(1, 2);
    ASSERT_TRUE(g_originalCalled == 1);

    NGOLHook_Uninstall(hook);
}

// 元関数を呼ばない設定では、呼び出し元は「実行されなかった」ことを知らないまま
//    戻り値を受け取る。返す値が決まっていないと不定値が渡るため、そこを固定する。
static void test_SetReturnValue_used_when_blocked() {
    printf("[test] NGOLHook_SetReturnValue is returned while the original is blocked\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook);
    NGOLHook_SetCallOriginal(hook, FALSE);
    ASSERT_TRUE(NGOLHook_SetReturnValue(hook, 12345) == TRUE);

    g_originalCalled = 0;
    int got = DummyFunc(1, 2);            // 元関数なら 3 を返すはず
    ASSERT_TRUE(g_originalCalled == 0);
    ASSERT_TRUE(got == 12345);

    NGOLHook_Uninstall(hook);
}

// 設定しなかった場合に不定値が返らないことを確かめる（設置時に 0 で初期化している）。
static void test_blocked_return_defaults_to_zero() {
    printf("[test] blocked call returns 0 when no return value was set\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook);
    NGOLHook_SetCallOriginal(hook, FALSE);

    g_originalCalled = 0;
    int got = DummyFunc(7, 8);
    ASSERT_TRUE(g_originalCalled == 0);
    ASSERT_TRUE(got == 0);

    NGOLHook_Uninstall(hook);
}

// 元関数を呼ぶ設定では、その戻り値がそのまま呼び出し元へ渡ること。
static void test_original_return_value_passes_through() {
    printf("[test] the original's return value reaches the caller when it is called\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook);
    NGOLHook_SetCallOriginal(hook, TRUE);
    NGOLHook_SetReturnValue(hook, 999);   // 元関数を呼ぶ側では使われないこと

    g_originalCalled = 0;
    int got = DummyFunc(3, 4);
    ASSERT_TRUE(g_originalCalled == 1);
    ASSERT_TRUE(got == 7);

    NGOLHook_Uninstall(hook);
}

static void test_SetReturnValue_invalid_handle() {
    printf("[test] NGOLHook_SetReturnValue rejects an unknown handle\n");
    ASSERT_TRUE(NGOLHook_SetReturnValue(reinterpret_cast<LPVOID>(0x1234), 1) == FALSE);
    ASSERT_STR_CONTAINS(NGOL_GetLastError(), "INVALID_HANDLE");
}

static void test_double_install_already_hooked() {
    printf("[test] NGOLHook_Install double install\n");
    LPVOID hook1 = nullptr, hook2 = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook1);
    BOOL ok = NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook2);
    ASSERT_TRUE(ok == FALSE);
    ASSERT_STR_CONTAINS(NGOL_GetLastError(), "ALREADY_HOOKED");
    NGOLHook_Uninstall(hook1);
}

static void test_ResetCount() {
    printf("[test] NGOLHook_ResetCount\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook);
    DummyFunc(1, 2);
    NGOLHook_ResetCount(hook);
    LONGLONG count = -1, a0=0,a1=0,a2=0,a3=0;
    NGOLHook_Read(hook, &count, &a0, &a1, &a2, &a3);
    ASSERT_TRUE(count == 0);
    NGOLHook_Uninstall(hook);
}

static void test_UninstallAll() {
    printf("[test] NGOLHook_UninstallAll\n");
    LPVOID h1 = nullptr, h2 = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &h1);
    NGOLHook_UninstallAll();
    ASSERT_TRUE(NGOLHook_IsActive(h1) == FALSE);
}

static void test_GetTrampoline_non_null() {
    printf("[test] NGOLHook_GetTrampoline non-null\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook);
    ASSERT_TRUE(NGOLHook_GetTrampoline(hook) != nullptr);
    NGOLHook_Uninstall(hook);
}

// マネージドコールバック機構のテスト（C++側のみ、疑似コールバックで検証）
static volatile int g_callbackFireCount = 0;
static volatile LONGLONG g_callbackLastA0 = 0, g_callbackLastA1 = 0;
static volatile LPVOID g_callbackObservedHook = nullptr;

static void __cdecl FakeManagedCallback(LPVOID hook, LPVOID a0, LPVOID a1, LPVOID a2, LPVOID a3) {
    (void)a2; (void)a3;
    g_callbackFireCount++;
    g_callbackLastA0 = reinterpret_cast<LONGLONG>(a0);
    g_callbackLastA1 = reinterpret_cast<LONGLONG>(a1);
    g_callbackObservedHook = hook;
}

static void test_SetManagedCallback_fires_with_args() {
    printf("[test] NGOLHook_SetManagedCallback fires with hook+args\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook);
    NGOLHook_SetCallOriginal(hook, TRUE);
    NGOLHook_SetManagedCallback(hook, reinterpret_cast<LPVOID>(FakeManagedCallback));

    g_callbackFireCount = 0;
    g_originalCalled = 0;
    DummyFunc(111, 222);

    ASSERT_TRUE(g_callbackFireCount == 1);
    ASSERT_TRUE(g_callbackLastA0 == 111);
    ASSERT_TRUE(g_callbackLastA1 == 222);
    ASSERT_TRUE(g_callbackObservedHook == hook);
    ASSERT_TRUE(g_originalCalled == 1); // callOriginal=TRUEなのでコールバック後に元関数も呼ばれる

    NGOLHook_Uninstall(hook);
}

static void test_SetManagedCallback_null_clears() {
    printf("[test] NGOLHook_SetManagedCallback(nullptr) clears callback\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook);
    NGOLHook_SetManagedCallback(hook, reinterpret_cast<LPVOID>(FakeManagedCallback));
    NGOLHook_SetManagedCallback(hook, nullptr);

    g_callbackFireCount = 0;
    DummyFunc(1, 2);
    ASSERT_TRUE(g_callbackFireCount == 0);

    NGOLHook_Uninstall(hook);
}

static void test_SetManagedCallback_invalid_handle_fails() {
    printf("[test] NGOLHook_SetManagedCallback invalid handle\n");
    BOOL ok = NGOLHook_SetManagedCallback(nullptr, reinterpret_cast<LPVOID>(FakeManagedCallback));
    ASSERT_TRUE(ok == FALSE);
    ASSERT_STR_CONTAINS(NGOL_GetLastError(), "INVALID_HANDLE");
}

static void test_Uninstall_clears_managed_callback_slot_reuse() {
    printf("[test] managed callback does not leak into a reused slot\n");
    LPVOID hook1 = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook1);
    NGOLHook_SetManagedCallback(hook1, reinterpret_cast<LPVOID>(FakeManagedCallback));
    NGOLHook_Uninstall(hook1);

    // 同じスロットが再利用されても managedCallback は Install() 時に nullptr へ初期化される
    LPVOID hook2 = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook2);
    NGOLHook_SetCallOriginal(hook2, TRUE);

    g_callbackFireCount = 0;
    DummyFunc(1, 2);
    ASSERT_TRUE(g_callbackFireCount == 0);

    NGOLHook_Uninstall(hook2);
}

static void test_ExtraStackArgs_forwards_all_args_to_original() {
    printf("[test] NGOLHook_SetExtraStackArgs(4): call_original forwards all 8 args correctly\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc8), &hook);
    ASSERT_TRUE(NGOLHook_SetExtraStackArgs(hook, 4) == TRUE); // 4(register) + 4(stack) = 8 total
    NGOLHook_SetCallOriginal(hook, TRUE);

    g_dummy8Sum = 0;
    // HookImpl側はvoidのため戻り値(RAX)は検証しない。DummyFunc8内部の副作用(g_dummy8Sum)のみで
    // trampoline(元関数)がcall_original経由で正しい8引数を受け取ったことを確認する。
    DummyFunc8(1, 2, 3, 4, 5, 6, 7, 8);
    ASSERT_TRUE(g_dummy8Sum == 36);

    LONGLONG extra[4] = { -1, -1, -1, -1 };
    NGOLHook_ReadExtra(hook, extra, 4);
    ASSERT_TRUE(extra[0] == 5);
    ASSERT_TRUE(extra[1] == 6);
    ASSERT_TRUE(extra[2] == 7);
    ASSERT_TRUE(extra[3] == 8);

    NGOLHook_Uninstall(hook);
}

static void test_ExtraStackArgs_default_zero_still_fires_without_crash() {
    printf("[test] extraStackArgs default(0): hook still fires original without crashing (5th-8th args are undefined, known limitation)\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc8), &hook);
    NGOLHook_SetCallOriginal(hook, TRUE);
    // NGOLHook_SetExtraStackArgs を呼ばない -> デフォルト0のまま（後方互換の回帰確認）

    g_dummy8CallCount = 0;
    DummyFunc8(1, 2, 3, 4, 5, 6, 7, 8);
    ASSERT_TRUE(g_dummy8CallCount == 1); // 5〜8引数目の値は不定だが、クラッシュせず元関数は呼ばれる

    NGOLHook_Uninstall(hook);
}

static void test_SetExtraStackArgs_out_of_range_fails() {
    printf("[test] NGOLHook_SetExtraStackArgs out-of-range count fails\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc8), &hook);
    ASSERT_TRUE(NGOLHook_SetExtraStackArgs(hook, 9) == FALSE);
    ASSERT_STR_CONTAINS(NGOL_GetLastError(), "INVALID_EXTRA_COUNT");
    ASSERT_TRUE(NGOLHook_SetExtraStackArgs(hook, -1) == FALSE);
    NGOLHook_Uninstall(hook);
}

static void test_InstallTyped_mixed_float_slots_forwards_correctly() {
    printf("[test] NGOLHook_InstallTyped floatSlotMask=0b1010: call_original forwards GP+XMM args correctly\n");
    LPVOID hook = nullptr;
    BOOL ok = NGOLHook_InstallTyped(reinterpret_cast<LPVOID>(DummyFuncMixed), 0b1010, &hook);
    ASSERT_TRUE(ok == TRUE);
    ASSERT_TRUE(hook != nullptr);
    NGOLHook_SetCallOriginal(hook, TRUE);

    g_dummyMixedCallCount = 0;
    DummyFuncMixed(reinterpret_cast<LPVOID>(static_cast<LONG_PTR>(0x1234)), 3.5,
                    reinterpret_cast<LPVOID>(static_cast<LONG_PTR>(0x5678)), 7.25);

    ASSERT_TRUE(g_dummyMixedCallCount == 1);
    ASSERT_TRUE(g_dummyMixedA0 == 0x1234);
    ASSERT_TRUE(g_dummyMixedB1 == 3.5);
    ASSERT_TRUE(g_dummyMixedA2 == 0x5678);
    ASSERT_TRUE(g_dummyMixedB3 == 7.25);

    // last_a1/a3 として捕捉されたビットパターンをdoubleとして再解釈しても一致することを確認
    LONGLONG count = 0, a0 = 0, a1 = 0, a2 = 0, a3 = 0;
    NGOLHook_Read(hook, &count, &a0, &a1, &a2, &a3);
    double capturedB1, capturedB3;
    memcpy(&capturedB1, &a1, sizeof(double));
    memcpy(&capturedB3, &a3, sizeof(double));
    ASSERT_TRUE(a0 == 0x1234);
    ASSERT_TRUE(capturedB1 == 3.5);
    ASSERT_TRUE(a2 == 0x5678);
    ASSERT_TRUE(capturedB3 == 7.25);

    NGOLHook_Uninstall(hook);
}

static void test_Install_default_still_misreads_float_slot_known_limitation() {
    printf("[test] plain NGOLHook_Install (no float typing): still fires without crash on a float-arg function (known limitation)\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFuncMixed), &hook);
    NGOLHook_SetCallOriginal(hook, TRUE);

    g_dummyMixedCallCount = 0;
    DummyFuncMixed(reinterpret_cast<LPVOID>(static_cast<LONG_PTR>(0x1234)), 3.5,
                    reinterpret_cast<LPVOID>(static_cast<LONG_PTR>(0x5678)), 7.25);
    ASSERT_TRUE(g_dummyMixedCallCount == 1); // XMMスロットの値は不定だが、クラッシュせず元関数は呼ばれる

    NGOLHook_Uninstall(hook);
}

static void test_InstallTyped_invalid_mask_fails() {
    printf("[test] NGOLHook_InstallTyped rejects out-of-range floatSlotMask\n");
    LPVOID hook = nullptr;
    ASSERT_TRUE(NGOLHook_InstallTyped(reinterpret_cast<LPVOID>(DummyFuncMixed), 16, &hook) == FALSE);
    ASSERT_STR_CONTAINS(NGOL_GetLastError(), "INVALID_FLOAT_SLOT_MASK");
    ASSERT_TRUE(NGOLHook_InstallTyped(reinterpret_cast<LPVOID>(DummyFuncMixed), -1, &hook) == FALSE);
}

static void test_SetExtraStackArgs_rejected_on_float_hook() {
    printf("[test] NGOLHook_SetExtraStackArgs rejects nonzero count on a floatSlotMask hook\n");
    LPVOID hook = nullptr;
    NGOLHook_InstallTyped(reinterpret_cast<LPVOID>(DummyFuncMixed), 0b1010, &hook);
    ASSERT_TRUE(NGOLHook_SetExtraStackArgs(hook, 1) == FALSE);
    ASSERT_STR_CONTAINS(NGOL_GetLastError(), "FLOAT_HOOK_NO_EXTRA_STACK_ARGS");
    ASSERT_TRUE(NGOLHook_SetExtraStackArgs(hook, 0) == TRUE); // count=0の再設定は許可される
    NGOLHook_Uninstall(hook);
}

// ============================================================
// 呼び出し元 / 呼び出しの並び
// ============================================================
// 呼び出し元の検証は、答えが独立に確かめられる形にする。
// 決まった 1 か所からだけ呼ぶ関数を用意し、戻り番地がその関数の中を指すことを見る。
static __declspec(noinline) int CallDummyFromHere(int n) {
    int r = 0;
    for (int i = 0; i < n; i++) r += DummyFunc(i, 1);
    return r;
}

static bool WithinFunction(LONGLONG addr, void* fn, SIZE_T span) {
    BYTE* base = reinterpret_cast<BYTE*>(fn);
    BYTE* p    = reinterpret_cast<BYTE*>(addr);
    return p > base && p < base + span;
}

static void test_ReturnAddress_points_into_the_caller() {
    printf("[test] NGOLHook_ReadReturnAddress: 戻り番地が呼び出し元の中を指す\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook);
    NGOLHook_SetCallOriginal(hook, TRUE);
    CallDummyFromHere(1);
    LONGLONG ret = 0;
    NGOLHook_ReadReturnAddress(hook, &ret);
    ASSERT_TRUE(ret != 0);
    ASSERT_TRUE(WithinFunction(ret, reinterpret_cast<void*>(CallDummyFromHere), 0x200));
    NGOLHook_Uninstall(hook);
}

static void test_RecordSize_is_48() {
    printf("[test] NGOLHook_RecordSize: 段数 0 なら 48 バイト、8 段なら 112 バイト\n");
    ASSERT_TRUE(NGOLHook_RecordSize(0) == 48);
    ASSERT_TRUE(NGOLHook_RecordSize(8) == 48 + 8 * 8);
}

// 呼ぶ側の読み方。この DLL は読む口を持たない。貸した所は呼ぶ側のものなので、
// 自分で読む。ここでの読み手はノードが書くものと同じ形にしてある。
static const int FIELDS = 6;

static LONGLONG* AllocBuffer(int capacity) {
    return reinterpret_cast<LONGLONG*>(
        VirtualAlloc(nullptr, (SIZE_T)capacity * NGOLHook_RecordSize(0),
                     MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
}

// 目当ての 1 件を採る。書きかけ、または読んでいる最中に上書きされたら false。
static bool TryTake(LONGLONG* buf, int capacity, LONGLONG seq, LONGLONG* out6) {
    LONGLONG* r = buf + ((seq - 1) & (capacity - 1)) * FIELDS;
    if (r[0] != seq) return false;
    for (int k = 0; k < FIELDS; k++) out6[k] = r[k];
    return r[0] == seq && out6[0] == seq;
}

// 続きから読む。戻り値は採れた件数。pLost には上書きで消えた件数が入る。
static int DrainInto(LPVOID hook, LONGLONG* buf, int capacity,
                     LONGLONG firstSeq, LONGLONG* pNext, LONGLONG* out, int maxRecords,
                     LONGLONG* pLost) {
    LONGLONG count = 0, a0 = 0, a1 = 0, a2 = 0, a3 = 0;
    NGOLHook_Read(hook, &count, &a0, &a1, &a2, &a3);
    LONGLONG oldest = count - capacity + 1;
    if (oldest < firstSeq) oldest = firstSeq;
    *pLost = 0;
    if (*pNext < oldest) { *pLost = oldest - *pNext; *pNext = oldest; }
    int n = 0;
    while (*pNext <= count && n < maxRecords) {
        if (!TryTake(buf, capacity, *pNext, out + (SIZE_T)n * FIELDS)) break;
        n++;
        (*pNext)++;
    }
    return n;
}

static void test_Records_are_written_in_order() {
    printf("[test] 並びが飛ばずに残り、取りこぼしが 0 である\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook);
    NGOLHook_SetCallOriginal(hook, TRUE);
    LONGLONG* buf = AllocBuffer(8);
    LONGLONG firstSeq = 0;
    ASSERT_TRUE(NGOLHook_SetRecordBuffer(hook, buf, 8, 0, &firstSeq) == TRUE);
    ASSERT_TRUE(firstSeq == 1);

    CallDummyFromHere(5);

    LONGLONG out[8 * FIELDS] = {0};
    LONGLONG next = firstSeq, lost = -1;
    int n = DrainInto(hook, buf, 8, firstSeq, &next, out, 8, &lost);
    ASSERT_TRUE(n == 5);
    ASSERT_TRUE(lost == 0);
    ASSERT_TRUE(next == 6);
    bool seqOk = true, argOk = true, retOk = true;
    for (int i = 0; i < n; i++) {
        if (out[i * FIELDS + 0] != i + 1) seqOk = false;
        if (out[i * FIELDS + 2] != i)     argOk = false;
        if (!WithinFunction(out[i * FIELDS + 1], reinterpret_cast<void*>(CallDummyFromHere), 0x200)) retOk = false;
    }
    ASSERT_TRUE(seqOk);
    ASSERT_TRUE(argOk);
    ASSERT_TRUE(retOk);

    int n2 = DrainInto(hook, buf, 8, firstSeq, &next, out, 8, &lost);
    ASSERT_TRUE(n2 == 0);
    ASSERT_TRUE(lost == 0);

    NGOLHook_SetRecordBuffer(hook, nullptr, 0, 0, nullptr);
    NGOLHook_Uninstall(hook);
    VirtualFree(buf, 0, MEM_RELEASE);
}

static void test_Records_overwritten_are_countable() {
    // 陽性対照: 消えた件数が出せることを、わざと溢れさせて確かめる。
    // 0 が出るだけの検査にしない。
    printf("[test] 溢れたら消えた件数が数えられる（陽性対照）\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook);
    NGOLHook_SetCallOriginal(hook, TRUE);
    LONGLONG* buf = AllocBuffer(4);
    LONGLONG firstSeq = 0;
    NGOLHook_SetRecordBuffer(hook, buf, 4, 0, &firstSeq);

    CallDummyFromHere(10);

    LONGLONG out[4 * FIELDS] = {0};
    LONGLONG next = firstSeq, lost = 0;
    int n = DrainInto(hook, buf, 4, firstSeq, &next, out, 4, &lost);
    ASSERT_TRUE(lost == 6);
    ASSERT_TRUE(n == 4);
    ASSERT_TRUE(out[0] == 7);
    ASSERT_TRUE(out[3 * FIELDS] == 10);
    ASSERT_TRUE(next == 11);

    NGOLHook_SetRecordBuffer(hook, nullptr, 0, 0, nullptr);
    NGOLHook_Uninstall(hook);
    VirtualFree(buf, 0, MEM_RELEASE);
}

static void test_Records_stop_after_detach() {
    printf("[test] 外したら記録が止まる（回数は数え続ける）\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook);
    NGOLHook_SetCallOriginal(hook, TRUE);
    LONGLONG* buf = AllocBuffer(8);
    LONGLONG firstSeq = 0;
    NGOLHook_SetRecordBuffer(hook, buf, 8, 0, &firstSeq);
    CallDummyFromHere(2);
    ASSERT_TRUE(NGOLHook_SetRecordBuffer(hook, nullptr, 0, 0, nullptr) == TRUE);
    CallDummyFromHere(3);

    LONGLONG out[8 * FIELDS] = {0};
    LONGLONG next = firstSeq + 2, lost = 0;   // 2 件は読み終えている前提
    ASSERT_TRUE(DrainInto(hook, buf, 8, firstSeq, &next, out, 8, &lost) == 0);
    LONGLONG count = 0, a0 = 0, a1 = 0, a2 = 0, a3 = 0;
    NGOLHook_Read(hook, &count, &a0, &a1, &a2, &a3);
    ASSERT_TRUE(count == 5);

    NGOLHook_Uninstall(hook);
    VirtualFree(buf, 0, MEM_RELEASE);
}

static void test_Buffer_rejects_bad_capacity() {
    printf("[test] 件数が 2 の冪でなければ断る\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook);
    LONGLONG* buf = AllocBuffer(8);
    ASSERT_TRUE(NGOLHook_SetRecordBuffer(hook, buf, 6, 0, nullptr) == FALSE);
    ASSERT_STR_CONTAINS(NGOL_GetLastError(), "CAPACITY_NOT_POW2");
    NGOLHook_Uninstall(hook);
    VirtualFree(buf, 0, MEM_RELEASE);
}

static void test_Buffer_rejects_unwritable() {
    printf("[test] 書けない置き場は断る\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook);
    LPVOID ro = VirtualAlloc(nullptr, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READONLY);
    ASSERT_TRUE(NGOLHook_SetRecordBuffer(hook, ro, 8, 0, nullptr) == FALSE);
    ASSERT_STR_CONTAINS(NGOL_GetLastError(), "BUFFER_NOT_WRITABLE");
    NGOLHook_Uninstall(hook);
    VirtualFree(ro, 0, MEM_RELEASE);
}

static void test_Buffer_rejects_too_small() {
    printf("[test] 寸法が足りない置き場は断る（はみ出す前に止まる）\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook);
    LONGLONG* buf = AllocBuffer(4);           // 4 件ぶんしかない
    ASSERT_TRUE(NGOLHook_SetRecordBuffer(hook, buf, 1024, 0, nullptr) == FALSE);
    ASSERT_STR_CONTAINS(NGOL_GetLastError(), "BUFFER_TOO_SMALL");
    NGOLHook_Uninstall(hook);
    VirtualFree(buf, 0, MEM_RELEASE);
}

static void test_FirstSeq_skips_calls_before_lending() {
    // 設置してから貸すまでの間に発火した分は記録されない。
    // その番号を待ち続けると 1 件も出なくなるので、貸した時点の番号が返ることを確かめる。
    printf("[test] 貸した時点の番号が返る（設置と貸しの間の発火で詰まらない）\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook);
    NGOLHook_SetCallOriginal(hook, TRUE);

    CallDummyFromHere(3);            // 貸す前に 3 回（記録されない）

    LONGLONG* buf = AllocBuffer(8);
    LONGLONG firstSeq = 0;
    NGOLHook_SetRecordBuffer(hook, buf, 8, 0, &firstSeq);
    ASSERT_TRUE(firstSeq == 4);

    CallDummyFromHere(2);            // 貸したあとに 2 回

    LONGLONG out[8 * FIELDS] = {0};
    LONGLONG next = firstSeq, lost = -1;
    int n = DrainInto(hook, buf, 8, firstSeq, &next, out, 8, &lost);
    ASSERT_TRUE(n == 2);
    ASSERT_TRUE(lost == 0);          // 貸す前の分は取りこぼしに数えない
    ASSERT_TRUE(out[0] == 4);
    ASSERT_TRUE(out[FIELDS] == 5);

    NGOLHook_SetRecordBuffer(hook, nullptr, 0, 0, nullptr);
    NGOLHook_Uninstall(hook);
    VirtualFree(buf, 0, MEM_RELEASE);
}

static void test_Frames_reach_the_caller() {
    // 段数を頼むと、記録に呼び出し元の連なりが入る。
    // 判定は段数ではなく「呼び出し元に届いたか」で行う。
    // 段数だけ見ると、フック機構の内側で足踏みしていても合格になる。
    printf("[test] 段数を頼むと記録に呼び出し元の連なりが入る\n");
    const int FR = 8;
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook);
    NGOLHook_SetCallOriginal(hook, TRUE);

    const int stride = NGOLHook_RecordSize(FR) / (int)sizeof(LONGLONG);
    LONGLONG* buf = reinterpret_cast<LONGLONG*>(
        VirtualAlloc(nullptr, (SIZE_T)8 * NGOLHook_RecordSize(FR),
                     MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    LONGLONG firstSeq = 0;
    ASSERT_TRUE(NGOLHook_SetRecordBuffer(hook, buf, 8, FR, &firstSeq) == TRUE);

    CallDummyFromHere(1);

    LONGLONG* r = buf;                    // 1 件目
    ASSERT_TRUE(r[0] == firstSeq);
    int nonZero = 0;
    for (int i = 0; i < FR; i++) {
        if (r[6 + i] != 0) nonZero++;
    }
    printf("  -> 埋まった段: %d / %d\n", nonZero, FR);
    ASSERT_TRUE(nonZero > 1);

    // 1 段目がいきなり呼び出し元であること。
    // ここが緩いと、手前で飛ばす段数がずれても「届いた」で通ってしまい、
    // 頼んだ段数のうち何段かがこの DLL の中身で埋まることに気づけない。
    ASSERT_TRUE(WithinFunction(r[6], reinterpret_cast<void*>(CallDummyFromHere), 0x200));
    ASSERT_TRUE(r[6] == r[1]);            // 戻り番地と同じ場所を指している

    NGOLHook_SetRecordBuffer(hook, nullptr, 0, 0, nullptr);
    NGOLHook_Uninstall(hook);
    VirtualFree(buf, 0, MEM_RELEASE);
}

static void test_Frames_rejected_above_limit() {
    printf("[test] 段数の上限を超えたら断る\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook);
    LONGLONG* buf = AllocBuffer(8);
    ASSERT_TRUE(NGOLHook_SetRecordBuffer(hook, buf, 8, 99, nullptr) == FALSE);
    ASSERT_STR_CONTAINS(NGOL_GetLastError(), "TOO_MANY_FRAMES");
    NGOLHook_Uninstall(hook);
    VirtualFree(buf, 0, MEM_RELEASE);
}

// フック本体の中から巻き戻すと何段返るかを測る。
//
// これは能力の確認であって、hot path には何も足していない。
// SetManagedCallback は関数ポインタを受け取るだけなので、ここではネイティブの関数を渡す。
// つまりこのコールバックは HookImpl_N の中から同期で呼ばれる。
//
// 差し替えは jmp なので、HookImpl_N の入口の [rsp] は「元の呼び出し元の戻り番地」。
// HookImpl_N 自身は .pdata を持つので、巻き戻しがそこを越えられるなら
// 呼び出し元（このファイル内の関数）まで辿れるはず。越えられなければ数段で止まる。
static LPVOID g_walkFrames[64];
static DWORD  g_walkCount = 0;

static void __cdecl WalkProbe(LPVOID, LPVOID, LPVOID, LPVOID, LPVOID)
{
    g_walkCount = NGOLDbg_StackTrace(g_walkFrames, 64);
}

static bool FramesContain(void* fn, SIZE_T span)
{
    BYTE* base = reinterpret_cast<BYTE*>(fn);
    for (DWORD i = 0; i < g_walkCount; i++)
    {
        BYTE* p = reinterpret_cast<BYTE*>(g_walkFrames[i]);
        if (p > base && p < base + span) return true;
    }
    return false;
}

static void test_NativeSideStackWalk_depth()
{
    printf("[test] フック本体の中から巻き戻すと何段返るか（能力の確認）\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook);
    NGOLHook_SetCallOriginal(hook, TRUE);
    NGOLHook_SetManagedCallback(hook, reinterpret_cast<LPVOID>(WalkProbe));

    g_walkCount = 0;
    CallDummyFromHere(1);

    printf("  -> %u 段返った\n", g_walkCount);
    for (DWORD i = 0; i < g_walkCount && i < 12; i++)
        printf("     %2u  %p\n", i, g_walkFrames[i]);

    // 判定は「段数」ではなく「呼び出し元に届いたか」で行う。
    // 段数だけ見ると、フック機構の内側で足踏みしていても合格になる。
    const bool reached = FramesContain(reinterpret_cast<void*>(CallDummyFromHere), 0x200);
    printf("  -> 呼び出し元 CallDummyFromHere に届いたか: %s\n", reached ? "はい" : "いいえ");
    ASSERT_TRUE(g_walkCount > 0);

    NGOLHook_SetManagedCallback(hook, nullptr);
    NGOLHook_Uninstall(hook);
}

static void test_Release_order_survives() {
    printf("[test] 外す・解除・解放の順を通してもプロセスが生きている\n");
    LPVOID hook = nullptr;
    NGOLHook_Install(reinterpret_cast<LPVOID>(DummyFunc), &hook);
    NGOLHook_SetCallOriginal(hook, TRUE);
    LONGLONG* buf = AllocBuffer(16);
    NGOLHook_SetRecordBuffer(hook, buf, 16, 0, nullptr);
    CallDummyFromHere(4);
    NGOLHook_SetRecordBuffer(hook, nullptr, 0, 0, nullptr);
    NGOLHook_Uninstall(hook);
    VirtualFree(buf, 0, MEM_RELEASE);
    ASSERT_TRUE(CallDummyFromHere(3) == 3 + 1 + 2);
}

int main() {
    printf("=== ngol_native C++ unit tests ===\n\n");

    test_GetLastError_initial();
    test_Install_success();
    test_Install_null_fails();
    test_Install_padding_detected();
    test_IsActive_after_uninstall_false();
    test_Read_count_after_fire();
    test_Install_defaults_to_calling_the_original();
    test_SetCallOriginal_false_blocks();
    test_SetCallOriginal_true_calls_original();
    test_SetReturnValue_used_when_blocked();
    test_blocked_return_defaults_to_zero();
    test_original_return_value_passes_through();
    test_SetReturnValue_invalid_handle();
    test_double_install_already_hooked();
    test_ResetCount();
    test_UninstallAll();
    test_GetTrampoline_non_null();
    test_SetManagedCallback_fires_with_args();
    test_SetManagedCallback_null_clears();
    test_SetManagedCallback_invalid_handle_fails();
    test_Uninstall_clears_managed_callback_slot_reuse();
    test_ExtraStackArgs_forwards_all_args_to_original();
    test_ExtraStackArgs_default_zero_still_fires_without_crash();
    test_SetExtraStackArgs_out_of_range_fails();
    test_InstallTyped_mixed_float_slots_forwards_correctly();
    test_Install_default_still_misreads_float_slot_known_limitation();
    test_InstallTyped_invalid_mask_fails();
    test_SetExtraStackArgs_rejected_on_float_hook();
    test_ReturnAddress_points_into_the_caller();
    test_RecordSize_is_48();
    test_Records_are_written_in_order();
    test_Records_overwritten_are_countable();
    test_Records_stop_after_detach();
    test_Buffer_rejects_bad_capacity();
    test_Buffer_rejects_unwritable();
    test_Buffer_rejects_too_small();
    test_FirstSeq_skips_calls_before_lending();
    test_Release_order_survives();
    test_NativeSideStackWalk_depth();
    test_Frames_reach_the_caller();
    test_Frames_rejected_above_limit();

    printf("\n=== Result: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail == 0 ? 0 : 1;
}
