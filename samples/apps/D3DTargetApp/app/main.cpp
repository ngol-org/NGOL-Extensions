// D3DTargetApp: アプリ層。
//
// ウィンドウを出し、D3D11 で描くだけのネイティブアプリ。
//   NGOL に触れるのは Bridge_Start() を呼ぶ 1 行だけで、その中身が何なのかは
//   このアプリの関知するところではない（後付けで解析される側）。
//
// D3D11 のデバッグレイヤは既定で無効。`-d3ddebug` を付けて起動すると有効になる
// （無ければ自動で無効へ落とす）。
//
// 常時有効にしない。デバッグレイヤは、別スレッドから同じデバイス／コンテキストを
//   触った瞬間を検出して例外 0x0000087d を投げ、プロセスごと落とす。
//   対象の即時コンテキストを借りるノード（ngol.gfx.capture_backbuffer など）は
//   まさにその形なので、有効なままだと取り込みのたびにこのアプリが死ぬ。
// 逆に、ノードの側の不具合を追うときは有効にする価値がある。
//   「黙って壊れる」が「その場で落ちて番地が残る」に変わる。

#define UNICODE
#define _UNICODE
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <d3d11.h>
#include <string>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "version.lib")

// アプリが呼ぶ唯一の外部の 1 行。中身（＝後付けの解析基盤）はアプリの関知外。
extern "C" void Bridge_Start();

namespace
{
    // 内部解像度（正方形）。ウィンドウのクライアント領域をこの大きさにする。
    constexpr int kInternalSize = 512;

    ID3D11Device*        g_device = nullptr;
    ID3D11DeviceContext* g_context = nullptr;
    IDXGISwapChain*      g_swapChain = nullptr;
    ID3D11RenderTargetView* g_rtv = nullptr;
    bool g_running = true;

    LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
    {
        if (msg == WM_DESTROY || msg == WM_CLOSE) { g_running = false; PostQuitMessage(0); return 0; }
        return DefWindowProcW(hwnd, msg, wp, lp);
    }

    bool CreateDeviceAndSwapChain(HWND hwnd, bool debug)
    {
        DXGI_SWAP_CHAIN_DESC scd{};
        scd.BufferCount = 2;                                   // flip model は 2 以上
        scd.BufferDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;    // 87: BMP と同じ並び
        scd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        scd.OutputWindow = hwnd;
        scd.SampleDesc.Count = 1;
        scd.Windowed = TRUE;
        scd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;

        UINT flags = 0;
        if (debug) flags |= D3D11_CREATE_DEVICE_DEBUG;

        D3D_FEATURE_LEVEL fl;
        HRESULT hr = D3D11CreateDeviceAndSwapChain(
            nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, nullptr, 0,
            D3D11_SDK_VERSION, &scd, &g_swapChain, &g_device, &fl, &g_context);
        if (FAILED(hr) && debug) return CreateDeviceAndSwapChain(hwnd, false);
        if (FAILED(hr)) return false;

        ID3D11Texture2D* backBuffer = nullptr;
        if (FAILED(g_swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&backBuffer))) return false;
        g_device->CreateRenderTargetView(backBuffer, nullptr, &g_rtv);
        backBuffer->Release();
        return true;
    }
}

int WINAPI wWinMain(HINSTANCE hInstance, HINSTANCE, PWSTR pCmdLine, int nCmdShow)
{
    // デバッグレイヤは頼まれたときだけ。既定は無効（上の注意を参照）。
    const bool wantD3dDebug = (pCmdLine != nullptr) && (wcsstr(pCmdLine, L"-d3ddebug") != nullptr);

    // version.dll を import に留めるための 1 行（対象がプロキシ方式を満たすため）。
    DWORD dummy = 0; GetFileVersionInfoSizeW(L"", &dummy);
    // ↑ の下に、解析基盤を起こす 1 行だけ。中身はこのアプリの関知外。
    Bridge_Start();

    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = WndProc;
    wc.hInstance = hInstance;
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.lpszClassName = L"D3DTargetApp";
    RegisterClassExW(&wc);
    // 内部解像度は正方形。ウィンドウはその大きさがそのままクライアント領域になるよう決める
    // （枠とタイトルバーの分は AdjustWindowRect が足す）。
    RECT wr{ 0, 0, kInternalSize, kInternalSize };
    AdjustWindowRect(&wr, WS_OVERLAPPEDWINDOW, FALSE);

    HWND hwnd = CreateWindowExW(0, wc.lpszClassName, L"D3D Target App",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE, CW_USEDEFAULT, CW_USEDEFAULT,
        wr.right - wr.left, wr.bottom - wr.top,
        nullptr, nullptr, hInstance, nullptr);
    if (!hwnd) return 3;

    if (!CreateDeviceAndSwapChain(hwnd, wantD3dDebug)) return 1;

    float t = 0.0f;
    MSG msg{};
    while (g_running)
    {
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
        {
            if (msg.message == WM_QUIT) { g_running = false; break; }
            TranslateMessage(&msg); DispatchMessageW(&msg);
        }
        if (!g_running) break;

        t += 0.01f;
        float col[4] = { 0.1f, (float)(0.5 + 0.4 * (double)((int)(t * 50) % 100) / 100.0), 0.3f, 1.0f };
        g_context->OMSetRenderTargets(1, &g_rtv, nullptr);
        g_context->ClearRenderTargetView(g_rtv, col);
        g_swapChain->Present(1, 0);      // 画面更新イベント。ブリッジがここをフックして Tick を回す
    }

    if (g_rtv) g_rtv->Release();
    if (g_swapChain) g_swapChain->Release();
    if (g_context) g_context->Release();
    if (g_device) g_device->Release();
    return 0;
}
