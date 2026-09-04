cmake_minimum_required(VERSION 3.22)
project(NgolForObs CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# ---------------------------------------------------------------------------
# ホストのヘッダー
#
# 配布版に含まれていないので、使う側が持っているソースを指してもらう。
# 版が動くので既定値は持たせない。タグは配布版と揃えること。
# ---------------------------------------------------------------------------
set(OBS_SOURCE_DIR "" CACHE PATH "obs-studio source tree (must match the installed version)")

if(NOT EXISTS "${OBS_SOURCE_DIR}/libobs/obs-module.h")
    message(FATAL_ERROR "libobs headers not found. Pass -DOBS_SOURCE_DIR=<obs-studio source>. See README.md.")
endif()
if(NOT EXISTS "${OBS_SOURCE_DIR}/frontend/api/obs-frontend-api.h")
    message(FATAL_ERROR "obs-frontend-api.h not found under ${OBS_SOURCE_DIR}/frontend/api. See README.md.")
endif()

# libobs の obs-config.h は obsconfig.h を読む。これはビルドの構成で作られるもので
# ソースには入っていない。Windows の配布構成で意味を持つのは 2 つだけなので、ここで作る。
set(_gen_dir "${CMAKE_BINARY_DIR}/generated")
file(MAKE_DIRECTORY "${_gen_dir}")
file(WRITE "${_gen_dir}/obsconfig.h"
     "#pragma once
#define OBS_RELEASE_CANDIDATE 0
#define OBS_BETA 0
")

# ---------------------------------------------------------------------------
# インポートライブラリ
#
# 配布版に .lib は入っていないので、置いてある DLL のエクスポート表から作る。
# libobs も obs-frontend-api も C のエクスポートなので名前の装飾が無い。
# ---------------------------------------------------------------------------
set(OBS_BINARY_DIR "" CACHE PATH "Installed OBS bin\\64bit directory (obs.dll, obs-frontend-api.dll)")

if(NOT EXISTS "${OBS_BINARY_DIR}/obs.dll")
    message(FATAL_ERROR "obs.dll not found. Pass -DOBS_BINARY_DIR=<obs-studio\\bin\\64bit>. See README.md.")
endif()

get_filename_component(_msvc_bin "${CMAKE_LINKER}" DIRECTORY)
set(_dumpbin "${_msvc_bin}/dumpbin.exe")
set(_lib "${_msvc_bin}/lib.exe")
if(NOT EXISTS "${_dumpbin}" OR NOT EXISTS "${_lib}")
    message(FATAL_ERROR "dumpbin.exe / lib.exe not found next to the linker (${_msvc_bin}).")
endif()

set(_implib_dir "${CMAKE_BINARY_DIR}/implib")
file(MAKE_DIRECTORY "${_implib_dir}")

function(make_import_lib module_name out_var)
    set(_dump "${_implib_dir}/${module_name}.exports.txt")
    set(_def "${_implib_dir}/${module_name}.def")
    set(_out "${_implib_dir}/${module_name}.lib")

    execute_process(
        COMMAND "${_dumpbin}" -exports "${OBS_BINARY_DIR}/${module_name}.dll"
        OUTPUT_FILE "${_dump}" RESULT_VARIABLE _rc)
    if(NOT _rc EQUAL 0)
        message(FATAL_ERROR "dumpbin failed for ${module_name}.dll")
    endif()

    # 装飾された C++ の名前 (先頭が ?) は要らないので、英字始まりだけ拾う。
    file(STRINGS "${_dump}" _lines REGEX "^ +[0-9]+ +[0-9A-Fa-f]+ +[0-9A-Fa-f]+ +[A-Za-z_]")
    set(_body "EXPORTS\n")
    foreach(_line IN LISTS _lines)
        string(REGEX REPLACE "^ +[0-9]+ +[0-9A-Fa-f]+ +[0-9A-Fa-f]+ +([A-Za-z_][A-Za-z0-9_]*).*$"
               "\\1" _name "${_line}")
        string(APPEND _body "    ${_name}\n")
    endforeach()
    file(WRITE "${_def}" "${_body}")

    execute_process(
        COMMAND "${_lib}" -nologo "-def:${_def}" -machine:x64 "-out:${_out}"
        RESULT_VARIABLE _rc OUTPUT_QUIET)
    if(NOT _rc EQUAL 0 OR NOT EXISTS "${_out}")
        message(FATAL_ERROR "could not build an import library for ${module_name}.dll")
    endif()
    set(${out_var} "${_out}" PARENT_SCOPE)
endfunction()

make_import_lib(obs OBS_IMPORT_LIB)
make_import_lib(obs-frontend-api OBS_FRONTEND_IMPORT_LIB)

# ---------------------------------------------------------------------------
# .NET を起こすためのネイティブホスト
# ---------------------------------------------------------------------------
set(DOTNET_HOST_NATIVE "" CACHE PATH ".NET SDK win-x64 native host dir (nethost.h, libnethost.lib)")

if(NOT DOTNET_HOST_NATIVE)
    file(GLOB _host_dirs
        "$ENV{ProgramFiles}/dotnet/packs/Microsoft.NETCore.App.Host.win-x64/*/runtimes/win-x64/native")
    if(_host_dirs)
        list(SORT _host_dirs)
        list(GET _host_dirs -1 DOTNET_HOST_NATIVE)
        message(STATUS "DOTNET_HOST_NATIVE (auto): ${DOTNET_HOST_NATIVE}")
    endif()
endif()

if(NOT EXISTS "${DOTNET_HOST_NATIVE}/libnethost.lib")
    message(FATAL_ERROR "libnethost.lib not found. Pass -DDOTNET_HOST_NATIVE=<path>. See README.md.")
endif()

# ---------------------------------------------------------------------------

add_library(NgolForObs SHARED
    plugin.cpp
    ObsOps.cpp
    ObsShaderFilter.cpp
    ObsFrameSource.cpp
    ObsGlassFilter.cpp
    NgolBridge.cpp
)

target_include_directories(NgolForObs PRIVATE
    "${_gen_dir}"
    "${OBS_SOURCE_DIR}/libobs"
    "${OBS_SOURCE_DIR}/frontend/api"
    "${DOTNET_HOST_NATIVE}"
)

# nethost.h は既定で nethost.dll からの dllimport を宣言する。静的リンクするので切り替える。
target_compile_definitions(NgolForObs PRIVATE NETHOST_USE_AS_STATIC)

target_link_libraries(NgolForObs PRIVATE
    "${OBS_IMPORT_LIB}"
    "${OBS_FRONTEND_IMPORT_LIB}"
    "${DOTNET_HOST_NATIVE}/libnethost.lib"
    shell32
)

if(MSVC)
    # 配置先に再頒布可能パッケージを要求しないよう CRT を静的リンクする。
    set_property(TARGET NgolForObs PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded")
    # ホストのヘッダーもこちらのソースも UTF-8。
    target_compile_options(NgolForObs PRIVATE "/utf-8" "/EHsc")
    # PE に記録される PDB パスをファイル名だけにする（ビルド環境の絶対パスを配布物へ残さない）。
    target_link_options(NgolForObs PRIVATE "/PDBALTPATH:%_PDB%")
endif()
