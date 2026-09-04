// OBS Studio のプラグインとして NGOL を載せる。
//
// ホストの型を知っているのはこのファイルだけ。NGOL 側は .NET で動き、
// ここが用意した 3 つの口（Ngol_Obs_Call / Ngol_Obs_TakeResult / Ngol_Obs_TakeFrame）から
// ホストへ届く。

#include <obs-module.h>
#include <obs-frontend-api.h>

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <shellapi.h>

#include <atomic>
#include <memory>
#include <string>
#include <thread>

#include "NgolBridge.h"
#include "ObsOps.h"
#include "ObsShaderFilter.h"
#include "ObsFrameSource.h"
#include "ObsGlassFilter.h"

OBS_DECLARE_MODULE()
OBS_MODULE_USE_DEFAULT_LOCALE("ngol-for-obs", "en-US")

MODULE_EXPORT const char* obs_module_description(void)
{
    return "Runs a node graph runtime inside OBS and lets graphs drive it.";
}

MODULE_EXPORT const char* obs_module_name(void)
{
    return "NGOL for OBS";
}

namespace {

std::unique_ptr<NgolBridge> g_bridge;
std::thread g_starter;
std::wstring g_ngolDir;

// 起こすのは別スレッドで、聞きに来るのは UI スレッド。
// 出来上がってからここへ入れる。破棄は UI スレッドでしか起きないので、読む側と競らない。
std::atomic<NgolBridge*> g_ready{nullptr};

// 答えの控えは呼んだスレッドごとに持つ。別のスレッドの答えを引き取らないため。
thread_local std::string g_lastResult;

std::wstring Widen(const std::string& text)
{
    if (text.empty()) return {};
    int need = MultiByteToWideChar(CP_UTF8, 0, text.c_str(), int(text.size()), nullptr, 0);
    std::wstring out(size_t(need), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, text.c_str(), int(text.size()), out.data(), need);
    return out;
}

std::string Narrow(const std::wstring& text)
{
    if (text.empty()) return {};
    int need = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), int(text.size()), nullptr, 0, nullptr, nullptr);
    std::string out(size_t(need), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.c_str(), int(text.size()), out.data(), need, nullptr, nullptr);
    return out;
}

// このモジュールの置き場所から NGOL 一式の場所を決める。
// 配置は <root>\bin\64bit\NgolForObs.dll と <root>\ngol\ 。
std::wstring ResolveNgolDir()
{
    HMODULE self = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                                GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            reinterpret_cast<LPCWSTR>(&ResolveNgolDir), &self)) {
        return {};
    }

    wchar_t path[MAX_PATH]{};
    if (!GetModuleFileNameW(self, path, MAX_PATH)) return {};

    std::wstring dir(path);
    for (int i = 0; i < 3; i++) {
        size_t slash = dir.find_last_of(L"\\/");
        if (slash == std::wstring::npos) return {};
        dir.erase(slash);
    }
    return dir + L"\\ngol";
}

void StartNgol()
{
    try {
        // マネージド入口も NGOL 本体も同じフォルダに置く。
        g_bridge = std::make_unique<NgolBridge>(g_ngolDir, g_ngolDir);
        g_ready.store(g_bridge.get(), std::memory_order_release);

        // 待ち受け先は NGOL が自分のログへ出す。ここで番号を繰り返すと、
        // 移った場合に片方だけが古いことを言う。開く手段だけを案内する。
        blog(LOG_INFO, "[NgolForObs] started; open the node graph from the Tools menu");
    } catch (const std::exception& e) {
        // 起こせなくてもホストは巻き込まない。
        blog(LOG_ERROR, "[NgolForObs] could not start: %s", e.what());
    } catch (...) {
        blog(LOG_ERROR, "[NgolForObs] could not start: unknown error");
    }
}

