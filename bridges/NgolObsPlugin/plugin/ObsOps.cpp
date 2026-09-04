#include "ObsOps.h"
#include "ObsShaderFilter.h"

#include <obs-module.h>
#include <obs-frontend-api.h>
#include <util/bmem.h>
#include <graphics/vec2.h>
#include <graphics/vec4.h>

#include <cmath>
#include <cstring>
#include <deque>
#include <mutex>
#include <string>
#include <vector>

namespace {

// ---------------------------------------------------------------------------
// 控え
// ---------------------------------------------------------------------------

std::mutex g_frameLock;
std::vector<unsigned char> g_frame;
int g_frameW = 0, g_frameH = 0, g_framePitch = 0;

std::mutex g_eventLock;
std::deque<std::string> g_events;
const size_t kMaxEvents = 512;

// ---------------------------------------------------------------------------
// 組み立ての下ごしらえ
// ---------------------------------------------------------------------------

std::string Finish(obs_data_t* res)
{
    const char* json = obs_data_get_json(res);
    std::string text = json ? json : "{}";
    obs_data_release(res);
    return text;
}

std::string Fail(const std::string& message)
{
    obs_data_t* res = obs_data_create();
    obs_data_set_bool(res, "ok", false);
    obs_data_set_string(res, "error", message.c_str());
    return Finish(res);
}

obs_data_t* Ok()
{
    obs_data_t* res = obs_data_create();
    obs_data_set_bool(res, "ok", true);
    return res;
}

// ホストが返した文字列は bfree で返す約束になっている。
std::string TakeOwned(char* owned)
{
    std::string text = owned ? owned : "";
    if (owned) bfree(owned);
    return text;
}

const char* SourceTypeName(obs_source_type type)
{
    switch (type) {
    case OBS_SOURCE_TYPE_INPUT:      return "input";
    case OBS_SOURCE_TYPE_FILTER:     return "filter";
    case OBS_SOURCE_TYPE_TRANSITION: return "transition";
    case OBS_SOURCE_TYPE_SCENE:      return "scene";
    default:                         return "unknown";
    }
}

double MulToDb(float mul)
{
    if (mul <= 0.0f) return -100.0;
    double db = 20.0 * std::log10(static_cast<double>(mul));
    return db < -100.0 ? -100.0 : db;
}

float DbToMul(double db)
{
    if (db <= -100.0) return 0.0f;
    return static_cast<float>(std::pow(10.0, db / 20.0));
}

// 名前で引く。空なら現在の番組シーン。呼んだ側が obs_source_release する。
obs_source_t* ResolveScene(const char* name)
{
    if (name && *name) {
        obs_source_t* src = obs_get_source_by_name(name);
        if (src && !obs_scene_from_source(src)) {
            obs_source_release(src);
            return nullptr;
        }
        return src;
    }
    return obs_frontend_get_current_scene();
}

// ---------------------------------------------------------------------------
// 一覧を作るための受け皿
// ---------------------------------------------------------------------------

struct SourceListCtx {
    obs_data_array_t* array;
};

void AppendSource(obs_data_array_t* array, obs_source_t* src)
{
    obs_data_t* item = obs_data_create();
    obs_data_set_string(item, "name", obs_source_get_name(src));
    obs_data_set_string(item, "id", obs_source_get_id(src));
    obs_data_set_string(item, "unversioned_id", obs_source_get_unversioned_id(src));
    obs_data_set_string(item, "type", SourceTypeName(obs_source_get_type(src)));
    obs_data_set_int(item, "width", obs_source_get_width(src));
    obs_data_set_int(item, "height", obs_source_get_height(src));

    uint32_t flags = obs_source_get_output_flags(src);
    bool hasAudio = (flags & OBS_SOURCE_AUDIO) != 0;
    bool hasVideo = (flags & OBS_SOURCE_VIDEO) != 0;
    obs_data_set_bool(item, "has_audio", hasAudio);
    obs_data_set_bool(item, "has_video", hasVideo);
    if (hasAudio) {
        obs_data_set_bool(item, "muted", obs_source_muted(src));
        obs_data_set_double(item, "volume", obs_source_get_volume(src));
    }
    obs_data_array_push_back(array, item);
    obs_data_release(item);
}

bool EnumSourceCb(void* param, obs_source_t* src)
{
    AppendSource(static_cast<SourceListCtx*>(param)->array, src);
    return true;
}

struct SceneItemCtx {
    obs_data_array_t* array;
};

bool EnumSceneItemCb(obs_scene_t*, obs_sceneitem_t* item, void* param)
{
    auto* ctx = static_cast<SceneItemCtx*>(param);
    obs_source_t* src = obs_sceneitem_get_source(item);

    struct vec2 pos {}, scale {};
    obs_sceneitem_get_pos(item, &pos);
    obs_sceneitem_get_scale(item, &scale);

    obs_data_t* entry = obs_data_create();
    obs_data_set_int(entry, "item_id", obs_sceneitem_get_id(item));
    obs_data_set_string(entry, "name", src ? obs_source_get_name(src) : "");
    obs_data_set_string(entry, "source_id", src ? obs_source_get_id(src) : "");
    obs_data_set_bool(entry, "visible", obs_sceneitem_visible(item));
    obs_data_set_bool(entry, "locked", obs_sceneitem_locked(item));
    obs_data_set_double(entry, "x", pos.x);
    obs_data_set_double(entry, "y", pos.y);
    obs_data_set_double(entry, "scale_x", scale.x);
    obs_data_set_double(entry, "scale_y", scale.y);
    obs_data_set_double(entry, "rotation", obs_sceneitem_get_rot(item));
    if (src) {
        obs_data_set_int(entry, "source_width", obs_source_get_width(src));
        obs_data_set_int(entry, "source_height", obs_source_get_height(src));
    }
    obs_data_array_push_back(ctx->array, entry);
    obs_data_release(entry);
    return true;
}

struct FindItemCtx {
    std::string name;
    long long id;
    obs_sceneitem_t* found;
};

bool FindItemCb(obs_scene_t*, obs_sceneitem_t* item, void* param)
{
    auto* ctx = static_cast<FindItemCtx*>(param);
    bool hit = false;
    if (ctx->id > 0) {
        hit = obs_sceneitem_get_id(item) == ctx->id;
    } else if (!ctx->name.empty()) {
        obs_source_t* src = obs_sceneitem_get_source(item);
        const char* name = src ? obs_source_get_name(src) : nullptr;
        hit = name && ctx->name == name;
    }
    if (!hit) return true;

    // 列挙の外まで持ち出すので参照を取る。呼んだ側が返す。
    obs_sceneitem_addref(item);
    ctx->found = item;
    return false;
}

// 計算式が通らなかったときの中身を応答へ載せる。
// これが返らないと、書く側は何が悪いか分からないまま直すことになる。
// 定型を前に付けている分だけ行番号がずれるので、その行数も一緒に返す。
void AddShaderStatus(obs_data_t* target, obs_source_t* filter)
{
    std::string problem;
    int preamble = 0;
    if (!ObsShaderFilter::Status(filter, problem, preamble)) return;
    obs_data_set_string(target, "shader_error", problem.c_str());
    obs_data_set_bool(target, "shader_ok", problem.empty());
    obs_data_set_int(target, "shader_preamble_lines", preamble);
}

struct FilterCtx {
    obs_data_array_t* array;
};

void EnumFilterCb(obs_source_t*, obs_source_t* filter, void* param)
{
    auto* ctx = static_cast<FilterCtx*>(param);
    obs_data_t* entry = obs_data_create();
    obs_data_set_string(entry, "name", obs_source_get_name(filter));
    obs_data_set_string(entry, "id", obs_source_get_id(filter));
    obs_data_set_bool(entry, "enabled", obs_source_enabled(filter));
    AddShaderStatus(entry, filter);
    obs_data_array_push_back(ctx->array, entry);
    obs_data_release(entry);
}

// ---------------------------------------------------------------------------
// 個々の操作
// ---------------------------------------------------------------------------

std::string OpInfo()
{
    obs_data_t* res = Ok();

    obs_data_set_string(res, "obs_version", obs_get_version_string());

    struct obs_video_info ovi {};
    if (obs_get_video_info(&ovi)) {
        obs_data_set_int(res, "base_width", ovi.base_width);
        obs_data_set_int(res, "base_height", ovi.base_height);
        obs_data_set_int(res, "output_width", ovi.output_width);
        obs_data_set_int(res, "output_height", ovi.output_height);
        obs_data_set_double(res, "fps", ovi.fps_den ? double(ovi.fps_num) / ovi.fps_den : 0.0);
    }
    obs_data_set_double(res, "active_fps", obs_get_active_fps());
    obs_data_set_int(res, "total_frames", obs_get_total_frames());
    obs_data_set_int(res, "lagged_frames", obs_get_lagged_frames());

    obs_data_set_bool(res, "streaming", obs_frontend_streaming_active());
    obs_data_set_bool(res, "recording", obs_frontend_recording_active());
    obs_data_set_bool(res, "recording_paused", obs_frontend_recording_paused());
    obs_data_set_bool(res, "replay_buffer", obs_frontend_replay_buffer_active());
    obs_data_set_bool(res, "virtualcam", obs_frontend_virtualcam_active());
    obs_data_set_bool(res, "studio_mode", obs_frontend_preview_program_mode_active());

    if (obs_source_t* scene = obs_frontend_get_current_scene()) {
        obs_data_set_string(res, "current_scene", obs_source_get_name(scene));
        obs_source_release(scene);
    }
    if (obs_frontend_preview_program_mode_active()) {
        if (obs_source_t* preview = obs_frontend_get_current_preview_scene()) {
            obs_data_set_string(res, "preview_scene", obs_source_get_name(preview));
            obs_source_release(preview);
        }
    }
    if (obs_source_t* transition = obs_frontend_get_current_transition()) {
        obs_data_set_string(res, "transition", obs_source_get_name(transition));
        obs_source_release(transition);
    }
    obs_data_set_int(res, "transition_duration_ms", obs_frontend_get_transition_duration());

    obs_data_set_string(res, "profile", TakeOwned(obs_frontend_get_current_profile()).c_str());
    obs_data_set_string(res, "scene_collection",
                        TakeOwned(obs_frontend_get_current_scene_collection()).c_str());
    obs_data_set_string(res, "record_path",
                        TakeOwned(obs_frontend_get_current_record_output_path()).c_str());
    return Finish(res);
}

std::string OpSceneList()
{
    obs_data_t* res = Ok();
    obs_data_array_t* array = obs_data_array_create();

    struct obs_frontend_source_list scenes {};
    obs_frontend_get_scenes(&scenes);
    for (size_t i = 0; i < scenes.sources.num; i++) {
        obs_source_t* src = scenes.sources.array[i];
        obs_data_t* entry = obs_data_create();
        obs_data_set_string(entry, "name", obs_source_get_name(src));
        obs_data_set_int(entry, "width", obs_source_get_width(src));
        obs_data_set_int(entry, "height", obs_source_get_height(src));
        obs_data_array_push_back(array, entry);
        obs_data_release(entry);
    }
    obs_frontend_source_list_free(&scenes);

    obs_data_set_array(res, "scenes", array);
    obs_data_array_release(array);

    if (obs_source_t* current = obs_frontend_get_current_scene()) {
        obs_data_set_string(res, "current_scene", obs_source_get_name(current));
        obs_source_release(current);
    }
    return Finish(res);
}

std::string OpSceneSet(obs_data_t* req)
{
    const char* name = obs_data_get_string(req, "name");
    if (!name || !*name) return Fail("give the name of a scene");

    obs_source_t* src = obs_get_source_by_name(name);
    if (!src) return Fail(std::string("no source is named '") + name + "'");
    if (!obs_scene_from_source(src)) {
        obs_source_release(src);
        return Fail(std::string("'") + name + "' exists but is not a scene");
    }

    bool preview = obs_data_get_bool(req, "preview");
    if (preview) {
        if (!obs_frontend_preview_program_mode_active()) {
            obs_source_release(src);
            return Fail("studio mode is off, so there is no preview to set");
        }
        obs_frontend_set_current_preview_scene(src);
    } else {
        obs_frontend_set_current_scene(src);
    }
    obs_source_release(src);

    obs_data_t* res = Ok();
    obs_data_set_string(res, "applied_to", preview ? "preview" : "program");
    obs_data_set_string(res, "name", name);
    return Finish(res);
}

std::string OpSourceList(obs_data_t* req)
{
    obs_data_t* res = Ok();
    obs_data_array_t* array = obs_data_array_create();

    SourceListCtx ctx { array };
    obs_enum_sources(EnumSourceCb, &ctx);
    if (obs_data_get_bool(req, "include_scenes")) {
        obs_enum_scenes(EnumSourceCb, &ctx);
    }

    obs_data_set_array(res, "sources", array);
    obs_data_set_int(res, "count", obs_data_array_count(array));
    obs_data_array_release(array);
    return Finish(res);
}

std::string OpSourceTypes()
{
    obs_data_t* res = Ok();
    obs_data_array_t* array = obs_data_array_create();

    const char* id = nullptr;
    for (size_t i = 0; obs_enum_source_types(i, &id); i++) {
        if (!id || !*id) continue;
        const char* display = obs_source_get_display_name(id);
        obs_data_t* entry = obs_data_create();
        obs_data_set_string(entry, "id", id);
        obs_data_set_string(entry, "display_name", display ? display : id);
        obs_data_array_push_back(array, entry);
        obs_data_release(entry);
    }

    obs_data_set_array(res, "types", array);
    obs_data_set_int(res, "count", obs_data_array_count(array));
    obs_data_array_release(array);
    return Finish(res);
}

std::string OpSceneItemList(obs_data_t* req)
{
    obs_source_t* sceneSrc = ResolveScene(obs_data_get_string(req, "scene"));
    if (!sceneSrc) return Fail("that scene was not found");

    obs_data_t* res = Ok();
    obs_data_set_string(res, "scene", obs_source_get_name(sceneSrc));

    obs_data_array_t* array = obs_data_array_create();
    SceneItemCtx ctx { array };
    obs_scene_enum_items(obs_scene_from_source(sceneSrc), EnumSceneItemCb, &ctx);
    obs_data_set_array(res, "items", array);
    obs_data_set_int(res, "count", obs_data_array_count(array));
    obs_data_array_release(array);

    obs_source_release(sceneSrc);
    return Finish(res);
}

std::string OpSceneItemSet(obs_data_t* req)
{
    obs_source_t* sceneSrc = ResolveScene(obs_data_get_string(req, "scene"));
    if (!sceneSrc) return Fail("that scene was not found");

    FindItemCtx find { obs_data_get_string(req, "name"),
                       obs_data_get_int(req, "item_id"), nullptr };
    obs_scene_enum_items(obs_scene_from_source(sceneSrc), FindItemCb, &find);
    if (!find.found) {
        std::string sceneName = obs_source_get_name(sceneSrc);
        obs_source_release(sceneSrc);
        return Fail("no such item in scene '" + sceneName + "'");
    }

    obs_sceneitem_t* item = find.found;
    obs_data_t* res = Ok();
    obs_data_array_t* changed = obs_data_array_create();

    auto note = [&](const char* what) {
        obs_data_t* entry = obs_data_create();
        obs_data_set_string(entry, "field", what);
        obs_data_array_push_back(changed, entry);
        obs_data_release(entry);
    };

    if (obs_data_has_user_value(req, "visible")) {
        obs_sceneitem_set_visible(item, obs_data_get_bool(req, "visible"));
        note("visible");
    }
    if (obs_data_has_user_value(req, "locked")) {
        obs_sceneitem_set_locked(item, obs_data_get_bool(req, "locked"));
        note("locked");
    }
    if (obs_data_has_user_value(req, "x") || obs_data_has_user_value(req, "y")) {
        struct vec2 pos {};
        obs_sceneitem_get_pos(item, &pos);
        if (obs_data_has_user_value(req, "x")) pos.x = float(obs_data_get_double(req, "x"));
        if (obs_data_has_user_value(req, "y")) pos.y = float(obs_data_get_double(req, "y"));
        obs_sceneitem_set_pos(item, &pos);
        note("position");
    }
    if (obs_data_has_user_value(req, "scale_x") || obs_data_has_user_value(req, "scale_y")) {
        struct vec2 scale {};
        obs_sceneitem_get_scale(item, &scale);
        if (obs_data_has_user_value(req, "scale_x")) scale.x = float(obs_data_get_double(req, "scale_x"));
        if (obs_data_has_user_value(req, "scale_y")) scale.y = float(obs_data_get_double(req, "scale_y"));
        obs_sceneitem_set_scale(item, &scale);
        note("scale");
    }
    if (obs_data_has_user_value(req, "rotation")) {
        obs_sceneitem_set_rot(item, float(obs_data_get_double(req, "rotation")));
        note("rotation");
    }

    obs_source_t* itemSrc = obs_sceneitem_get_source(item);
    obs_data_set_string(res, "name", itemSrc ? obs_source_get_name(itemSrc) : "");
    obs_data_set_int(res, "item_id", obs_sceneitem_get_id(item));
    obs_data_set_array(res, "changed", changed);
    obs_data_array_release(changed);

    obs_sceneitem_release(item);
    obs_source_release(sceneSrc);
    return Finish(res);
}

std::string OpSettingsGet(obs_data_t* req)
{
    const char* name = obs_data_get_string(req, "name");
    obs_source_t* src = (name && *name) ? obs_get_source_by_name(name) : nullptr;
    if (!src) return Fail("give the name of an existing source");

    obs_data_t* settings = obs_source_get_settings(src);
    const char* json = settings ? obs_data_get_json(settings) : nullptr;

    obs_data_t* res = Ok();
    obs_data_set_string(res, "name", obs_source_get_name(src));
    obs_data_set_string(res, "id", obs_source_get_id(src));
    obs_data_set_string(res, "settings", json ? json : "{}");
    if (settings) obs_data_release(settings);
    obs_source_release(src);
    return Finish(res);
}

std::string OpSettingsSet(obs_data_t* req)
{
    const char* name = obs_data_get_string(req, "name");
    obs_source_t* src = (name && *name) ? obs_get_source_by_name(name) : nullptr;
    if (!src) return Fail("give the name of an existing source");

    const char* json = obs_data_get_string(req, "settings");
    obs_data_t* patch = (json && *json) ? obs_data_create_from_json(json) : nullptr;
    if (!patch) {
        obs_source_release(src);
        return Fail("settings must be a JSON object");
    }

    obs_source_update(src, patch);
    obs_data_release(patch);

    obs_data_t* after = obs_source_get_settings(src);
    const char* afterJson = after ? obs_data_get_json(after) : nullptr;

    obs_data_t* res = Ok();
    obs_data_set_string(res, "name", obs_source_get_name(src));
    obs_data_set_string(res, "id", obs_source_get_id(src));
    obs_data_set_string(res, "settings", afterJson ? afterJson : "{}");
    if (after) obs_data_release(after);
    obs_source_release(src);
    return Finish(res);
}

std::string OpAudioGet(obs_data_t* req)
{
    const char* name = obs_data_get_string(req, "name");
    obs_source_t* src = (name && *name) ? obs_get_source_by_name(name) : nullptr;
    if (!src) return Fail("give the name of an existing source");

    float mul = obs_source_get_volume(src);
    obs_data_t* res = Ok();
    obs_data_set_string(res, "name", obs_source_get_name(src));
    obs_data_set_bool(res, "has_audio", (obs_source_get_output_flags(src) & OBS_SOURCE_AUDIO) != 0);
    obs_data_set_bool(res, "muted", obs_source_muted(src));
    obs_data_set_double(res, "volume", mul);
    obs_data_set_double(res, "volume_db", MulToDb(mul));
    obs_data_set_int(res, "sync_offset_ns", obs_source_get_sync_offset(src));
    obs_source_release(src);
    return Finish(res);
}

std::string OpAudioSet(obs_data_t* req)
{
    const char* name = obs_data_get_string(req, "name");
    obs_source_t* src = (name && *name) ? obs_get_source_by_name(name) : nullptr;
    if (!src) return Fail("give the name of an existing source");

    if (obs_data_has_user_value(req, "muted"))
        obs_source_set_muted(src, obs_data_get_bool(req, "muted"));
    if (obs_data_has_user_value(req, "volume_db"))
        obs_source_set_volume(src, DbToMul(obs_data_get_double(req, "volume_db")));
    else if (obs_data_has_user_value(req, "volume"))
        obs_source_set_volume(src, float(obs_data_get_double(req, "volume")));
    if (obs_data_has_user_value(req, "sync_offset_ns"))
        obs_source_set_sync_offset(src, obs_data_get_int(req, "sync_offset_ns"));

    float mul = obs_source_get_volume(src);
    obs_data_t* res = Ok();
    obs_data_set_string(res, "name", obs_source_get_name(src));
    obs_data_set_bool(res, "has_audio", (obs_source_get_output_flags(src) & OBS_SOURCE_AUDIO) != 0);
    obs_data_set_bool(res, "muted", obs_source_muted(src));
    obs_data_set_double(res, "volume", mul);
    obs_data_set_double(res, "volume_db", MulToDb(mul));
    obs_source_release(src);
    return Finish(res);
}

std::string OpFilterList(obs_data_t* req)
{
    const char* name = obs_data_get_string(req, "name");
    obs_source_t* src = (name && *name) ? obs_get_source_by_name(name) : nullptr;
    if (!src) return Fail("give the name of an existing source");

    obs_data_t* res = Ok();
    obs_data_array_t* array = obs_data_array_create();
    FilterCtx ctx { array };
    obs_source_enum_filters(src, EnumFilterCb, &ctx);
    obs_data_set_string(res, "name", obs_source_get_name(src));
    obs_data_set_array(res, "filters", array);
    obs_data_set_int(res, "count", obs_data_array_count(array));
    obs_data_array_release(array);
    obs_source_release(src);
    return Finish(res);
}

std::string OpFilterSet(obs_data_t* req)
{
    const char* name = obs_data_get_string(req, "name");
    obs_source_t* src = (name && *name) ? obs_get_source_by_name(name) : nullptr;
    if (!src) return Fail("give the name of an existing source");

    const char* filterName = obs_data_get_string(req, "filter");
    obs_source_t* filter = (filterName && *filterName)
                               ? obs_source_get_filter_by_name(src, filterName)
                               : nullptr;
    if (!filter) {
        obs_source_release(src);
        return Fail("give the name of a filter on that source");
    }

    if (obs_data_has_user_value(req, "enabled"))
        obs_source_set_enabled(filter, obs_data_get_bool(req, "enabled"));

    const char* json = obs_data_get_string(req, "settings");
    if (json && *json) {
        if (obs_data_t* patch = obs_data_create_from_json(json)) {
            obs_source_update(filter, patch);
            // 映像のソースへの更新はホストが次の描画まで持ち越す。
            // 計算式は渡したその場で通ったかを返さないと直しようがないので、
            // 自前の種別だけは待たずに作り直す。
            ObsShaderFilter::ApplyNow(filter);
            obs_data_release(patch);
        }
    }

    obs_data_t* res = Ok();
    obs_data_set_string(res, "name", obs_source_get_name(src));
    obs_data_set_string(res, "filter", obs_source_get_name(filter));
    obs_data_set_bool(res, "enabled", obs_source_enabled(filter));
    AddShaderStatus(res, filter);
    obs_source_release(filter);
    obs_source_release(src);
    return Finish(res);
}

std::string OpFilterAdd(obs_data_t* req)
{
    const char* name = obs_data_get_string(req, "name");
    obs_source_t* src = (name && *name) ? obs_get_source_by_name(name) : nullptr;
    if (!src) return Fail("give the name of an existing source");

    const char* id = obs_data_get_string(req, "filter_id");
    const char* filterName = obs_data_get_string(req, "filter");
    if (!id || !*id || !filterName || !*filterName) {
        obs_source_release(src);
        return Fail("give both a filter type id and a name for it");
    }
    if (obs_source_t* clash = obs_source_get_filter_by_name(src, filterName)) {
        obs_source_release(clash);
        obs_source_release(src);
        return Fail(std::string("'") + name + "' already has a filter named '" + filterName + "'");
    }

    const char* json = obs_data_get_string(req, "settings");
    obs_data_t* settings = (json && *json) ? obs_data_create_from_json(json) : nullptr;
    obs_source_t* filter = obs_source_create_private(id, filterName, settings);
    if (settings) obs_data_release(settings);
    if (!filter) {
        obs_source_release(src);
        return Fail(std::string("the host would not create a '") + id + "'");
    }

    obs_source_filter_add(src, filter);

    obs_data_t* res = Ok();
    obs_data_set_string(res, "name", obs_source_get_name(src));
    obs_data_set_string(res, "filter", filterName);
    obs_data_set_string(res, "filter_id", id);
    obs_data_set_bool(res, "enabled", obs_source_enabled(filter));
    AddShaderStatus(res, filter);
    obs_source_release(filter);
    obs_source_release(src);
    return Finish(res);
}

std::string OpFilterRemove(obs_data_t* req)
{
    const char* name = obs_data_get_string(req, "name");
    obs_source_t* src = (name && *name) ? obs_get_source_by_name(name) : nullptr;
    if (!src) return Fail("give the name of an existing source");

    const char* filterName = obs_data_get_string(req, "filter");
    obs_source_t* filter = (filterName && *filterName)
                               ? obs_source_get_filter_by_name(src, filterName)
                               : nullptr;
    if (!filter) {
        obs_source_release(src);
        return Fail("give the name of a filter on that source");
    }

    obs_source_filter_remove(src, filter);

    obs_data_t* res = Ok();
    obs_data_set_string(res, "name", obs_source_get_name(src));
    obs_data_set_string(res, "removed", filterName);
    obs_source_release(filter);
    obs_source_release(src);
    return Finish(res);
}

std::string OpSourceAdd(obs_data_t* req)
{
    obs_source_t* sceneSrc = ResolveScene(obs_data_get_string(req, "scene"));
    if (!sceneSrc) return Fail("that scene was not found");

    const char* id = obs_data_get_string(req, "id");
    const char* name = obs_data_get_string(req, "name");
    if (!id || !*id || !name || !*name) {
        obs_source_release(sceneSrc);
        return Fail("give both a source type id and a name");
    }
    if (obs_source_t* clash = obs_get_source_by_name(name)) {
        obs_source_release(clash);
        obs_source_release(sceneSrc);
        return Fail(std::string("a source named '") + name + "' already exists");
    }

    const char* json = obs_data_get_string(req, "settings");
    obs_data_t* settings = (json && *json) ? obs_data_create_from_json(json) : nullptr;
    obs_source_t* created = obs_source_create(id, name, settings, nullptr);
    if (settings) obs_data_release(settings);
    if (!created) {
        obs_source_release(sceneSrc);
        return Fail(std::string("the host would not create a '") + id + "'");
    }

    obs_sceneitem_t* item = obs_scene_add(obs_scene_from_source(sceneSrc), created);

    obs_data_t* res = Ok();
    obs_data_set_string(res, "scene", obs_source_get_name(sceneSrc));
    obs_data_set_string(res, "name", name);
    obs_data_set_string(res, "id", id);
    obs_data_set_int(res, "item_id", item ? obs_sceneitem_get_id(item) : 0);

    obs_source_release(created);
    obs_source_release(sceneSrc);
    return Finish(res);
}

std::string OpSourceRemove(obs_data_t* req)
{
    obs_source_t* sceneSrc = ResolveScene(obs_data_get_string(req, "scene"));
    if (!sceneSrc) return Fail("that scene was not found");

    FindItemCtx find { obs_data_get_string(req, "name"),
                       obs_data_get_int(req, "item_id"), nullptr };
    obs_scene_enum_items(obs_scene_from_source(sceneSrc), FindItemCb, &find);
    if (!find.found) {
        obs_source_release(sceneSrc);
        return Fail("no such item in that scene");
    }

    obs_source_t* itemSrc = obs_sceneitem_get_source(find.found);
    std::string removed = itemSrc ? obs_source_get_name(itemSrc) : "";
    obs_sceneitem_remove(find.found);
    obs_sceneitem_release(find.found);

    obs_data_t* res = Ok();
    obs_data_set_string(res, "scene", obs_source_get_name(sceneSrc));
    obs_data_set_string(res, "removed", removed.c_str());
    obs_source_release(sceneSrc);
    return Finish(res);
}

std::string OpControl(obs_data_t* req)
{
    std::string action = obs_data_get_string(req, "action");
    if (action.empty()) return Fail("give an action");

    if (action == "start_streaming")        obs_frontend_streaming_start();
    else if (action == "stop_streaming")    obs_frontend_streaming_stop();
    else if (action == "start_recording")   obs_frontend_recording_start();
    else if (action == "stop_recording")    obs_frontend_recording_stop();
    else if (action == "pause_recording")   obs_frontend_recording_pause(true);
    else if (action == "resume_recording")  obs_frontend_recording_pause(false);
    else if (action == "split_recording")   obs_frontend_recording_split_file();
    else if (action == "start_replay")      obs_frontend_replay_buffer_start();
    else if (action == "stop_replay")       obs_frontend_replay_buffer_stop();
    else if (action == "save_replay")       obs_frontend_replay_buffer_save();
    else if (action == "start_virtualcam")  obs_frontend_start_virtualcam();
    else if (action == "stop_virtualcam")   obs_frontend_stop_virtualcam();
    else if (action == "studio_mode_on")    obs_frontend_set_preview_program_mode(true);
    else if (action == "studio_mode_off")   obs_frontend_set_preview_program_mode(false);
    else if (action == "transition")        obs_frontend_preview_program_trigger_transition();
    else if (action == "screenshot")        obs_frontend_take_screenshot();
    else if (action == "save")              obs_frontend_save();
    else return Fail("unknown action '" + action + "'");

    obs_data_t* res = Ok();
    obs_data_set_string(res, "action", action.c_str());
    // 起動と停止は頼んだ時点では終わっていない。すぐ読める状態だけ返す。
    obs_data_set_bool(res, "streaming", obs_frontend_streaming_active());
    obs_data_set_bool(res, "recording", obs_frontend_recording_active());
    obs_data_set_bool(res, "replay_buffer", obs_frontend_replay_buffer_active());
    obs_data_set_bool(res, "virtualcam", obs_frontend_virtualcam_active());
    obs_data_set_bool(res, "studio_mode", obs_frontend_preview_program_mode_active());
    return Finish(res);
}

std::string OpEventsPoll(obs_data_t* req)
{
    long long limit = obs_data_get_int(req, "limit");
    if (limit <= 0) limit = 100;

    obs_data_t* res = Ok();
    obs_data_array_t* array = obs_data_array_create();
    {
        std::lock_guard<std::mutex> guard(g_eventLock);
        while (!g_events.empty() && obs_data_array_count(array) < size_t(limit)) {
            obs_data_t* entry = obs_data_create();
            obs_data_set_string(entry, "event", g_events.front().c_str());
            obs_data_array_push_back(array, entry);
            obs_data_release(entry);
            g_events.pop_front();
        }
        obs_data_set_int(res, "remaining", g_events.size());
    }
    obs_data_set_array(res, "events", array);
    obs_data_set_int(res, "count", obs_data_array_count(array));
    obs_data_array_release(array);
    return Finish(res);
}

} // namespace

