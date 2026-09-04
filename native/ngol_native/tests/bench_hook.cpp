#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <algorithm>

// フック本体 1 回あたりの費用を測る。
//
// 対象の DLL は引数で渡す。版の違う DLL を同じ物差しで並べて測れる。
// 片方にしか無い口は GetProcAddress が null を返すので、その場面だけ飛ばす。
//
// 揺れを先に出す（陰性対照）。同じ条件を 2 回測り、その差より小さい増分は
// 「増えなかった」ではなく「測れなかった」と読む。

typedef BOOL  (*Fn_Install)(LPVOID, LPVOID*);
typedef BOOL  (*Fn_Uninstall)(LPVOID);
typedef BOOL  (*Fn_SetCallOriginal)(LPVOID, BOOL);
typedef BOOL  (*Fn_SetRecordBuffer)(LPVOID, LPVOID, int, int, LONGLONG*);

static volatile LONGLONG g_sink = 0;

static __declspec(noinline) LONGLONG __cdecl Target(LONGLONG a, LONGLONG b) {
    g_sink += a ^ b;
    return g_sink;
}

static double MeasureNsPerCall(int iterations) {
    LARGE_INTEGER freq, t0, t1;
    QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&t0);
    for (int i = 0; i < iterations; i++) Target(i, 3);
    QueryPerformanceCounter(&t1);
    double sec = double(t1.QuadPart - t0.QuadPart) / double(freq.QuadPart);
    return sec * 1e9 / iterations;
}

struct Stat { double best; double median; };

static Stat Run(int rounds, int iterations) {
    double* v = (double*)malloc(sizeof(double) * rounds);
    for (int r = 0; r < rounds; r++) v[r] = MeasureNsPerCall(iterations);
    std::sort(v, v + rounds);
    Stat s;
    s.best   = v[0];
    s.median = v[rounds / 2];
    free(v);
    return s;
}

int main(int argc, char** argv) {
    const char* dllPath = (argc > 1) ? argv[1] : "ngol_native.dll";
    // 段数つきの計測は、口が新しい形の DLL でしか呼べない。
    // 古い DLL に段数を渡すと別の引数として解釈されて落ちるので、頼まれたときだけ測る。
    const bool withFrames = (argc > 2) && (0 == strcmp(argv[2], "frames"));
    HMODULE h = LoadLibraryA(dllPath);
    if (!h) { printf("load failed: %s\n", dllPath); return 1; }

    Fn_Install         Install       = (Fn_Install)GetProcAddress(h, "NGOLHook_Install");
    Fn_Uninstall       Uninstall     = (Fn_Uninstall)GetProcAddress(h, "NGOLHook_Uninstall");
    Fn_SetCallOriginal SetCallOrig   = (Fn_SetCallOriginal)GetProcAddress(h, "NGOLHook_SetCallOriginal");
    Fn_SetRecordBuffer SetRecordBuffer = (Fn_SetRecordBuffer)GetProcAddress(h, "NGOLHook_SetRecordBuffer");
    if (!Install || !Uninstall || !SetCallOrig) { printf("missing exports\n"); return 1; }

    const int ROUNDS = 9;
    const int ITERS  = 2000000;

    SetPriorityClass(GetCurrentProcess(), HIGH_PRIORITY_CLASS);
    SetThreadAffinityMask(GetCurrentThread(), 1);

    printf("dll   : %s\n", dllPath);
    printf("rounds: %d x %d calls\n\n", ROUNDS, ITERS);

    Run(3, ITERS);

    Stat a1 = Run(ROUNDS, ITERS);
    Stat a2 = Run(ROUNDS, ITERS);
    printf("A1 bare function            best %7.3f ns  median %7.3f ns\n", a1.best, a1.median);
    printf("A2 bare function (control)  best %7.3f ns  median %7.3f ns\n", a2.best, a2.median);
    printf("   -> noise floor           best %7.3f ns  median %7.3f ns\n\n",
           a2.best - a1.best, a2.median - a1.median);

    LPVOID hook = nullptr;
    if (!Install((LPVOID)Target, &hook)) { printf("install failed\n"); return 1; }
    SetCallOrig(hook, TRUE);
    Run(3, ITERS);
    Stat b = Run(ROUNDS, ITERS);
    printf("B  hook, no buffer         best %7.3f ns  median %7.3f ns\n", b.best, b.median);
    printf("   -> vs A1                best %7.3f ns  median %7.3f ns\n\n",
           b.best - a1.best, b.median - a1.median);

    if (SetRecordBuffer) {
        const int CAP = 4096;
        // 8 段ぶんも入る大きさで取る（段数を増やすと 1 件が大きくなる）。
        SIZE_T bytes = (SIZE_T)CAP * (6 + 8) * sizeof(LONGLONG);
        LPVOID ring = VirtualAlloc(nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (SetRecordBuffer(hook, ring, CAP, 0, nullptr)) {
            Run(3, ITERS);
            Stat c = Run(ROUNDS, ITERS);
            printf("C  hook, buffer (0 frames) best %7.3f ns  median %7.3f ns\n", c.best, c.median);
            printf("   -> vs B                 best %7.3f ns  median %7.3f ns\n\n",
                   c.best - b.best, c.median - b.median);
            if (withFrames && SetRecordBuffer(hook, ring, CAP, 8, nullptr)) {
                Run(3, ITERS);
                Stat d = Run(ROUNDS, ITERS);
                printf("D  hook, buffer + 8 frames best %7.3f ns  median %7.3f ns\n", d.best, d.median);
                printf("   -> vs C                 best %7.3f ns  median %7.3f ns\n\n",
                       d.best - c.best, d.median - c.median);
            }
            SetRecordBuffer(hook, nullptr, 0, 0, nullptr);
        } else {
            printf("C  could not set a record buffer\n\n");
        }
        Uninstall(hook);
        Sleep(50);
        VirtualFree(ring, 0, MEM_RELEASE);
    } else {
        printf("C  this DLL has no record-buffer API (pre-change build)\n\n");
        Uninstall(hook);
    }
    return 0;
}
