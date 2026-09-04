// win-x64 向けバックエンド。MinHook で実現する。
//
// ここ以外に MinHook への依存を置かないこと。別プラットフォーム対応は
// このファイルと同じ境界（common/ngol_hook_backend.h）を実装したものを足して差し替える。

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>

#include "MinHook.h"
#include "../common/ngol_hook_backend.h"

static bool g_initialized = false;

extern "C" const char* NgolBackend_Init(void) {
    if (g_initialized) return nullptr;
    if (MH_Initialize() != MH_OK) return "ERR: HOOK_BACKEND_INIT_FAILED (MH_Initialize)";
    g_initialized = true;
    return nullptr;
}

extern "C" const char* NgolBackend_Create(void* target, void* detour, void** trampoline) {
    if (MH_CreateHook(target, detour, trampoline) != MH_OK)
        return "ERR: HOOK_BACKEND_FAILED (MH_CreateHook)";
    return nullptr;
}

extern "C" const char* NgolBackend_Enable(void* target) {
    if (MH_EnableHook(target) != MH_OK)
        return "ERR: HOOK_BACKEND_FAILED (MH_EnableHook)";
    return nullptr;
}

extern "C" void NgolBackend_Disable(void* target) {
    MH_DisableHook(target);
}

extern "C" void NgolBackend_Remove(void* target) {
    MH_RemoveHook(target);
}

extern "C" void NgolBackend_Shutdown(void) {
    if (!g_initialized) return;
    MH_Uninitialize();
    g_initialized = false;
}

extern "C" const char* NgolBackend_Name(void) {
    return "minhook";
}
