#include "ObsShaderFilter.h"

#include <obs-module.h>

#include <cmath>
#include <cstring>
#include <string>

namespace {

const char* const kId = "ngol_shader";
const char* const kSettingShader = "shader";
const char* const kSettingFreezeEvery = "freeze_every";
const char* const kSettingFreezeOffset = "freeze_offset";
const char* const kParamNames[4] = { "p1", "p2", "p3", "p4" };

// ホストが受け付ける形は決まっている。毎回これを書かせると、
// 計算式そのものより定型のほうが長くなり、写し間違いで落ちる。
// technique を含まない文字列にはこちらで前後を付ける。
//
// 書く側が用意するのは render ひとつだけ。
const char* const kPreamble = R"NGOL(uniform float4x4 ViewProj;
uniform texture2d image;
uniform float2 uv_size;
uniform float elapsed;
uniform float p1;
uniform float p2;
uniform float p3;
uniform float p4;

// 或る瞬間に控えておいた絵。freeze_every を立てると使えるようになる。
// 動きの途中で下地が変わってほしくないとき（割れた破片が飛ぶ間など）に使う。
uniform texture2d frozen;
uniform float freeze_age;

sampler_state ngol_sampler {
	Filter    = Linear;
	AddressU  = Clamp;
	AddressV  = Clamp;
};

struct NgolVert {
	float4 pos : POSITION;
	float2 uv  : TEXCOORD0;
};

NgolVert NgolVS(NgolVert v_in)
{
	NgolVert v_out;
	v_out.uv  = v_in.uv;
	v_out.pos = mul(float4(v_in.pos.xyz, 1.0), ViewProj);
	return v_out;
}

float4 sample_at(float2 uv)
{
	return image.Sample(ngol_sampler, uv);
}

float4 sample_frozen(float2 uv)
{
	return frozen.Sample(ngol_sampler, uv);
}

)NGOL";

const char* const kPostamble = R"NGOL(

float4 NgolPS(NgolVert v_in) : TARGET
{
	return render(v_in.uv);
}

technique Draw
{
	pass
	{
		vertex_shader = NgolVS(v_in);
		pixel_shader  = NgolPS(v_in);
	}
}
)NGOL";

int CountLines(const char* text)
{
    int lines = 0;
    for (const char* p = text; *p; p++)
        if (*p == '\n') lines++;
    return lines;
}

struct ShaderData {
    obs_source_t* context = nullptr;
    gs_effect_t* effect = nullptr;

    gs_eparam_t* uvSize = nullptr;
    gs_eparam_t* elapsedParam = nullptr;
    gs_eparam_t* values[4] = {};
    gs_eparam_t* frozenParam = nullptr;
    gs_eparam_t* freezeAgeParam = nullptr;

    // 或る瞬間の絵を控えておく場所。
    // シェーダは前のフレームを覚えられないので、ここで持つしかない。
    gs_texrender_t* keep = nullptr;
    float freezeEvery = 0.0f;    // 何秒ごとに控え直すか。0 なら控えない
    float freezeOffset = 0.0f;   // 周期のどこで控えるか
    float lastPhase = -1.0f;
    float sinceFreeze = 0.0f;
    bool wantKeep = false;
    bool kept = false;

    float elapsed = 0.0f;
    float params[4] = {};

    std::string source;   // 直前に受け取った文字列。同じなら作り直さない
    std::string error;    // 空なら通っている
    int preambleLines = 0;
};

// 文字列から作り直す。描画文脈の内側でしか作れない。
void Rebuild(ShaderData* data, const char* text)
{
    std::string given = text ? text : "";

    // 包むかどうかは technique の有無で決める。丸ごと書きたい向きも通す。
    bool wrapped = given.find("technique") == std::string::npos;
    std::string full;
    int preamble = 0;
    if (wrapped) {
        full = std::string(kPreamble) + given + kPostamble;
        preamble = CountLines(kPreamble);
    } else {
        full = given;
    }

    obs_enter_graphics();

    if (data->effect) {
        gs_effect_destroy(data->effect);
        data->effect = nullptr;
    }
    data->uvSize = nullptr;
    data->elapsedParam = nullptr;
    data->frozenParam = nullptr;
    data->freezeAgeParam = nullptr;
    for (int i = 0; i < 4; i++) data->values[i] = nullptr;

    data->preambleLines = preamble;
    data->error.clear();
    data->kept = false;

    if (given.empty()) {
        data->error = "no shader was given";
    } else {
        char* problem = nullptr;
        // 名前は渡さない。渡すとホストの共有の控えに載り、
        // 作り直しても古いほうが返る。渡す文字列は毎回変わる。
        gs_effect_t* built = gs_effect_create(full.c_str(), nullptr, &problem);
        if (!built) {
            data->error = problem ? problem : "the host would not build it, and said nothing";
        } else {
            data->effect = built;
            data->uvSize = gs_effect_get_param_by_name(built, "uv_size");
            data->elapsedParam = gs_effect_get_param_by_name(built, "elapsed");
            data->frozenParam = gs_effect_get_param_by_name(built, "frozen");
            data->freezeAgeParam = gs_effect_get_param_by_name(built, "freeze_age");
            for (int i = 0; i < 4; i++)
                data->values[i] = gs_effect_get_param_by_name(built, kParamNames[i]);
        }
        if (problem) bfree(problem);
    }

    obs_leave_graphics();

    if (!data->error.empty()) {
        blog(LOG_WARNING, "[NgolForObs] shader would not build: %s", data->error.c_str());
    }
}

