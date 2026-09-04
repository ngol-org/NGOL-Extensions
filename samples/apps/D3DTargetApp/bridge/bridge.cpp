// ブリッジ層の公開入口。アプリは Bridge_Start() を 1 回呼ぶだけ。
//   (1) hostfxr で CLR を起こし NGOL を読み込む（DirectMode=false）
//   (2) アプリの Present をフックして、毎フレーム NGOL Tick() を回す
// アプリはこの中身（＝NGOL）を知らない。

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <memory>
#include <string>

#include "NgolBridge.h"
#include "PresentHook.h"

namespace
{
    std::unique_ptr<NgolBridge> g_ngol;

    // このブリッジ DLL 自身の置かれたディレクトリ。NGOL 一式・マネージド入口はここにある想定。
    std::wstring BridgeDir()
    {
        HMODULE self = nullptr;
        GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&BridgeDir), &self);
        wchar_t buf[MAX_PATH]{};
        GetModuleFileNameW(self, buf, MAX_PATH);
        std::wstring p(buf);
        auto slash = p.find_last_of(L"\\/");
        return slash == std::wstring::npos ? L"." : p.substr(0, slash);
    }

    // 毎フレーム、アプリのレンダースレッドで呼ばれる。NGOL を 1 tick 進めるだけ。
    void OnPresent()
    {
        if (g_ngol) g_ngol->Tick();
    }
}

extern "C" __declspec(dllexport) void Bridge_Start()
{
    if (g_ngol) return;
    try
    {
        std::wstring dir = BridgeDir();
        g_ngol = std::make_unique<NgolBridge>(dir, dir);   // hostfxr で NGOL Init（DirectMode=false）
        PresentHook_Install(&OnPresent);                    // 画面更新イベントに Tick をつなぐ
    }
    catch (const std::exception& e)
    {
        // アプリを巻き込まない。失敗はファイルに残す（アプリの標準出力は当てにできない）。
        std::string path(BridgeDir().begin(), BridgeDir().end());
        path += "\\ngol_bridge_diag.log";
        if (FILE* f = nullptr; fopen_s(&f, path.c_str(), "w") == 0 && f)
        { fprintf(f, "Bridge_Start failed: %s\n", e.what()); fclose(f); }
    }
}

extern "C" __declspec(dllexport) void Bridge_Stop()
{
    PresentHook_Remove();
    g_ngol.reset();
}
