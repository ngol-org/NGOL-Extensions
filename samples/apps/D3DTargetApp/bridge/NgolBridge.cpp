#include "NgolBridge.h"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <nethost.h>
#include <hostfxr.h>
#include <coreclr_delegates.h>

#include <iostream>
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
            throw std::runtime_error(".NET hostfxr not found (.NET runtime missing?). rc=" + std::to_string(rc));
        }
        return std::wstring(buffer);
    }
}

NgolBridge::NgolBridge(const std::wstring& ngolRoot, const std::wstring& managedHostDir)
{
    LoadHostfxr();
    InitializeRuntimeAndGetDelegates(ngolRoot, managedHostDir);

    if (m_initFn(ngolRoot.c_str()) != 0)
    {
        throw std::runtime_error("NgolActivator.EntryPoint.Init failed (see NGOL log)");
    }
    std::wcout << L"[NgolBridge] NGOL initialized (ngolRoot=" << ngolRoot << L")" << std::endl;
}

NgolBridge::~NgolBridge()
{
    if (m_shutdownFn) m_shutdownFn();
    if (m_hostContext && m_hostfxrLib)
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

void NgolBridge::InitializeRuntimeAndGetDelegates(const std::wstring& ngolRoot, const std::wstring& managedHostDir)
{
    auto initForConfigFn = reinterpret_cast<hostfxr_initialize_for_runtime_config_fn>(
        GetProcAddress(static_cast<HMODULE>(m_hostfxrLib), "hostfxr_initialize_for_runtime_config"));
    auto getDelegateFn = reinterpret_cast<hostfxr_get_runtime_delegate_fn>(
        GetProcAddress(static_cast<HMODULE>(m_hostfxrLib), "hostfxr_get_runtime_delegate"));
    if (!initForConfigFn || !getDelegateFn)
    {
        throw std::runtime_error("failed to resolve hostfxr entry points");
    }

    std::wstring runtimeConfigPath = managedHostDir + L"\\NgolActivator.runtimeconfig.json";
    std::wstring assemblyPath = managedHostDir + L"\\NgolActivator.dll";

    hostfxr_handle hostContext = nullptr;
    int rc = initForConfigFn(runtimeConfigPath.c_str(), nullptr, &hostContext);
    if (rc != 0 || !hostContext)
    {
        std::wcerr << L"check runtimeconfig.json path: " << runtimeConfigPath << std::endl;
        throw std::runtime_error("hostfxr_initialize_for_runtime_config failed (rc=" + std::to_string(rc) + ")");
    }
    m_hostContext = hostContext;

    load_assembly_and_get_function_pointer_fn loadAssemblyAndGetFunctionPointer = nullptr;
    rc = getDelegateFn(hostContext, hdt_load_assembly_and_get_function_pointer,
        reinterpret_cast<void**>(&loadAssemblyAndGetFunctionPointer));
    if (rc != 0 || !loadAssemblyAndGetFunctionPointer)
    {
        throw std::runtime_error("hostfxr_get_runtime_delegate failed (rc=" + std::to_string(rc) + ")");
    }

    auto LoadFn = [&](const wchar_t* methodName, void** outFn) {
        int r = loadAssemblyAndGetFunctionPointer(
            assemblyPath.c_str(),
            L"NgolActivator.EntryPoint, NgolActivator",
            methodName,
            UNMANAGEDCALLERSONLY_METHOD,
            nullptr,
            outFn);
        if (r != 0 || !*outFn)
        {
            throw std::runtime_error(std::string("load_assembly_and_get_function_pointer failed for method (rc=") + std::to_string(r) + ")");
        }
    };

    LoadFn(L"Init", reinterpret_cast<void**>(&m_initFn));
    LoadFn(L"Tick", reinterpret_cast<void**>(&m_tickFn));
    LoadFn(L"Shutdown", reinterpret_cast<void**>(&m_shutdownFn));
}

void NgolBridge::Tick()
{
    if (m_tickFn) m_tickFn();
}