const char* GetName(void*)
{
    return obs_module_text("ShaderFilter");
}

void Update(void* raw, obs_data_t* settings)
{
    auto* data = static_cast<ShaderData*>(raw);

    for (int i = 0; i < 4; i++)
        data->params[i] = float(obs_data_get_double(settings, kParamNames[i]));

    data->freezeEvery = float(obs_data_get_double(settings, kSettingFreezeEvery));
    data->freezeOffset = float(obs_data_get_double(settings, kSettingFreezeOffset));

    const char* text = obs_data_get_string(settings, kSettingShader);
    std::string given = text ? text : "";
    // 同じ文字列で作り直すと、時間が巻き戻って動きが止まって見える。
    if (given == data->source && data->effect) return;

    data->source = given;
    data->elapsed = 0.0f;
    Rebuild(data, text);
}

void* Create(obs_data_t* settings, obs_source_t* source)
{
    auto* data = new ShaderData();
    data->context = source;
    Update(data, settings);
    // 通らなくても畳まない。畳むとフィルタごと消え、
    // 何が悪かったのかを読む相手が居なくなる。
    return data;
}

void Destroy(void* raw)
{
    auto* data = static_cast<ShaderData*>(raw);
    if (data->effect || data->keep) {
        obs_enter_graphics();
        if (data->effect) gs_effect_destroy(data->effect);
        if (data->keep) gs_texrender_destroy(data->keep);
        obs_leave_graphics();
    }
    delete data;
}

// いま下地が描いているものを 1 枚控える。
//
// 描く処理の中から呼ぶこと。時計の刻みの側から呼んでも何も描かれない。
// 下地が親そのもの（このフィルタが列の先頭）のときは、
// obs_source_video_render を呼ぶとフィルタ列へ入り直してしまい、
// 何も描かれないまま帰ってくる。そのときは既定の描き方を直に呼ぶ。
// ホスト同梱の gpu-delay が同じ形で控えている。
void KeepOne(ShaderData* data)
{
    obs_source_t* target = obs_filter_get_target(data->context);
    obs_source_t* parent = obs_filter_get_parent(data->context);
    if (!target || !parent) return;

    uint32_t w = obs_source_get_base_width(target);
    uint32_t h = obs_source_get_base_height(target);
    if (w == 0 || h == 0) return;

    if (!data->keep) data->keep = gs_texrender_create(GS_RGBA, GS_ZS_NONE);
    if (!data->keep) return;

    gs_texrender_reset(data->keep);

    // 混ぜ方を固定してから描かせる。直前に誰が何を設定したか分からない。
    gs_blend_state_push();
    gs_blend_function(GS_BLEND_ONE, GS_BLEND_ZERO);

    if (gs_texrender_begin(data->keep, w, h)) {
        uint32_t flags = obs_source_get_output_flags(target);
        bool ownDraw = (flags & OBS_SOURCE_CUSTOM_DRAW) != 0;
        bool later = (flags & OBS_SOURCE_ASYNC) != 0;

        struct vec4 blank;
        vec4_zero(&blank);
        gs_clear(GS_CLEAR_COLOR, &blank, 0.0f, 0);
        gs_ortho(0.0f, float(w), 0.0f, float(h), -100.0f, 100.0f);

        if (target == parent && !ownDraw && !later)
            obs_source_default_render(target);
        else
            obs_source_video_render(target);

        gs_texrender_end(data->keep);
        data->kept = true;
    }

    gs_blend_state_pop();
}

