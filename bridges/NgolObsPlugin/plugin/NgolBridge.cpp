#include "NgolBridge.h"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <nethost.h>
#include <hostfxr.h>
#include <coreclr_delegates.h>

#include <stdexcept>

namespace
{
    std::wstring GetHostfxrPath()
    {
        wchar_t buffer[1024];
        size_t bufferSize = std::size(buffer);
        int rc = get_hostfxr_path(buffer, &bufferSize, nullptr);
        if (rc != 0)
        {
            throw std::runtime_error(".NET hostfxr was not found (the .NET runtime may be missing). Error code: " + std::to_string(rc));
        }
        return std::wstring(buffer);
    }
}

NgolBridge::NgolBridge(const std::wstring& pluginDir, const std::wstring& managedHostDir)
{
    LoadHostfxr();
    InitializeRuntimeAndGetDelegates(managedHostDir);

    if (m_initFn(pluginDir.c_str()) != 0)
    {
        throw std::runtime_error("managed entry Init() returned a failure");
    }
}

int NgolBridge::ServerPort() const
{
    return m_getServerPortFn ? m_getServerPortFn() : 0;
}

NgolBridge::~NgolBridge()
{
    if (m_shutdownFn) m_shutdownFn();
    if (m_hostContext)
    {
        using hostfxr_close_fn = int32_t(__stdcall*)(void*);
        auto closeFn = reinterpret_cast<hostfxr_close_fn>(GetProcAddress(static_cast<HMODULE>(m_hostfxrLib), "hostfxr_close"));
        if (closeFn) closeFn(m_hostContext);
    }
    if (m_hostfxrLib) FreeLibrary(static_cast<HMODULE>(m_hostfxrLib));
}

void NgolBridge::LoadHostfxr()
{
    std::wstring hostfxrPath = GetHostfxrPath();
    m_hostfxrLib = LoadLibraryW(hostfxrPath.c_str());
    if (!m_hostfxrLib)
    {
        throw std::runtime_error("failed to load hostfxr.dll");
    }
}

void NgolBridge::InitializeRuntimeAndGetDelegates(const std::wstring& managedHostDir)
{
    auto initForConfigFn = reinterpret_cast<hostfxr_initialize_for_runtime_config_fn>(
        GetProcAddress(static_cast<HMODULE>(m_hostfxrLib), "hostfxr_initialize_for_runtime_config"));
    auto getDelegateFn = reinterpret_cast<hostfxr_get_runtime_delegate_fn>(
        GetProcAddress(static_cast<HMODULE>(m_hostfxrLib), "hostfxr_get_runtime_delegate"));
    if (!initForConfigFn || !getDelegateFn)
    {
        throw std::runtime_error("failed to get hostfxr's function entry points");
    }

    std::wstring runtimeConfigPath = managedHostDir + L"\\NgolActivator.runtimeconfig.json";
    std::wstring assemblyPath = managedHostDir + L"\\NgolActivator.dll";

    hostfxr_handle hostContext = nullptr;
    int rc = initForConfigFn(runtimeConfigPath.c_str(), nullptr, &hostContext);
    if (rc != 0 || !hostContext)
    {
        throw std::runtime_error("hostfxr_initialize_for_runtime_config failed (rc=" +
                                 std::to_string(rc) + ")");
    }
    m_hostContext = hostContext;

    load_assembly_and_get_function_pointer_fn loadAssemblyAndGetFunctionPointer = nullptr;
    rc = getDelegateFn(hostContext, hdt_load_assembly_and_get_function_pointer,
        reinterpret_cast<void**>(&loadAssemblyAndGetFunctionPointer));
    if (rc != 0 || !loadAssemblyAndGetFunctionPointer)
    {
        throw std::runtime_error("hostfxr_get_runtime_delegate failed (rc=" + std::to_string(rc) + ")");
    }

    // 名前は narrow で受けて、失敗したときにどの入口かを言えるようにする。
    // 引ける入口が増えるほど、番号だけでは何が欠けているのか分からなくなる。
    auto LoadFn = [&](const char* methodName, void** outFn) {
        std::string narrow(methodName);
        std::wstring wide(narrow.begin(), narrow.end());
        int r = loadAssemblyAndGetFunctionPointer(
            assemblyPath.c_str(),
            L"NgolActivator.EntryPoint, NgolActivator",
            wide.c_str(),
            UNMANAGEDCALLERSONLY_METHOD,
            nullptr,
            outFn);
        if (r != 0 || !*outFn)
        {
            throw std::runtime_error("could not find managed entry point " + narrow +
                                     " (rc=" + std::to_string(r) + ")");
        }
    };

    LoadFn("Init", reinterpret_cast<void**>(&m_initFn));
    LoadFn("Shutdown", reinterpret_cast<void**>(&m_shutdownFn));
    // マネージド側と揃って配られるものなので、欠けている状態は配置の壊れとして早く出す。
    LoadFn("GetServerPort", reinterpret_cast<void**>(&m_getServerPortFn));
}

