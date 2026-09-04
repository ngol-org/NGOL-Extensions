#pragma once

#include <string>

// .NET hostfxr Native Hosting API 経由で NGOL をインプロセス埋め込みする。
class NgolBridge
{
public:
    // pluginDir:      NodeGraphModLab.Core.dll 等が置かれたディレクトリ。
    // managedHostDir: NgolActivator.dll / .runtimeconfig.json が置かれたディレクトリ。
    NgolBridge(const std::wstring& pluginDir, const std::wstring& managedHostDir);
    ~NgolBridge();

    NgolBridge(const NgolBridge&) = delete;
    NgolBridge& operator=(const NgolBridge&) = delete;

    // 実際に待ち受けているポート。待ち受けていなければ 0。
    // 設定値は使わない。使用中のポートは NGOL が空きへ移すため、設定と食い違うことがある。
    // 稼働中に移ることもあるので、控えずに呼ぶたびに聞く。
    int ServerPort() const;

private:
    void LoadHostfxr();
    void InitializeRuntimeAndGetDelegates(const std::wstring& managedHostDir);

    void* m_hostfxrLib = nullptr;
    void* m_hostContext = nullptr;

    using InitFn = int(__stdcall*)(const wchar_t* pluginDir);
    using ShutdownFn = void(__stdcall*)();
    using GetServerPortFn = int(__stdcall*)();

    InitFn m_initFn = nullptr;
    ShutdownFn m_shutdownFn = nullptr;
    GetServerPortFn m_getServerPortFn = nullptr;
};