void Tick(void* raw, float seconds)
{
    auto* data = static_cast<ShaderData*>(raw);
    data->elapsed += seconds;
    data->sinceFreeze += seconds;

    if (data->freezeEvery <= 0.0f) return;

    // 周期の決まった位置を通り過ぎた瞬間に控え直す。
    // ここでは合図を立てるだけ。実際に控えるのは描く処理の中。
    float phase = fmodf(data->elapsed, data->freezeEvery);
    bool crossed = (data->lastPhase >= 0.0f)
                   && ((data->lastPhase < data->freezeOffset && phase >= data->freezeOffset)
                       || phase < data->lastPhase);
    if (!data->kept || crossed) {
        data->wantKeep = true;
        data->sinceFreeze = 0.0f;
    }
    data->lastPhase = phase;
}

void Render(void* raw, gs_effect_t*)
{
    auto* data = static_cast<ShaderData*>(raw);

    // 作れていないときは下地をそのまま通す。
    // 黒くしてしまうと、載っていないのか壊れているのかが見分けられない。
    if (!data->effect) {
        obs_source_skip_video_filter(data->context);
        return;
    }

    // 控えは下地を描かせる処理なので、フィルタの描画を始める前に済ませる。
    if (data->wantKeep) {
        KeepOne(data);
        data->wantKeep = false;
    }

    if (!obs_source_process_filter_begin(data->context, GS_RGBA, OBS_ALLOW_DIRECT_RENDERING))
        return;

    if (data->uvSize) {
        obs_source_t* target = obs_filter_get_target(data->context);
        struct vec2 size;
        vec2_set(&size, float(obs_source_get_width(target)), float(obs_source_get_height(target)));
        gs_effect_set_vec2(data->uvSize, &size);
    }
    if (data->elapsedParam) gs_effect_set_float(data->elapsedParam, data->elapsed);
    if (data->freezeAgeParam) gs_effect_set_float(data->freezeAgeParam, data->sinceFreeze);
    if (data->frozenParam && data->keep) {
        gs_texture_t* kept = gs_texrender_get_texture(data->keep);
        if (kept) gs_effect_set_texture(data->frozenParam, kept);
    }
    for (int i = 0; i < 4; i++)
        if (data->values[i]) gs_effect_set_float(data->values[i], data->params[i]);

    gs_blend_state_push();
    gs_blend_function(GS_BLEND_ONE, GS_BLEND_INVSRCALPHA);

    obs_source_process_filter_end(data->context, data->effect, 0, 0);

    gs_blend_state_pop();
}

void Defaults(obs_data_t* settings)
{
    obs_data_set_default_string(settings, kSettingShader, "");
    obs_data_set_default_double(settings, kSettingFreezeEvery, 0.0);
    obs_data_set_default_double(settings, kSettingFreezeOffset, 0.0);
    for (int i = 0; i < 4; i++)
        obs_data_set_default_double(settings, kParamNames[i], 0.0);
}

obs_properties_t* Properties(void*)
{
    obs_properties_t* props = obs_properties_create();
    obs_properties_add_text(props, kSettingShader, "Shader", OBS_TEXT_MULTILINE);
    obs_properties_add_float(props, kSettingFreezeEvery, "Keep a copy every (s)", 0.0, 3600.0, 0.1);
    obs_properties_add_float(props, kSettingFreezeOffset, "...at this point in the cycle (s)",
                             0.0, 3600.0, 0.1);
    for (int i = 0; i < 4; i++)
        obs_properties_add_float(props, kParamNames[i], kParamNames[i], -10000.0, 10000.0, 0.001);
    return props;
}

struct obs_source_info g_info = {
    .id = kId,
    .type = OBS_SOURCE_TYPE_FILTER,
    .output_flags = OBS_SOURCE_VIDEO | OBS_SOURCE_SRGB,
    .get_name = GetName,
    .create = Create,
    .destroy = Destroy,
    .get_defaults = Defaults,
    .get_properties = Properties,
    .update = Update,
    .video_tick = Tick,
    .video_render = Render,
};

} // namespace

namespace ObsShaderFilter {

void Register()
{
    obs_register_source(&g_info);
}

ShaderData* DataOf(obs_source_t* filter)
{
    if (!filter) return nullptr;
    const char* id = obs_source_get_id(filter);
    if (!id || strcmp(id, kId) != 0) return nullptr;
    return static_cast<ShaderData*>(obs_obj_get_data(filter));
}

bool ApplyNow(obs_source_t* filter)
{
    ShaderData* data = DataOf(filter);
    if (!data) return false;

    obs_data_t* settings = obs_source_get_settings(filter);
    Update(data, settings);
    obs_data_release(settings);
    return true;
}

bool Status(obs_source_t* filter, std::string& error, int& preambleLines)
{
    ShaderData* data = DataOf(filter);
    if (!data) return false;

    error = data->error;
    preambleLines = data->preambleLines;
    return true;
}

}