const char* EventName(enum obs_frontend_event event)
{
    switch (event) {
    case OBS_FRONTEND_EVENT_STREAMING_STARTED:        return "streaming_started";
    case OBS_FRONTEND_EVENT_STREAMING_STOPPED:        return "streaming_stopped";
    case OBS_FRONTEND_EVENT_RECORDING_STARTED:        return "recording_started";
    case OBS_FRONTEND_EVENT_RECORDING_STOPPED:        return "recording_stopped";
    case OBS_FRONTEND_EVENT_RECORDING_PAUSED:         return "recording_paused";
    case OBS_FRONTEND_EVENT_RECORDING_UNPAUSED:       return "recording_unpaused";
    case OBS_FRONTEND_EVENT_REPLAY_BUFFER_STARTED:    return "replay_buffer_started";
    case OBS_FRONTEND_EVENT_REPLAY_BUFFER_STOPPED:    return "replay_buffer_stopped";
    case OBS_FRONTEND_EVENT_REPLAY_BUFFER_SAVED:      return "replay_buffer_saved";
    case OBS_FRONTEND_EVENT_VIRTUALCAM_STARTED:       return "virtualcam_started";
    case OBS_FRONTEND_EVENT_VIRTUALCAM_STOPPED:       return "virtualcam_stopped";
    case OBS_FRONTEND_EVENT_SCENE_CHANGED:            return "scene_changed";
    case OBS_FRONTEND_EVENT_PREVIEW_SCENE_CHANGED:    return "preview_scene_changed";
    case OBS_FRONTEND_EVENT_SCENE_LIST_CHANGED:       return "scene_list_changed";
    case OBS_FRONTEND_EVENT_TRANSITION_CHANGED:       return "transition_changed";
    case OBS_FRONTEND_EVENT_SCENE_COLLECTION_CHANGED: return "scene_collection_changed";
    case OBS_FRONTEND_EVENT_PROFILE_CHANGED:          return "profile_changed";
    case OBS_FRONTEND_EVENT_STUDIO_MODE_ENABLED:      return "studio_mode_enabled";
    case OBS_FRONTEND_EVENT_STUDIO_MODE_DISABLED:     return "studio_mode_disabled";
    case OBS_FRONTEND_EVENT_SCREENSHOT_TAKEN:         return "screenshot_taken";
    case OBS_FRONTEND_EVENT_THEME_CHANGED:            return "theme_changed";
    case OBS_FRONTEND_EVENT_FINISHED_LOADING:         return "finished_loading";
    case OBS_FRONTEND_EVENT_EXIT:                     return "exit";
    default:                                          return nullptr;
    }
}

void OnFrontendEvent(enum obs_frontend_event event, void*)
{
    if (const char* name = EventName(event)) {
        ObsOps::PushEvent(name);
    }
    if (event == OBS_FRONTEND_EVENT_EXIT) {
        // 終了が始まったら、ホストの型に触る要求はもう通さない。
        g_ready.store(nullptr, std::memory_order_release);
        g_bridge.reset();
    }
}

void OnToolsMenu(void*)
{
    // 番号は控えず、そのつど聞く。設定どおりのポートが使用中なら NGOL は空きへ移っており、
    // 稼働中に移されることもある。控えた値は、そのどちらでも古くなる。
    NgolBridge* bridge = g_ready.load(std::memory_order_acquire);
    int port = bridge ? bridge->ServerPort() : 0;
    if (port <= 0) {
        blog(LOG_WARNING, "[NgolForObs] no port is being served yet");
        MessageBoxW(nullptr, Widen(obs_module_text("NgolNotReady")).c_str(),
                    Widen(obs_module_text("NgolForObs")).c_str(), MB_OK | MB_ICONINFORMATION);
        return;
    }

    std::wstring url = L"http://127.0.0.1:" + std::to_wstring(port) + L"/";
    ShellExecuteW(nullptr, L"open", url.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
}

// 要求 1 件を UI スレッドで処理する。
struct UiCall {
    const std::string* request;
    std::string result;
};

void RunOnUi(void* param)
{
    auto* call = static_cast<UiCall*>(param);
    call->result = ObsOps::HandleOnUiThread(*call->request);
}

} // namespace

