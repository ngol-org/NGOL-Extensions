// AviUtl ExEdit2 の汎用プラグイン (.aux2) として NGOL を起こす。
//
// このファイルだけがホストの型を知っている。ランタイムを起こす側 (NgolBridge) には
// ホストの型を持ち込まない。

#include <windows.h>

#include <cmath>
#include <cstdlib>
#include <memory>
#include <string>
#include <vector>

#include "plugin2.h"
#include "logger2.h"
#include "module2.h"
#include "config2.h"

#include "NgolBridge.h"

namespace {

// NGOL 一式を置くフォルダ。この DLL と同じ場所を既定とする。
constexpr wchar_t kEnvHome[] = L"NGOL_AVIUTL2_HOME";

LOG_HANDLE* g_logger = nullptr;
CONFIG_HANDLE* g_config = nullptr;
std::unique_ptr<NgolBridge> g_bridge;
std::wstring g_selfDir;

std::wstring GetSelfDir() {
    HMODULE self = nullptr;
    GetModuleHandleExW(
        GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(&GetSelfDir),
        &self);
    wchar_t buf[MAX_PATH]{};
    GetModuleFileNameW(self, buf, MAX_PATH);
    std::wstring path(buf);
    auto slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? std::wstring() : path.substr(0, slash);
}

std::wstring GetEnv(const wchar_t* name) {
    wchar_t buf[1024]{};
    DWORD n = GetEnvironmentVariableW(name, buf, static_cast<DWORD>(std::size(buf)));
    return (n == 0 || n >= std::size(buf)) ? std::wstring() : std::wstring(buf, n);
}

// ホストのログは 1024 文字で切られる。切られない記録を残すため、
// 置かれた場所へも書く。ホストがログ出力ハンドルを渡す前に落ちた場合も
// こちらには残るので、読み込まれなかったのか初期化で落ちたのかを区別できる。
void WriteFileLog(const std::wstring& message) {
    std::wstring path = (g_selfDir.empty() ? GetSelfDir() : g_selfDir) + L"\\NgolForAviUtl2.log";

    HANDLE h = CreateFileW(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ, nullptr,
                           OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;

    SYSTEMTIME t{};
    GetLocalTime(&t);
    wchar_t head[64]{};
    swprintf_s(head, L"[%02d:%02d:%02d.%03d] [tid %lu] ",
               t.wHour, t.wMinute, t.wSecond, t.wMilliseconds, GetCurrentThreadId());
    std::wstring line = std::wstring(head) + message + L"\r\n";

    int bytes = WideCharToMultiByte(CP_UTF8, 0, line.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (bytes > 1) {
        std::string utf8(static_cast<size_t>(bytes) - 1, '\0');
        WideCharToMultiByte(CP_UTF8, 0, line.c_str(), -1, utf8.data(), bytes, nullptr, nullptr);
        DWORD written = 0;
        WriteFile(h, utf8.data(), static_cast<DWORD>(utf8.size()), &written, nullptr);
    }
    CloseHandle(h);
}

void Log(const std::wstring& message) {
    WriteFileLog(message);
    if (g_logger) g_logger->info(g_logger, message.c_str());
}

void LogError(const std::wstring& message) {
    WriteFileLog(L"[error] " + message);
    if (g_logger) g_logger->error(g_logger, message.c_str());
}

std::wstring Widen(const char* s) {
    if (!s) return L"";
    int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
    if (n <= 1) return L"";
    std::wstring w(static_cast<size_t>(n) - 1, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s, -1, w.data(), n);
    return w;
}

std::string Narrow(const std::wstring& s) {
    if (s.empty()) return std::string();
    int n = WideCharToMultiByte(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()),
                                nullptr, 0, nullptr, nullptr);
    std::string out(static_cast<size_t>(n), 0);
    WideCharToMultiByte(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()),
                        out.data(), n, nullptr, nullptr);
    return out;
}

COMMON_PLUGIN_TABLE g_plugin_table = {
    L"NGOL for AviUtl ExEdit2",
    L"NGOL bridge",
};

//--------------------------------------------------------------------
// PNG の組み立て。
//
// 外部の圧縮ライブラリは持ち込まない。PNG が要求するのは zlib 形式であって
// 縮んでいることではないので、無圧縮の格納ブロックで規格を満たせる。
// 保存するのは確認用の画像で、置き場所もその都度消える所になる。
//--------------------------------------------------------------------

unsigned long Crc32(const unsigned char* data, size_t len, unsigned long crc = 0xFFFFFFFFu) {
    static unsigned long table[256];
    static bool ready = false;
    if (!ready) {
        for (unsigned long n = 0; n < 256; n++) {
            unsigned long c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
            table[n] = c;
        }
        ready = true;
    }
    for (size_t i = 0; i < len; i++) crc = table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
    return crc;
}

void PutBe32(std::string& s, unsigned long v) {
    s.push_back(static_cast<char>((v >> 24) & 0xFF));
    s.push_back(static_cast<char>((v >> 16) & 0xFF));
    s.push_back(static_cast<char>((v >> 8) & 0xFF));
    s.push_back(static_cast<char>(v & 0xFF));
}

void PutChunk(std::string& out, const char type[4], const std::string& body) {
    PutBe32(out, static_cast<unsigned long>(body.size()));
    std::string typed(type, 4);
    std::string payload = typed + body;
    out += payload;
    unsigned long crc = Crc32(reinterpret_cast<const unsigned char*>(payload.data()), payload.size()) ^ 0xFFFFFFFFu;
    PutBe32(out, crc);
}

bool BuildPng(const unsigned char* rgba, int w, int h, std::string& out) {
    if (w <= 0 || h <= 0) return false;

    // 走査線ごとに「フィルタ種別」を 1 バイト先頭へ置くのが PNG の規約。0 = フィルタなし。
    std::string raw;
    raw.reserve(static_cast<size_t>(h) * (static_cast<size_t>(w) * 4 + 1));
    for (int y = 0; y < h; y++) {
        raw.push_back(0);
        raw.append(reinterpret_cast<const char*>(rgba + static_cast<size_t>(y) * w * 4),
                   static_cast<size_t>(w) * 4);
    }

    // zlib: 2 バイトの見出しの後、無圧縮ブロックを並べ、最後に adler32 を置く。
    std::string z;
    z.push_back('\x78');
    z.push_back('\x01');
    const size_t kBlock = 65535;
    for (size_t pos = 0; pos < raw.size(); pos += kBlock) {
        size_t len = raw.size() - pos;
        bool last = len <= kBlock;
        if (!last) len = kBlock;
        z.push_back(last ? '\x01' : '\x00');
        z.push_back(static_cast<char>(len & 0xFF));
        z.push_back(static_cast<char>((len >> 8) & 0xFF));
        z.push_back(static_cast<char>((~len) & 0xFF));
        z.push_back(static_cast<char>(((~len) >> 8) & 0xFF));
        z.append(raw, pos, len);
    }
    unsigned long a = 1, b = 0;
    for (unsigned char c : raw) { a = (a + c) % 65521; b = (b + a) % 65521; }
    PutBe32(z, (b << 16) | a);

    out.assign("\x89PNG\r\n\x1a\n", 8);

    std::string ihdr;
    PutBe32(ihdr, static_cast<unsigned long>(w));
    PutBe32(ihdr, static_cast<unsigned long>(h));
    ihdr.push_back(8);      // ビット深度
    ihdr.push_back(6);      // RGBA
    ihdr.push_back(0);      // 圧縮方式
    ihdr.push_back(0);      // フィルタ方式
    ihdr.push_back(0);      // インターレースなし
    PutChunk(out, "IHDR", ihdr);
    PutChunk(out, "IDAT", z);
    PutChunk(out, "IEND", std::string());
    return true;
}

//--------------------------------------------------------------------
// スクリプトから呼べる関数。
//
// ホストへ渡す文字列は UTF-8。実行時の文字セットを UTF-8 に指定しているので
// 素の文字列リテラルがそのまま UTF-8 になる。u8 を付けた文字列は C++20 で
// 別の型になり、ホストの受け口へ渡せない。
//
// ホストは表を受け取った時点では読まず、必要になってから取りに来る
// （実行ファイルの型情報で確認済み）。スクリプトの実行環境がまだ無い
// 段階で登録してよい。
//--------------------------------------------------------------------

// スクリプトの実行環境を持つスレッドを知るために記録する。
// このスレッドでなければ触ってはいけない対象があるため、
// 実装の前に実測で確かめる。
DWORD g_scriptThreadId = 0;

void ScriptVersion(SCRIPT_MODULE_PARAM* param) {
    DWORD tid = GetCurrentThreadId();
    if (tid != g_scriptThreadId) {
        g_scriptThreadId = tid;
        wchar_t line[128]{};
        swprintf_s(line, L"script thread id = %lu", tid);
        Log(line);
    }
    param->push_result_string("NGOL for AviUtl ExEdit2");
}

void ScriptLog(SCRIPT_MODULE_PARAM* param) {
    if (param->get_param_num() < 1) {
        param->set_error("引数が足りません");
        return;
    }
    LPCSTR text = param->get_param_string(0);
    if (!text) {
        param->set_error("文字列を渡してください");
        return;
    }
    Log(L"[script] " + Widen(text));
}

// 画像バッファを受け取って PNG として保存する。
//
// スクリプト側は obj.getpixeldata() で描いた結果を読めるが、
// ファイルへ書き出す手段は実行環境から外されている。
// 受け取って書くところだけをこちらが担う。
//
// 引数は公式のサンプルと同じ並び。data はユーザーデータとして渡るポインタ。
void ScriptSavePixels(SCRIPT_MODULE_PARAM* param) {
    if (param->get_param_num() < 4) {
        param->set_error("引数は data,w,h,path の 4 つです");
        return;
    }
    auto* pixels = static_cast<const unsigned char*>(param->get_param_data(0));
    int w = param->get_param_int(1);
    int h = param->get_param_int(2);
    LPCSTR path = param->get_param_string(3);

    if (!pixels || w <= 0 || h <= 0 || !path) {
        param->set_error("引数の値が正しくありません");
        return;
    }

    std::wstring wpath = Widen(path);
    std::string png;
    // 画素は RGBA の 32bit。上から下へ並ぶ前提。
    if (!BuildPng(pixels, w, h, png)) {
        param->set_error("画像の作成に失敗しました");
        return;
    }

    HANDLE f = CreateFileW(wpath.c_str(), GENERIC_WRITE, 0, nullptr,
                           CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (f == INVALID_HANDLE_VALUE) {
        param->set_error("ファイルを作成できません");
        return;
    }
    DWORD written = 0;
    WriteFile(f, png.data(), static_cast<DWORD>(png.size()), &written, nullptr);
    CloseHandle(f);

    wchar_t line[512]{};
    swprintf_s(line, L"saved %dx%d to %s", w, h, wpath.c_str());
    Log(line);
    param->push_result_boolean(true);
}

//--------------------------------------------------------------------
// NGOL 側から渡された式を、スクリプトの実行環境で実行する。
//
// 実行環境は自分のスレッドでしか触れないので、こちらから直接は実行できない。
// 代わりに積んでおき、スクリプトが呼んでくれたときに、そのスレッドで取り出す。
//
// 依頼と結果は番号で対応づける。積んだ順に取り出されるとは限らず、
// 結果を待っている側が別の依頼の結果を受け取ってしまうため。
//--------------------------------------------------------------------

struct EvalRequest {
    unsigned long long id = 0;
    std::string code;
    std::string result;
    bool done = false;
};

CRITICAL_SECTION g_evalLock;
bool g_evalLockReady = false;
std::vector<EvalRequest> g_evalQueue;
unsigned long long g_nextEvalId = 1;

struct EvalLock {
    EvalLock() { if (g_evalLockReady) EnterCriticalSection(&g_evalLock); }
    ~EvalLock() { if (g_evalLockReady) LeaveCriticalSection(&g_evalLock); }
};

// スクリプトが定期的に呼ぶ。溜まっている依頼を 1 つ返す。
// 返すものが無ければ空文字を返し、スクリプト側は何もしない。
void ScriptTakeRequest(SCRIPT_MODULE_PARAM* param) {
    EvalLock lock;
    for (auto& r : g_evalQueue) {
        if (!r.done && r.result.empty()) {
            // 取り出したことを示すため、結果欄に印を置く。
            r.result = "\x01";
            param->push_result_double(static_cast<double>(r.id));
            param->push_result_string(r.code.c_str());
            return;
        }
    }
    param->push_result_double(0.0);
    param->push_result_string("");
}

// スクリプトが実行した結果を戻す。
void ScriptPutResult(SCRIPT_MODULE_PARAM* param) {
    if (param->get_param_num() < 2) {
        param->set_error("引数は id,result の 2 つです");
        return;
    }
    auto id = static_cast<unsigned long long>(param->get_param_double(0));
    LPCSTR text = param->get_param_string(1);

    EvalLock lock;
    for (auto& r : g_evalQueue) {
        if (r.id == id) {
            r.result = text ? text : "";
            r.done = true;
            return;
        }
    }
}

SCRIPT_MODULE_FUNCTION g_script_functions[] = {
    { L"version", ScriptVersion },
    { L"log", ScriptLog },
    { L"save_pixels", ScriptSavePixels },
    { L"take_request", ScriptTakeRequest },
    { L"put_result", ScriptPutResult },
    { nullptr, nullptr },
};

SCRIPT_MODULE_TABLE g_script_module_table = {
    L"NGOL for AviUtl ExEdit2",
    g_script_functions,
};

//--------------------------------------------------------------------
// スクリプトの実行環境を起こす。
//
// 実行環境はスクリプトが使われるまで作られない。オブジェクトを 1 つ作れば
// そこで作られるので、編集セクション越しにホストへ作成を依頼する。
// 編集セクションのコールバックはメインスレッドから呼ばれる（ヘッダに明記）。
//
// 既定では何もしない。何も置いていない状態で実行環境が生まれるのは
// 利用者の意図と違うため、明示的に指示されたときだけ動く。
//--------------------------------------------------------------------

EDIT_HANDLE* g_edit = nullptr;
HOST_APP_TABLE* g_host = nullptr;

// 収集した文字列を UTF-8 で呼び出し側の領域へ写す。
// 入り切らない場合は入る分だけ写し、必要な長さを返す。
int CopyOutUtf8(const std::wstring& text, char* outUtf8, int outLen) {
    int need = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (need <= 0) return 0;
    if (need <= outLen) {
        WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, outUtf8, outLen, nullptr, nullptr);
    } else {
        outUtf8[0] = '\0';
    }
    return need;
}

// 編集セクションのコールバックへ渡す内容。
struct CreateRequest {
    std::string alias;
    int layer;
    int frame;
    bool created;
};

CreateRequest* g_pendingRequest = nullptr;

bool CreateObjectFromAlias(const std::string& aliasUtf8, int layer, int frame) {
    if (!g_edit || !g_edit->call_edit_section) {
        LogError(L"no edit handle; cannot create an object");
        return false;
    }

    CreateRequest request{ aliasUtf8, layer, frame, false };
    g_pendingRequest = &request;

    bool entered = g_edit->call_edit_section([](EDIT_SECTION* edit) {
        auto* req = g_pendingRequest;
        if (!req || !edit || !edit->create_object_from_alias) return;
        req->created = edit->create_object_from_alias(req->alias.c_str(),
                                                      req->layer, req->frame, 0) != nullptr;
    });

    g_pendingRequest = nullptr;

    if (!entered) {
        LogError(L"the host refused to open an edit section");
        return false;
    }
    return request.created;
}

//--------------------------------------------------------------------
// 設定。
//
// 設定を保存する口はホストに無い。置き場所を新しく作らず、NGOL が既に
// 使っている ngol-config.json に相乗りする。同じファイルを NGOL 本体も
// 読むので、書き戻すときは対象のキーの値だけを差し替える。
//--------------------------------------------------------------------

constexpr char kWakeKey[] = "\"wakeScriptRuntimeOnStartup\"";

// 既定は起こさない。何も置いていない状態で実行環境が生まれるのは
// 利用者の意図と違う。
bool g_wakeOnStartup = false;

std::wstring ConfigPath() {
    return (g_selfDir.empty() ? GetSelfDir() : g_selfDir) + L"\\ngol-config.json";
}

std::string ReadConfigText() {
    HANDLE h = CreateFileW(ConfigPath().c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return "";

    std::string text;
    char buf[4096];
    DWORD read = 0;
    while (ReadFile(h, buf, sizeof(buf), &read, nullptr) && read > 0) {
        text.append(buf, read);
    }
    CloseHandle(h);
    return text;
}

bool WriteConfigText(const std::string& text) {
    HANDLE h = CreateFileW(ConfigPath().c_str(), GENERIC_WRITE, 0, nullptr,
                           CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;

    DWORD written = 0;
    bool ok = WriteFile(h, text.data(), static_cast<DWORD>(text.size()), &written, nullptr) != 0;
    CloseHandle(h);
    return ok;
}

// 値の範囲を返す。キーが無ければ npos。
size_t FindWakeValue(const std::string& text, size_t* valueEnd) {
    size_t key = text.find(kWakeKey);
    if (key == std::string::npos) return std::string::npos;

    size_t colon = text.find(':', key + sizeof(kWakeKey) - 1);
    if (colon == std::string::npos) return std::string::npos;

    size_t begin = text.find_first_not_of(" \t\r\n", colon + 1);
    if (begin == std::string::npos) return std::string::npos;

    size_t end = text.find_first_of(",}\r\n \t", begin);
    *valueEnd = (end == std::string::npos) ? text.size() : end;
    return begin;
}

bool LoadWakeSetting() {
    std::string text = ReadConfigText();
    size_t end = 0;
    size_t begin = FindWakeValue(text, &end);
    if (begin == std::string::npos) return false;
    return text.compare(begin, end - begin, "true") == 0;
}

bool SaveWakeSetting(bool value) {
    std::string text = ReadConfigText();
    const char* literal = value ? "true" : "false";

    size_t end = 0;
    size_t begin = FindWakeValue(text, &end);
    if (begin != std::string::npos) {
        text.replace(begin, end - begin, literal);
        return WriteConfigText(text);
    }

    size_t close = text.find_last_of('}');
    if (close == std::string::npos) {
        return WriteConfigText(std::string("{\n  ") + kWakeKey + ": " + literal + "\n}\n");
    }

    // 直前の項目の後ろへカンマを足してから追記する。カンマを追記の側に
    // 付けると、行頭にカンマだけが残る。
    size_t last = text.find_last_not_of(" \t\r\n", close - 1);
    if (last != std::string::npos && text[last] != '{') {
        text.insert(last + 1, ",");
        close += 1;
    }
    text.insert(close, std::string("  ") + kWakeKey + ": " + literal + "\n");
    return WriteConfigText(text);
}

//--------------------------------------------------------------------
// スクリプトの実行環境を起こす。
//
// 実行環境はスクリプトが使われるまで作られない。ホストへシーンの
// レンダリングを依頼すると、置いてあるスクリプトがそこで動く。
// オブジェクトを作らずに済むので、利用者のプロジェクトは変わらない。
//
// 依頼はタスクを積むだけで返る。完了はイベント通知スレッドから届く。
//--------------------------------------------------------------------

// 直近に描かれた 1 枚を控える場所。
//   写すのはコールバックの中（イベント通知スレッド）、取り出すのはノードのスレッド。
//   触る側が違うので、既存の待ち合わせと同じ形で守る。
CRITICAL_SECTION g_frameLock;
bool g_frameLockReady = false;
std::vector<unsigned char> g_frameBytes;
int g_frameWidth = 0;
int g_frameHeight = 0;
int g_framePitch = 0;
unsigned int g_frameSeq = 0;

bool RenderScene(int frame) {
    if (!g_edit || !g_edit->rendering_scene_video) {
        LogError(L"this host build cannot render on request");
        return false;
    }

    bool queued = g_edit->rendering_scene_video(frame, nullptr,
        [](void*, int f, const void* buffer, int width, int height, int pitch) {
            wchar_t text[128]{};
            swprintf_s(text, L"rendered frame %d (%d x %d)", f, width, height);
            Log(text);

            // 画素はこのコールバックの間しか有効でない。渡された先頭を控えても
            // あとで読めば別のものを読む。だからここで写す。
            if (!buffer || width <= 0 || height <= 0 || pitch <= 0) return;

            EnterCriticalSection(&g_frameLock);
            g_frameBytes.resize(static_cast<size_t>(pitch) * height);
            memcpy(g_frameBytes.data(), buffer, g_frameBytes.size());
            g_frameWidth = width;
            g_frameHeight = height;
            g_framePitch = pitch;
            g_frameSeq++;
            LeaveCriticalSection(&g_frameLock);
        });

    if (!queued) LogError(L"the host refused the rendering request");
    return queued;
}

//--------------------------------------------------------------------
// 設定の切り替え口。
//
// ホストの「設定」の中の「プラグイン設定」に出る。ウィンドウを持たなくても
// 出るので、この口のためにウィンドウを作る必要はない。
//--------------------------------------------------------------------

void ApplyWakeSetting(bool value, const wchar_t* from) {
    g_wakeOnStartup = value;
    SaveWakeSetting(value);
    Log(std::wstring(L"startup wake = ") + (value ? L"on" : L"off") + L" (from " + from + L")");
}

void ConfigMenuProc(HWND hwnd, HINSTANCE) {
    Log(L"config menu opened");

    std::wstring message = std::wstring(L"起動時にスクリプトの実行環境を起こす: ")
        + (g_wakeOnStartup ? L"有効" : L"無効") + L"\n\n切り替えますか?";
    if (MessageBoxW(hwnd, message.c_str(), L"NGOL", MB_YESNO | MB_ICONQUESTION) == IDYES) {
        ApplyWakeSetting(!g_wakeOnStartup, L"config menu");
    }
}

// プロジェクトを読み込んだ直後に呼ばれる。プロジェクトの初期化時にも呼ばれる。
void ProjectLoadProc(PROJECT_FILE*) {
    Log(L"project loaded");
    if (g_wakeOnStartup) RenderScene(0);
}

}  // namespace

// NGOL 側から呼ぶ入口。開発中に手作業を省くためのもので、
// 利用者向けの機能としては設定で明示的に有効にしたときだけ使う。
EXTERN_C __declspec(dllexport) bool Ngol_CreateObjectFromAlias(
        const char* aliasUtf8, int layer, int frame) {
    if (!aliasUtf8) return false;
    bool ok = CreateObjectFromAlias(aliasUtf8, layer, frame);
    Log(ok ? L"object created" : L"object creation failed");
    return ok;
}

// 式を積む。積んだ番号を返す。
// スクリプトが動いていなければ、いつまでも実行されない。
EXTERN_C __declspec(dllexport) unsigned long long Ngol_LuaEval(const char* codeUtf8) {
    if (!codeUtf8) return 0;
    EvalLock lock;
    EvalRequest r;
    r.id = g_nextEvalId++;
    r.code = codeUtf8;
    g_evalQueue.push_back(r);
    return r.id;
}

// 結果を取り出す。まだ出ていなければ false。
// 取り出した依頼は捨てる。溜め続けると、実行されないものが積み上がる。
EXTERN_C __declspec(dllexport) bool Ngol_LuaPollResult(
        unsigned long long id, char* outUtf8, int outLen) {
    if (!outUtf8 || outLen <= 0) return false;
    outUtf8[0] = '\0';

    EvalLock lock;
    for (size_t i = 0; i < g_evalQueue.size(); i++) {
        if (g_evalQueue[i].id != id) continue;
        if (!g_evalQueue[i].done) return false;

        const auto& text = g_evalQueue[i].result;
        int n = static_cast<int>(text.size());
        if (n >= outLen) n = outLen - 1;
        memcpy(outUtf8, text.data(), static_cast<size_t>(n));
        outUtf8[n] = '\0';
        g_evalQueue.erase(g_evalQueue.begin() + static_cast<long long>(i));
        return true;
    }
    return false;
}

// ホストに登録されている効果の名前を列挙する。
//
// エイリアスへ書く effect.name は、ここに出てくる名前でなければならない。
// 名前を推測して失敗を繰り返すより、ホストに聞く方が速く確実。
//
// 結果は改行区切りで返す。1 行が「名前<TAB>種別<TAB>フラグ」。
EXTERN_C __declspec(dllexport) int Ngol_EnumEffectNames(char* outUtf8, int outLen) {
    if (!outUtf8 || outLen <= 0) return 0;
    outUtf8[0] = '\0';
    if (!g_edit || !g_edit->enum_effect_name) return 0;

    std::wstring buffer;
    g_edit->enum_effect_name(&buffer, [](void* param, LPCWSTR name, int type, int flag) {
        auto* b = static_cast<std::wstring*>(param);
        wchar_t tail[32]{};
        swprintf_s(tail, L"\t%d\t%d\n", type, flag);
        b->append(name ? name : L"").append(tail);
    });

    return CopyOutUtf8(buffer, outUtf8, outLen);
}

// ホストが読み込んでいるモジュールの一覧。
// 自分が登録したスクリプトモジュールが実際に載っているかを、
// 呼べたかどうかではなくホストの申告で確かめられる。
EXTERN_C __declspec(dllexport) int Ngol_EnumModules(char* outUtf8, int outLen) {
    if (!outUtf8 || outLen <= 0) return 0;
    outUtf8[0] = '\0';
    if (!g_edit || !g_edit->enum_module_info) return 0;

    std::wstring buffer;
    g_edit->enum_module_info(&buffer, [](void* param, MODULE_INFO* info) {
        auto* b = static_cast<std::wstring*>(param);
        if (!info) return;
        wchar_t head[32]{};
        swprintf_s(head, L"%d\t", info->type);
        b->append(head)
          .append(info->name ? info->name : L"")
          .append(L"\t")
          .append(info->information ? info->information : L"")
          .append(L"\n");
    });

    return CopyOutUtf8(buffer, outUtf8, outLen);
}

// 起動時に実行環境を起こすかどうか。ホストのメニューと同じ値を見る。
EXTERN_C __declspec(dllexport) bool Ngol_GetStartupWake() {
    return g_wakeOnStartup;
}

EXTERN_C __declspec(dllexport) bool Ngol_SetStartupWake(bool value) {
    ApplyWakeSetting(value, L"node");
    return true;
}

// シーンのレンダリングを依頼する。置いてあるスクリプトはここで動くので、
// 積んである式もここで実行される。オブジェクトは作らない。
// 直近に描かれた 1 枚を渡す。
//   置き場が足りなければ必要な数を返すだけにする（他の口と同じ作法）。
//   縮小や書き出しはこちらでは決めない。渡すところまでにする。
EXTERN_C __declspec(dllexport) int Ngol_TakeFrame(unsigned char* out, int outLen,
                                                 int* width, int* height, int* pitch,
                                                 unsigned int* seq) {
    if (!g_frameLockReady) return 0;

    EnterCriticalSection(&g_frameLock);
    const int need = static_cast<int>(g_frameBytes.size());
    if (width) *width = g_frameWidth;
    if (height) *height = g_frameHeight;
    if (pitch) *pitch = g_framePitch;
    if (seq) *seq = g_frameSeq;
    if (out && outLen >= need && need > 0) memcpy(out, g_frameBytes.data(), need);
    LeaveCriticalSection(&g_frameLock);
    return need;
}

EXTERN_C __declspec(dllexport) bool Ngol_RenderScene(int frame) {
    return RenderScene(frame);
}

// ある効果が持つ設定項目を列挙する。
//
// エイリアスへ書くキー名は、ここに出てくる名前でなければ効かない。
// 効果名と同じで、推測すると黙って無視された値のまま作られる。
//
// 結果は改行区切りで返す。1 行が「名前<TAB>種別」。
// 対象の効果が見つからない場合は -1 を返す（項目が 0 個の場合と区別する）。
EXTERN_C __declspec(dllexport) int Ngol_EnumEffectItems(
        const char* effectUtf8, char* outUtf8, int outLen) {
    if (!outUtf8 || outLen <= 0) return 0;
    outUtf8[0] = '\0';
    if (!effectUtf8 || !g_edit || !g_edit->enum_effect_item) return 0;

    std::wstring buffer;
    bool found = g_edit->enum_effect_item(Widen(effectUtf8).c_str(), &buffer,
        [](void* param, LPCWSTR name, int type) {
            auto* b = static_cast<std::wstring*>(param);
            wchar_t tail[32]{};
            swprintf_s(tail, L"\t%d\n", type);
            b->append(name ? name : L"").append(tail);
        });

    if (!found) return -1;
    return CopyOutUtf8(buffer, outUtf8, outLen);
}

// 編集情報を返す。
//
// 描画を依頼して断られたとき、こちら側には理由が無い。シーンの長さや
// 編集状態が分かれば、呼び出し側で「範囲外」と「出力中」を区別できる。
//
// 結果は「名前=値」を改行区切りで返す。
EXTERN_C __declspec(dllexport) int Ngol_GetEditInfo(char* outUtf8, int outLen) {
    if (!outUtf8 || outLen <= 0) return 0;
    outUtf8[0] = 0;
    if (!g_edit || !g_edit->get_edit_info) return 0;

    EDIT_INFO info{};
    g_edit->get_edit_info(&info, sizeof(info));

    constexpr wchar_t kLineFeed = 10;
    std::wstring out;
    wchar_t line[128]{};

    auto put = [&](const wchar_t* name, int value) {
        swprintf_s(line, L"%s=%d", name, value);
        out.append(line).append(1, kLineFeed);
    };

    put(L"width", info.width);
    put(L"height", info.height);
    put(L"rate", info.rate);
    put(L"scale", info.scale);
    put(L"sample_rate", info.sample_rate);
    put(L"frame", info.frame);
    put(L"layer", info.layer);
    put(L"frame_max", info.frame_max);
    put(L"layer_max", info.layer_max);
    put(L"select_range_start", info.select_range_start);
    put(L"select_range_end", info.select_range_end);
    put(L"scene_id", info.scene_id);
    put(L"edit_state", g_edit->get_edit_state ? g_edit->get_edit_state() : -1);

    return CopyOutUtf8(out, outUtf8, outLen);
}

// レイヤーの表示を切り替える。
//
// 何が隠れているのかを確かめるとき、画面を触らずに切り分けられる。
// 手前のレイヤーを一時的に消して、下に何が居るのかを見る用途。
// オブジェクトの設定項目の値を書き換える。
//
// スクリプト側からは自分の設定値を読むことしか出来ないので、外から振りたい場合はここを通す。
// 対象は「そのレイヤーの、そのフレーム以降で最初に見つかるオブジェクト」。
EXTERN_C __declspec(dllexport) bool Ngol_SetObjectItemValue(
    int layer, int frame, LPCWSTR effect, LPCWSTR item, LPCSTR value) {
    if (!g_edit || !g_edit->call_edit_section_param) return false;
    if (!effect || !item || !value) return false;

    struct Request {
        int layer;
        int frame;
        LPCWSTR effect;
        LPCWSTR item;
        LPCSTR value;
        bool ok;
    };
    Request request{ layer, frame, effect, item, value, false };

    bool entered = g_edit->call_edit_section_param(&request, [](void* p, EDIT_SECTION* edit) {
        auto* r = static_cast<Request*>(p);
        if (!edit || !edit->find_object || !edit->set_object_item_value) return;

        OBJECT_HANDLE object = edit->find_object(r->layer, r->frame);
        if (!object) return;

        r->ok = edit->set_object_item_value(object, r->effect, r->item, r->value);
    });

    return entered && request.ok;
}

// オブジェクトの設定項目の値を読む。書き換えた結果を確かめる側で使う。
// 戻り値は書き込みに必要な長さ。0 は対象が見つからなかったことを表す。
EXTERN_C __declspec(dllexport) int Ngol_GetObjectItemValue(
    int layer, int frame, LPCWSTR effect, LPCWSTR item, char* out, int outLen) {
    if (!g_edit || !g_edit->call_read_section_param) return 0;
    if (!effect || !item) return 0;

    struct Request {
        int layer;
        int frame;
        LPCWSTR effect;
        LPCWSTR item;
        std::string value;
        bool found;
    };
    Request request{ layer, frame, effect, item, std::string(), false };

    bool entered = g_edit->call_read_section_param(&request, [](void* p, EDIT_SECTION* edit) {
        auto* r = static_cast<Request*>(p);
        if (!edit || !edit->find_object || !edit->get_object_item_value) return;

        OBJECT_HANDLE object = edit->find_object(r->layer, r->frame);
        if (!object) return;

        LPCSTR text = edit->get_object_item_value(object, r->effect, r->item);
        if (!text) return;

        // 返る文字列は次の呼び出しまでしか有効ではないので、その場で写す
        r->value = text;
        r->found = true;
    });

    if (!entered || !request.found) return 0;

    int needed = static_cast<int>(request.value.size()) + 1;
    if (out && outLen >= needed) {
        memcpy(out, request.value.c_str(), static_cast<size_t>(needed));
    }
    return needed;
}

EXTERN_C __declspec(dllexport) bool Ngol_SetLayerEnable(int layer, bool enable) {
    if (!g_edit || !g_edit->call_edit_section_param) return false;

    struct Request { int layer; bool enable; bool ok; };
    Request request{ layer, enable, false };

    bool entered = g_edit->call_edit_section_param(&request, [](void* p, EDIT_SECTION* edit) {
        auto* r = static_cast<Request*>(p);
        if (!edit || !edit->set_layer_enable) return;
        edit->set_layer_enable(r->layer, r->enable);
        r->ok = true;
    });

    return entered && request.ok;
}

// レイヤーの表示状態を読む。切り替えた結果を確かめる側で使う。
EXTERN_C __declspec(dllexport) int Ngol_GetLayerEnable(int layer) {
    if (!g_edit || !g_edit->call_read_section_param) return -1;

    struct Request { int layer; int state; };
    Request request{ layer, -1 };

    g_edit->call_read_section_param(&request, [](void* p, EDIT_SECTION* edit) {
        auto* r = static_cast<Request*>(p);
        if (!edit || !edit->get_layer_enable) return;
        r->state = edit->get_layer_enable(r->layer) ? 1 : 0;
    });

    return request.state;
}
EXTERN_C __declspec(dllexport) DWORD RequiredVersion() {
    return 2003300;
}

// 外観設定の値をホストに解決させる。
//
// style.conf は読める場所にあるが、生の数値がそのまま使われるとは限らない
// (高 DPI で拡大される)。使う側が引きたいキーを渡し、ホストが答えた値を返す。
//
// 入力は 1 行 1 キーで「種別<TAB>キー名」。種別は color / layout / font。
// 出力は同じ順で「種別<TAB>キー名<TAB>値」。引けなかったものも行は返す。
EXTERN_C __declspec(dllexport) int Ngol_ResolveStyle(
        const char* keysUtf8, char* outUtf8, int outLen) {
    if (!outUtf8 || outLen <= 0) return 0;
    outUtf8[0] = 0;
    if (!keysUtf8 || !g_config) return 0;

    constexpr wchar_t kLineFeed = 10;
    constexpr wchar_t kTab = 9;

    std::wstring all = Widen(keysUtf8);
    std::wstring out;
    wchar_t number[64]{};

    size_t start = 0;
    while (start <= all.size()) {
        size_t at = all.find(kLineFeed, start);
        std::wstring line = (at == std::wstring::npos)
                                ? all.substr(start) : all.substr(start, at - start);
        start = (at == std::wstring::npos) ? all.size() + 1 : at + 1;

        if (!line.empty() && line.back() == 13) line.pop_back();
        size_t tab = line.find(kTab);
        if (tab == std::wstring::npos) continue;

        std::wstring kind = line.substr(0, tab);
        std::wstring key = line.substr(tab + 1);
        if (key.empty()) continue;

        std::string keyNarrow = Narrow(key);
        std::wstring value;

        if (kind == L"color") {
            swprintf_s(number, L"%06x", g_config->get_color_code(g_config, keyNarrow.c_str()));
            value = number;
        } else if (kind == L"layout") {
            swprintf_s(number, L"%d", g_config->get_layout_size(g_config, keyNarrow.c_str()));
            value = number;
        } else if (kind == L"font") {
            if (FONT_INFO* font = g_config->get_font_info(g_config, keyNarrow.c_str())) {
                swprintf_s(number, L"%d", static_cast<int>(font->size));
                value = number;
                value.append(1, kTab).append(font->name ? font->name : L"");
            }
        } else {
            continue;
        }

        out.append(kind).append(1, kTab).append(key).append(1, kTab)
           .append(value).append(1, kLineFeed);
    }

    return CopyOutUtf8(out, outUtf8, outLen);
}

// 編集ハンドルをそのまま返す。
//
// ここが無いと、外から使う側はこの変数の位置を番地で覚えるしかない。
// 番地はビルドのたびに動くので、古い値のままだと無関係なメモリを
// 編集ハンドルとして扱い、そこから読んだ値へ飛んで落ちる。
//
// 表を辿るのは呼ぶ側の仕事なので、型は伏せたまま渡す。
EXTERN_C __declspec(dllexport) void* Ngol_GetEditHandle() {
    return g_edit;
}

EXTERN_C __declspec(dllexport) void InitializeConfig(CONFIG_HANDLE* handle) {
    g_config = handle;
}

EXTERN_C __declspec(dllexport) void InitializeLogger(LOG_HANDLE* handle) {
    g_selfDir = GetSelfDir();
    g_logger = handle;

    // ホストが最初に呼ぶ入口。ここで待ち合わせの用意をしておく。
    if (!g_evalLockReady) {
        InitializeCriticalSection(&g_evalLock);
        InitializeCriticalSection(&g_frameLock);
        g_frameLockReady = true;
        g_evalLockReady = true;
    }
}

EXTERN_C __declspec(dllexport) COMMON_PLUGIN_TABLE* GetCommonPluginTable() {
    return &g_plugin_table;
}

EXTERN_C __declspec(dllexport) bool InitializePlugin(DWORD version) {
    wchar_t versionText[64]{};
    swprintf_s(versionText, L"host version %lu", version);

    std::wstring home = GetEnv(kEnvHome);
    if (home.empty()) home = g_selfDir;

    try {
        // マネージド入口と NGOL 本体は同じフォルダに置く。
        g_bridge = std::make_unique<NgolBridge>(home, home);
    } catch (const std::exception& e) {
        LogError(L"failed to start: " + Widen(e.what()));
        // 起こせなくてもホストは巻き込まない。
        return true;
    } catch (...) {
        LogError(L"failed to start: unknown error");
        return true;
    }

    Log(std::wstring(L"started (") + versionText + L")");
    return true;
}

EXTERN_C __declspec(dllexport) void RegisterPlugin(HOST_APP_TABLE* host) {
    if (!host) {
        LogError(L"RegisterPlugin: host table is null");
        return;
    }

    if (!host->register_script_module_name) {
        LogError(L"this host build has no script module registration");
        return;
    }
    host->register_script_module_name(&g_script_module_table, L"ngol");
    Log(L"script module registered as 'ngol'");

    g_host = host;

    if (host->create_edit_handle) {
        g_edit = host->create_edit_handle();
        if (!g_edit) LogError(L"could not obtain an edit handle");
    }

    g_wakeOnStartup = LoadWakeSetting();
    Log(std::wstring(L"startup wake = ") + (g_wakeOnStartup ? L"on" : L"off"));

    // ウィンドウの名前と同じにしない。表示メニューにも同じ名前が並ぶことになり、
    // 名前で引く側が一意に決められなくなる。
    if (host->register_config_menu) {
        host->register_config_menu(L"NGOL 起動設定", ConfigMenuProc);
        Log(L"config menu registered");
    }

    if (host->register_project_load_handler) {
        host->register_project_load_handler(ProjectLoadProc);
        Log(L"project load handler registered");
    }
}

EXTERN_C __declspec(dllexport) void UninitializePlugin() {
    g_bridge.reset();
    Log(L"stopped");
}
