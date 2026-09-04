cmake_minimum_required(VERSION 3.21)
project(ngol_native C CXX)

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
  message(FATAL_ERROR "ngol_native requires a 64-bit build")
endif()

# ---- バックエンド選択 ----
# 命令の書き換えとトランポリン生成だけが CPU 命令セットと OS に依存する。
# common/ はその実装を知らず、common/ngol_hook_backend.h の境界だけを使う。
set(NGOL_BACKEND "minhook" CACHE STRING "Hook backend implementation")
set_property(CACHE NGOL_BACKEND PROPERTY STRINGS minhook)

set(NGOL_COMMON_SRC
    common/ngol_native.cpp
)

if(NGOL_BACKEND STREQUAL "minhook")
    set(NGOL_BACKEND_SRC
        backend-win-x64/backend_minhook.cpp
        MinHook/src/buffer.c
        MinHook/src/hook.c
        MinHook/src/trampoline.c
        MinHook/src/hde/hde64.c
    )
    set(NGOL_BACKEND_INCLUDE MinHook/include)
else()
    message(FATAL_ERROR "Unknown NGOL_BACKEND: '${NGOL_BACKEND}' (available: minhook)")
endif()

message(STATUS "ngol_native: backend = ${NGOL_BACKEND}")

# ---- 本体 ----
add_library(ngol_native SHARED
    ${NGOL_COMMON_SRC}
    ${NGOL_BACKEND_SRC}
)

target_include_directories(ngol_native PRIVATE
    common
    ${NGOL_BACKEND_INCLUDE}
)

if(MSVC)
    set_property(TARGET ngol_native PROPERTY
        MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    target_compile_options(ngol_native PRIVATE
        /W3
        /EHa
        /GS-
        /utf-8
    )
    target_compile_definitions(ngol_native PRIVATE
        WIN32_LEAN_AND_MEAN
        NOMINMAX
    )
endif()

# ---- テスト ----
enable_testing()

add_executable(test_hook
    tests/test_hook.cpp
    ${NGOL_COMMON_SRC}
    ${NGOL_BACKEND_SRC}
)

target_include_directories(test_hook PRIVATE
    common
    ${NGOL_BACKEND_INCLUDE}
)

if(MSVC)
    set_property(TARGET test_hook PROPERTY
        MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    target_compile_options(test_hook PRIVATE /W3 /EHa /GS- /utf-8)
    target_compile_definitions(test_hook PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX NGOL_TESTING)
endif()

add_test(NAME HookTests COMMAND test_hook)

# ---- 費用の計測 ----
# フック本体 1 回あたりの費用を測る。対象の DLL は実行時に読み込むので、
# 変更前の DLL と変更後の DLL を同じ物差しで並べて測れる。
add_executable(bench_hook tests/bench_hook.cpp)
if(MSVC)
    set_property(TARGET bench_hook PROPERTY
        MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    target_compile_options(bench_hook PRIVATE /W3 /EHsc /utf-8)
    target_compile_definitions(bench_hook PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
endif()

