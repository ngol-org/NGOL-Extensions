#include "PresentHook.h"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <d3d11.h>

#pragma comment(lib, "d3d11.lib")

namespace
{
    using PresentFn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);

    constexpr int kPresentVtableSlot = 8;   // IDXGISwapChain::Present

    void**    g_vtable = nullptr;            // アプリと共有の vtable
    PresentFn g_originalPresent = nullptr;
    void (*g_onPresent)() = nullptr;

    // vtable はクラス共有なので、このプロセスで作られたどのスワップチェーンの Present もここを通る。
    // コールバックの先で誰かが描いて Present すると、その Present がまたここへ入ってくる。
    // 再入したときはコールバックを呼ばずに元の関数へ素通しする。
    // 通さずに積み上げると、数フレームでスタックを使い切ってプロセスごと落ちる。
    thread_local bool t_inCallback = false;

    struct CallbackScope
    {
        CallbackScope()  { t_inCallback = true; }
        ~CallbackScope() { t_inCallback = false; }
    };

    HRESULT STDMETHODCALLTYPE HookedPresent(IDXGISwapChain* self, UINT sync, UINT flags)
    {
        if (g_onPresent && !t_inCallback)
        {
            CallbackScope scope;
            g_onPresent();                  // NGOL Tick（アプリのレンダースレッド）
        }
        return g_originalPresent(self, sync, flags);
    }

    // ダミーのウィンドウ＋スワップチェーンを作り、vtable を取り出す。
    IDXGISwapChain* CreateDummySwapChain(HWND hwnd, ID3D11Device** outDevice, ID3D11DeviceContext** outCtx)
    {
        DXGI_SWAP_CHAIN_DESC scd{};
        scd.BufferCount = 1;
        scd.BufferDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        scd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        scd.OutputWindow = hwnd;
        scd.SampleDesc.Count = 1;
        scd.Windowed = TRUE;
        scd.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

        IDXGISwapChain* sc = nullptr;
        D3D_FEATURE_LEVEL fl;
        HRESULT hr = D3D11CreateDeviceAndSwapChain(
            nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
            D3D11_SDK_VERSION, &scd, &sc, outDevice, &fl, outCtx);
        return SUCCEEDED(hr) ? sc : nullptr;
    }
}

bool PresentHook_Install(void (*onPresent)())
{
    g_onPresent = onPresent;

    // ダミー用の隠しウィンドウ（表示しない）。
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"NgolPresentHookDummy";
    RegisterClassExW(&wc);
    HWND dummy = CreateWindowExW(0, wc.lpszClassName, L"", WS_POPUP, 0, 0, 8, 8,
        nullptr, nullptr, wc.hInstance, nullptr);
    if (!dummy) return false;

    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    IDXGISwapChain* sc = CreateDummySwapChain(dummy, &dev, &ctx);
    if (!sc) { DestroyWindow(dummy); return false; }

    // COM オブジェクトの先頭は vtable ポインタ。slot 8 が Present。
    g_vtable = *reinterpret_cast<void***>(sc);
    g_originalPresent = reinterpret_cast<PresentFn>(g_vtable[kPresentVtableSlot]);

    // vtable の slot を差し替える（クラス共有なのでアプリの Present もここを通る）。
    DWORD oldProtect = 0;
    VirtualProtect(&g_vtable[kPresentVtableSlot], sizeof(void*), PAGE_READWRITE, &oldProtect);
    g_vtable[kPresentVtableSlot] = reinterpret_cast<void*>(&HookedPresent);
    VirtualProtect(&g_vtable[kPresentVtableSlot], sizeof(void*), oldProtect, &oldProtect);

    // ダミーはもう不要（vtable は dxgi 側に残るので解放して構わない）。
    sc->Release();
    if (ctx) ctx->Release();
    if (dev) dev->Release();
    DestroyWindow(dummy);
    return true;
}

void PresentHook_Remove()
{
    if (!g_vtable || !g_originalPresent) return;
    DWORD oldProtect = 0;
    VirtualProtect(&g_vtable[kPresentVtableSlot], sizeof(void*), PAGE_READWRITE, &oldProtect);
    g_vtable[kPresentVtableSlot] = reinterpret_cast<void*>(g_originalPresent);
    VirtualProtect(&g_vtable[kPresentVtableSlot], sizeof(void*), oldProtect, &oldProtect);
    g_vtable = nullptr;
    g_originalPresent = nullptr;
}