// ---------------------------------------------------------------------------
// NGOL へ渡す口
// ---------------------------------------------------------------------------

// 要求も答えも JSON。置き場を渡さずに呼べば、要る長さだけが返る。
EXTERN_C __declspec(dllexport) int Ngol_Obs_Call(const char* requestUtf8, char* outUtf8, int outLen)
{
    if (!requestUtf8) return -1;

    std::string request(requestUtf8);
    std::string result;

    // どの操作かは呼んだスレッドで読む。obs_data は自分で錠を持っている。
    std::string op;
    if (obs_data_t* probe = obs_data_create_from_json(requestUtf8)) {
        op = obs_data_get_string(probe, "op");
        obs_data_release(probe);
    }

    // 描画スレッドの錠を取るものは UI スレッドへ渡さない。
    if (op == "capture") {
        result = ObsOps::HandleCapture(request);
    } else if (obs_in_task_thread(OBS_TASK_UI)) {
        result = ObsOps::HandleOnUiThread(request);
    } else {
        UiCall call { &request, {} };
        obs_queue_task(OBS_TASK_UI, RunOnUi, &call, true);
        result = std::move(call.result);
        if (result.empty()) {
            result = "{\"ok\":false,\"error\":\"the host is not taking requests yet\"}";
        }
    }

    // 答えを控えてから渡す。入りきらなかったときに op をもう一度走らせない--
    // 2 度目の scene.set や capture は、要求されていない操作になる。
    g_lastResult = result;

    int need = int(result.size()) + 1;
    if (outUtf8 && outLen >= need) memcpy(outUtf8, result.c_str(), size_t(need));
    return need;
}

// 入りきらなかった答えを、走らせ直さずに引き取る。
EXTERN_C __declspec(dllexport) int Ngol_Obs_TakeResult(char* outUtf8, int outLen)
{
    int need = int(g_lastResult.size()) + 1;
    if (outUtf8 && outLen >= need) memcpy(outUtf8, g_lastResult.c_str(), size_t(need));
    return need;
}

EXTERN_C __declspec(dllexport) int Ngol_Obs_TakeFrame(unsigned char* out, int outLen,
                                                      int* width, int* height, int* pitch)
{
    return ObsOps::TakeFrame(out, outLen, width, height, pitch);
}

// ---------------------------------------------------------------------------
// ホストの入口
// ---------------------------------------------------------------------------

bool obs_module_load(void)
{
    g_ngolDir = ResolveNgolDir();
    if (g_ngolDir.empty()) {
        blog(LOG_ERROR, "[NgolForObs] could not work out where this module lives");
        return true;
    }

    blog(LOG_INFO, "[NgolForObs] runtime folder: %s", Narrow(g_ngolDir).c_str());

    // 効果の計算式を文字列で受け取る種別。ホストが毎フレーム GPU で走らせる。
    ObsShaderFilter::Register();

    // 別のプロセスが置いた絵を、そのまま 1 つのソースとして出す種別。
    ObsFrameSource::Register();

    // 映っている絵を実際の板に切り分け、3D で回して飛ばす種別。
    ObsGlassFilter::Register();

    obs_frontend_add_event_callback(OnFrontendEvent, nullptr);

    // ホストの起動を待たせない。CLR を起こすのに 1 秒ほど掛かる。
    g_starter = std::thread(StartNgol);
    return true;
}

void obs_module_post_load(void)
{
    obs_frontend_add_tools_menu_item(obs_module_text("OpenNodeGraph"), OnToolsMenu, nullptr);
}

void obs_module_unload(void)
{
    if (g_starter.joinable()) g_starter.join();
    obs_frontend_remove_event_callback(OnFrontendEvent, nullptr);
    g_ready.store(nullptr, std::memory_order_release);
    g_bridge.reset();
    blog(LOG_INFO, "[NgolForObs] stopped");
}
