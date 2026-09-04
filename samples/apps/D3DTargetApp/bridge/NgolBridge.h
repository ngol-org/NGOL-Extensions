#pragma once

#include <string>

// hostfxr の Native Hosting API 経由で NodeGraphModLab.Core をインプロセス埋め込みする。
// 毎フレーム Tick() を同期呼び出しする（EnableDirectMode=false の経路）。
// managed 側の実体は NgolActivator.dll の NgolActivator.EntryPoint.Init/Tick/Shutdown。
class NgolBridge
{
public:
    // ngolRoot:       NodeGraphModLab.Core.dll 等・Extensions/・Nodes/ が置かれたディレクトリ。
    // managedHostDir: NgolActivator.dll / .runtimeconfig.json が置かれたディレクトリ。
    NgolBridge(const std::wstring& ngolRoot, const std::wstring& managedHostDir);
    ~NgolBridge();

    NgolBridge(const NgolBridge&) = delete;
    NgolBridge& operator=(const NgolBridge&) = delete;

    void Tick();

private:
    void LoadHostfxr();
    void InitializeRuntimeAndGetDelegates(const std::wstring& ngolRoot, const std::wstring& managedHostDir);

    void* m_hostfxrLib = nullptr;
    void* m_hostContext = nullptr;

    using InitFn = int(__stdcall*)(const wchar_t* ngolRoot);
    using TickFn = void(__stdcall*)();
    using ShutdownFn = void(__stdcall*)();

    InitFn m_initFn = nullptr;
    TickFn m_tickFn = nullptr;
    ShutdownFn m_shutdownFn = nullptr;
};
