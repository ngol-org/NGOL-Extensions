cmake_minimum_required(VERSION 3.22)
project(NgolForAviUtl2 CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# SDK のヘッダーは同梱しない。版が動くので、使う側が持っているものを指してもらう。
set(AVIUTL2_SDK_DIR "" CACHE PATH "AviUtl ExEdit2 Plugin SDK directory (plugin2.h, logger2.h)")

if(NOT EXISTS "${AVIUTL2_SDK_DIR}/plugin2.h")
    message(FATAL_ERROR "plugin2.h not found. Pass -DAVIUTL2_SDK_DIR=<path>. See README.md.")
endif()

# .NET SDK の win-x64 ネイティブホストディレクトリ（nethost.h / hostfxr.h / libnethost.lib）。
# 未指定なら、インストール済みの最新版を探す。
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

add_library(NgolForAviUtl2 SHARED
    plugin.cpp
    NgolBridge.cpp
)

target_include_directories(NgolForAviUtl2 PRIVATE "${AVIUTL2_SDK_DIR}" "${DOTNET_HOST_NATIVE}")

# nethost.h は既定で nethost.dll からの dllimport を宣言する。静的リンクするので切り替える。
target_compile_definitions(NgolForAviUtl2 PRIVATE NETHOST_USE_AS_STATIC)

target_link_libraries(NgolForAviUtl2 PRIVATE "${DOTNET_HOST_NATIVE}/libnethost.lib")

# ホストが読み込む名前は拡張子まで決まっている。
set_target_properties(NgolForAviUtl2 PROPERTIES SUFFIX ".aux2")

if(MSVC)
    # 配置先に再頒布可能パッケージを要求しないよう CRT を静的リンクする。
    set_property(TARGET NgolForAviUtl2 PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded")
    # このプロジェクトのソースは UTF-8。SDK のヘッダーは cp932 なので、
    # 文字コードの指定は 1 つに揃えられない。BOM のあるファイルは指定より
    # BOM が優先されるので、こちらは UTF-8 BOM 付きで保存して区別させる。
    target_compile_options(NgolForAviUtl2 PRIVATE "/source-charset:.932" "/execution-charset:utf-8")
    # PE に記録される PDB パスをファイル名だけにする（ビルド環境の絶対パスを配布物へ残さない）。
    target_link_options(NgolForAviUtl2 PRIVATE "/PDBALTPATH:%_PDB%")
endif()