// ---------------------------------------------------------------------------
// 表口
// ---------------------------------------------------------------------------

namespace ObsOps {

void PushEvent(const char* name)
{
    if (!name || !*name) return;
    std::lock_guard<std::mutex> guard(g_eventLock);
    if (g_events.size() >= kMaxEvents) g_events.pop_front();
    g_events.emplace_back(name);
}

std::string HandleOnUiThread(const std::string& requestJson)
{
    obs_data_t* req = obs_data_create_from_json(requestJson.c_str());
    if (!req) return Fail("the request was not a JSON object");

    std::string op = obs_data_get_string(req, "op");
    std::string out;

    if (op == "info")                   out = OpInfo();
    else if (op == "scene.list")        out = OpSceneList();
    else if (op == "scene.set")         out = OpSceneSet(req);
    else if (op == "source.list")       out = OpSourceList(req);
    else if (op == "source.types")      out = OpSourceTypes();
    else if (op == "sceneitem.list")    out = OpSceneItemList(req);
    else if (op == "sceneitem.set")     out = OpSceneItemSet(req);
    else if (op == "source.settings.get") out = OpSettingsGet(req);
    else if (op == "source.settings.set") out = OpSettingsSet(req);
    else if (op == "source.audio.get")  out = OpAudioGet(req);
    else if (op == "source.audio.set")  out = OpAudioSet(req);
    else if (op == "filter.list")       out = OpFilterList(req);
    else if (op == "filter.set")        out = OpFilterSet(req);
    else if (op == "filter.add")        out = OpFilterAdd(req);
    else if (op == "filter.remove")     out = OpFilterRemove(req);
    else if (op == "source.add")        out = OpSourceAdd(req);
    else if (op == "source.remove")     out = OpSourceRemove(req);
    else if (op == "control")           out = OpControl(req);
    else if (op == "events.poll")       out = OpEventsPoll(req);
    else if (op == "current_scene_name") {
        obs_data_t* res = Ok();
        if (obs_source_t* scene = obs_frontend_get_current_scene()) {
            obs_data_set_string(res, "name", obs_source_get_name(scene));
            obs_source_release(scene);
        }
        out = Finish(res);
    }
    else out = Fail("unknown op '" + op + "'");

    obs_data_release(req);
    return out;
}

// 画面を撮らない。ホストに描かせて、出来上がった画素をそのまま受け取る。
std::string HandleCapture(const std::string& requestJson)
{
    obs_data_t* req = obs_data_create_from_json(requestJson.c_str());
    if (!req) return Fail("the request was not a JSON object");

    std::string name = obs_data_get_string(req, "name");
    obs_data_release(req);
    if (name.empty()) return Fail("give the name of a source or scene to draw");

    obs_source_t* src = obs_get_source_by_name(name.c_str());
    if (!src) return Fail("no source is named '" + name + "'");

    uint32_t cx = obs_source_get_base_width(src);
    uint32_t cy = obs_source_get_base_height(src);
    if (cx == 0 || cy == 0) {
        obs_source_release(src);
        return Fail("'" + name + "' has no size to draw (it may produce sound only)");
    }

    bool copied = false;
    obs_enter_graphics();
    gs_texrender_t* texrender = gs_texrender_create(GS_BGRA, GS_ZS_NONE);
    gs_stagesurf_t* stage = gs_stagesurface_create(cx, cy, GS_BGRA);
    if (texrender && stage && gs_texrender_begin(texrender, cx, cy)) {
        struct vec4 clear {};
        vec4_zero(&clear);
        gs_clear(GS_CLEAR_COLOR, &clear, 0.0f, 0);
        gs_ortho(0.0f, float(cx), 0.0f, float(cy), -100.0f, 100.0f);

        gs_blend_state_push();
        gs_blend_function(GS_BLEND_ONE, GS_BLEND_ZERO);
        obs_source_video_render(src);
        gs_blend_state_pop();

        gs_texrender_end(texrender);

        gs_stage_texture(stage, gs_texrender_get_texture(texrender));

        uint8_t* pixels = nullptr;
        uint32_t linesize = 0;
        if (gs_stagesurface_map(stage, &pixels, &linesize)) {
            std::lock_guard<std::mutex> guard(g_frameLock);
            g_frame.assign(pixels, pixels + size_t(linesize) * cy);
            g_frameW = int(cx);
            g_frameH = int(cy);
            g_framePitch = int(linesize);
            copied = true;
            gs_stagesurface_unmap(stage);
        }
    }
    if (stage) gs_stagesurface_destroy(stage);
    if (texrender) gs_texrender_destroy(texrender);
    obs_leave_graphics();

    obs_source_release(src);

    if (!copied) return Fail("the host drew nothing that could be read back");

    obs_data_t* res = Ok();
    obs_data_set_string(res, "name", name.c_str());
    obs_data_set_int(res, "width", cx);
    obs_data_set_int(res, "height", cy);
    obs_data_set_int(res, "pitch", g_framePitch);
    obs_data_set_int(res, "bytes", int(g_frame.size()));
    return Finish(res);
}

int TakeFrame(unsigned char* out, int outLen, int* width, int* height, int* pitch)
{
    std::lock_guard<std::mutex> guard(g_frameLock);
    if (width) *width = g_frameW;
    if (height) *height = g_frameH;
    if (pitch) *pitch = g_framePitch;

    int need = int(g_frame.size());
    if (!out || outLen < need) return need;

    std::memcpy(out, g_frame.data(), size_t(need));
    return need;
}

} // namespace ObsOps
