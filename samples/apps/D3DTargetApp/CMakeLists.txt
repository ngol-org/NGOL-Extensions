cmake_minimum_required(VERSION 3.20)
project(D3DTargetApp CXX C)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# 静的 CRT（/MT）。静的 libnethost.lib が /MT のため一致させる（VC ランタイム DLL 依存も消える）。
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

if (MSVC)
    add_link_options("/PDBALTPATH:%_PDB%")   # 再配布物に絶対パスを焼き込まない
    add_compile_options("/utf-8")            # ソースは UTF-8（cp932 誤読でコメントがパースを壊すのを防ぐ）
endif()

# --- hostfxr / nethost（.NET SDK pack から探す・bridge だけが使う） ---
file(GLOB HOSTFXR_PACK_DIRS "C:/Program Files/dotnet/packs/Microsoft.NETCore.App.Host.win-x64/*/runtimes/win-x64/native")
if (NOT HOSTFXR_PACK_DIRS)
    message(FATAL_ERROR "Microsoft.NETCore.App.Host.win-x64 pack not found (install the .NET SDK).")
endif()
list(SORT HOSTFXR_PACK_DIRS)
list(GET HOSTFXR_PACK_DIRS -1 HOSTFXR_NATIVE_DIR)
message(STATUS "hostfxr native dir: ${HOSTFXR_NATIVE_DIR}")

# --- ブリッジ層（DLL）：hostfxr で NGOL を起こし、Present をフックして Tick を回す ---
add_library(NgolBridge SHARED
    bridge/bridge.cpp
    bridge/NgolBridge.cpp
    bridge/PresentHook.cpp
)
target_include_directories(NgolBridge PRIVATE "${HOSTFXR_NATIVE_DIR}" "${CMAKE_CURRENT_SOURCE_DIR}/bridge")
target_compile_definitions(NgolBridge PRIVATE NETHOST_USE_AS_STATIC)
target_link_libraries(NgolBridge PRIVATE "${HOSTFXR_NATIVE_DIR}/libnethost.lib" d3d11)

# --- アプリ層（EXE）：ブリッジをリンクし、Bridge_Start を 1 回呼ぶだけ ---
add_executable(D3DTargetApp WIN32
    app/main.cpp
    app/analysis_targets.cpp
)
target_include_directories(D3DTargetApp PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/app")
target_link_libraries(D3DTargetApp PRIVATE NgolBridge d3d11)
# アプリ自身の関数群（analysis_targets）を export する（解析の的）。
set_target_properties(D3DTargetApp PROPERTIES ENABLE_EXPORTS ON)
