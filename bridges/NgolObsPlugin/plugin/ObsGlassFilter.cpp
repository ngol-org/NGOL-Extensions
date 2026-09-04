#include "ObsGlassFilter.h"

#include <obs-module.h>

#include <algorithm>
#include <cmath>
#include <cstring>
#include <vector>

namespace {

const char* const kId = "ngol_glass";

// 板を貼って描くだけの効果。画素ごとの色は頂点の色で加減する
// （破片ごとに陰を付けたいので、ホスト既定の効果では足りない）。
const char* const kDrawEffect = R"NGOL(uniform float4x4 ViewProj;
uniform texture2d image;

sampler_state ngol_sampler {
	Filter    = Linear;
	AddressU  = Clamp;
	AddressV  = Clamp;
};

struct VertIn {
	float4 pos : POSITION;
	float2 uv  : TEXCOORD0;
	float4 col : COLOR;
};

struct VertOut {
	float4 pos : POSITION;
	float2 uv  : TEXCOORD0;
	float4 col : COLOR;
};

VertOut NgolVS(VertIn v_in)
{
	VertOut v_out;
	v_out.pos = mul(float4(v_in.pos.xyz, 1.0), ViewProj);
	v_out.uv  = v_in.uv;
	v_out.col = v_in.col;
	return v_out;
}

float4 NgolPS(VertOut v_in) : TARGET
{
	float4 c = image.Sample(ngol_sampler, v_in.uv);
	return float4(saturate(c.rgb * v_in.col.rgb), c.a * v_in.col.a);
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

float Hash(float a, float b)
{
    float n = sinf(a * 269.5f + b * 183.3f) * 43758.5453f;
    return n - floorf(n);
}

struct Vec2 { float x, y; };
struct Vec3 { float x, y, z; };

// 破片 1 枚。形は割れた瞬間の絵の上の座標で持つ（以後変わらない）。
struct Shard {
    std::vector<Vec2> ring;     // 縁を順に並べたもの
    Vec2 mid{};                 // 元あった場所の中心
    Vec3 push{};                // 弾かれる向きと強さ
    Vec3 axis{};                // 倒れる軸
    float spin = 0.0f;
    float delay = 0.0f;
    float radius = 0.0f;
};

struct GlassData {
    obs_source_t* context = nullptr;

    // 割れた瞬間の絵
    gs_texrender_t* keep = nullptr;
    bool kept = false;

    gs_effect_t* effect = nullptr;
    gs_vertbuffer_t* mesh = nullptr;
    size_t meshRoom = 0;

    std::vector<Shard> shards;
    std::vector<size_t> order;

    float elapsed = 0.0f;
    float lastPhase = -1.0f;
    float lap = -1.0f;
    uint32_t cx = 0, cy = 0;

    // 設定
    float cycle = 8.0f;
    float hold = 1.0f;
    float spokes = 13.0f;
    float rings = 7.0f;
    float burst = 0.55f;
    float spin = 3.0f;
    float gravity = 0.9f;
};

// 多角形を 1 本の直線で切る（枠の内側だけ残す）。
// 内側の判定は「その辺のどちら側か」だけで済む。
std::vector<Vec2> ClipTo(const std::vector<Vec2>& poly, int edge)
{
    auto inside = [&](const Vec2& p) {
        if (edge == 0) return p.x >= 0.0f;
        if (edge == 1) return p.x <= 1.0f;
        if (edge == 2) return p.y >= 0.0f;
        return p.y <= 1.0f;
    };
    auto cut = [&](const Vec2& a, const Vec2& b) {
        float t;
        if (edge == 0)      t = (0.0f - a.x) / (b.x - a.x);
        else if (edge == 1) t = (1.0f - a.x) / (b.x - a.x);
        else if (edge == 2) t = (0.0f - a.y) / (b.y - a.y);
        else                t = (1.0f - a.y) / (b.y - a.y);
        return Vec2{ a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t };
    };

    std::vector<Vec2> out;
    if (poly.empty()) return out;
    for (size_t i = 0; i < poly.size(); i++) {
        const Vec2& cur = poly[i];
        const Vec2& prev = poly[(i + poly.size() - 1) % poly.size()];
        bool curIn = inside(cur);
        bool prevIn = inside(prev);
        if (curIn) {
            if (!prevIn) out.push_back(cut(prev, cur));
            out.push_back(cur);
        } else if (prevIn) {
            out.push_back(cut(prev, cur));
        }
    }
    return out;
}

// 絵の外へはみ出した分を落とす。
//
// 落とさないと、枠の外の破片が端の色を引き伸ばした無地の板になり、
// 画面の上から降ってくる。窓ガラスは枠の外に無い。
std::vector<Vec2> ClipToPicture(const std::vector<Vec2>& poly)
{
    std::vector<Vec2> work = poly;
    for (int edge = 0; edge < 4 && !work.empty(); edge++)
        work = ClipTo(work, edge);
    return work;
}

// 割れ方を組む。
//
// ガラスは当たった点から放射状にひびが走り、それを輪が横切る形に割れる。
// 輪の間隔は中心で詰まり外へ行くほど広がる--中心は粉、外は大きな板になる。
// 等間隔の格子で切ると大きさが揃い、タイルが剥がれたようにしか見えない。
void BuildShards(GlassData* data, Vec2 impact, float aspect)
{
    data->shards.clear();

    const int spokes = std::max(4, int(data->spokes));
    const int rings = std::max(2, int(data->rings));
    const int arcSteps = 3;          // 弧をこれだけに割る。直線に見せないため
    const float reach = 1.6f;        // 画面の隅まで届く長さ

    auto angleAt = [&](int s) {
        float base = float(s) / float(spokes);
        // ひびは等間隔に走らない。境目ごとにずらす。
        float jitter = (Hash(float(s), 3.1f) - 0.5f) * 0.8f / float(spokes);
        return (base + jitter) * 6.2831853f;
    };
    auto radiusAt = [&](int k) {
        if (k <= 0) return 0.0f;
        // 外ほど間隔を広げる
        return powf(float(k) / float(rings), 1.0f / 0.55f) * reach;
    };
    auto pointAt = [&](float ang, float r) {
        return Vec2{ impact.x + cosf(ang) * r / aspect, impact.y + sinf(ang) * r };
    };

    for (int s = 0; s < spokes; s++) {
        float a0 = angleAt(s);
        float a1 = angleAt(s + 1);
        if (a1 <= a0) a1 += 6.2831853f;

        for (int k = 0; k < rings; k++) {
            float rIn = radiusAt(k);
            float rOut = radiusAt(k + 1);
            // 輪も凹凸させる。まん丸だと波紋にしか見えない。
            float wobbleIn = 1.0f + (Hash(float(s), float(k)) - 0.5f) * 0.28f;
            float wobbleOut = 1.0f + (Hash(float(s), float(k) + 1.0f) - 0.5f) * 0.28f;
            rIn *= wobbleIn;
            rOut *= wobbleOut;
            if (rOut <= rIn + 0.0005f) continue;

            Shard shard;
            for (int i = 0; i <= arcSteps; i++) {
                float a = a0 + (a1 - a0) * (float(i) / float(arcSteps));
                shard.ring.push_back(pointAt(a, rOut));
            }
            if (rIn > 0.0f) {
                for (int i = arcSteps; i >= 0; i--) {
                    float a = a0 + (a1 - a0) * (float(i) / float(arcSteps));
                    shard.ring.push_back(pointAt(a, rIn));
                }
            } else {
                shard.ring.push_back(impact);
            }

            // 枠の外は捨てる。切った結果が三角にならなければその破片は無い。
            shard.ring = ClipToPicture(shard.ring);
            if (shard.ring.size() < 3) continue;

            // 中心は切ったあとの形で取り直す。切る前の中心だと枠の外を指しうる。
            Vec2 centre{ 0.0f, 0.0f };
            for (const Vec2& v : shard.ring) { centre.x += v.x; centre.y += v.y; }
            centre.x /= float(shard.ring.size());
            centre.y /= float(shard.ring.size());

            float aMid = (a0 + a1) * 0.5f;
            float rMid = (rIn + rOut) * 0.5f;
            shard.mid = centre;
            shard.radius = rMid;

            // 中心に近いほど強く弾かれる
            float strength = data->burst * (0.30f + 1.00f * expf(-rMid * 2.6f));
            shard.push = Vec3{ cosf(aMid) / aspect * strength,
                               sinf(aMid) * strength,
                               (Hash(float(s) + 5.0f, float(k)) - 0.35f) * strength * 0.9f };

            float ax = Hash(float(s) + 11.0f, float(k)) - 0.5f;
            float ay = Hash(float(s) + 21.0f, float(k)) - 0.5f;
            float az = (Hash(float(s) + 31.0f, float(k)) - 0.5f) * 0.4f;
            float len = sqrtf(ax * ax + ay * ay + az * az);
            if (len < 0.0001f) { ax = 1.0f; ay = 0.0f; az = 0.0f; len = 1.0f; }
            shard.axis = Vec3{ ax / len, ay / len, az / len };
            shard.spin = (Hash(float(s) + 41.0f, float(k)) - 0.5f) * 2.0f * data->spin;

            // ほぼ同時に割れる。ばらつかせすぎると崩れ落ちるように見える。
            shard.delay = rMid * 0.06f + Hash(float(s) + 51.0f, float(k)) * 0.05f;

            data->shards.push_back(std::move(shard));
        }
    }
}

// いま下地が描いているものを 1 枚控える。
//
// 描く処理の中から呼ぶこと。下地が親そのもの（このフィルタが列の先頭）
// のときは obs_source_video_render がフィルタ列へ入り直してしまうので、
// 既定の描き方を直に呼ぶ。ホスト同梱の gpu-delay と同じ形。
void KeepOne(GlassData* data)
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
        data->cx = w;
        data->cy = h;
    }

    gs_blend_state_pop();
}

// 軸まわりに回す（ロドリゲス）
Vec3 Turn(Vec3 v, Vec3 axis, float ang)
{
    float c = cosf(ang), s = sinf(ang);
    float dot = axis.x * v.x + axis.y * v.y + axis.z * v.z;
    Vec3 cross{ axis.y * v.z - axis.z * v.y,
                axis.z * v.x - axis.x * v.z,
                axis.x * v.y - axis.y * v.x };
    return Vec3{ v.x * c + cross.x * s + axis.x * dot * (1.0f - c),
                 v.y * c + cross.y * s + axis.y * dot * (1.0f - c),
                 v.z * c + cross.z * s + axis.z * dot * (1.0f - c) };
}

const char* GetName(void*)
{
    return obs_module_text("GlassFilter");
}

void Update(void* raw, obs_data_t* settings)
{
    auto* data = static_cast<GlassData*>(raw);
    data->cycle = float(obs_data_get_double(settings, "cycle"));
    data->hold = float(obs_data_get_double(settings, "hold"));
    data->spokes = float(obs_data_get_double(settings, "spokes"));
    data->rings = float(obs_data_get_double(settings, "rings"));
    data->burst = float(obs_data_get_double(settings, "burst"));
    data->spin = float(obs_data_get_double(settings, "spin"));
    data->gravity = float(obs_data_get_double(settings, "gravity"));
    if (data->cycle < 2.0f) data->cycle = 2.0f;
    data->lap = -1.0f;      // 設定が変わったら組み直す
}

void* Create(obs_data_t* settings, obs_source_t* source)
{
    auto* data = new GlassData();
    data->context = source;
    Update(data, settings);
    return data;
}

void Destroy(void* raw)
{
    auto* data = static_cast<GlassData*>(raw);
    obs_enter_graphics();
    if (data->keep) gs_texrender_destroy(data->keep);
    if (data->effect) gs_effect_destroy(data->effect);
    if (data->mesh) gs_vertexbuffer_destroy(data->mesh);
    obs_leave_graphics();
    delete data;
}

void Tick(void* raw, float seconds)
{
    static_cast<GlassData*>(raw)->elapsed += seconds;
}

// 頂点の置き場を要るぶんだけ用意する
bool Room(GlassData* data, size_t need)
{
    if (data->mesh && data->meshRoom >= need) return true;
    if (data->mesh) { gs_vertexbuffer_destroy(data->mesh); data->mesh = nullptr; }

    struct gs_vb_data* vb = gs_vbdata_create();
    vb->num = need;
    vb->points = (struct vec3*)bzalloc(sizeof(struct vec3) * need);
    vb->colors = (uint32_t*)bzalloc(sizeof(uint32_t) * need);
    vb->num_tex = 1;
    vb->tvarray = (struct gs_tvertarray*)bzalloc(sizeof(struct gs_tvertarray));
    vb->tvarray[0].width = 2;
    vb->tvarray[0].array = bzalloc(sizeof(float) * 2 * need);

    data->mesh = gs_vertexbuffer_create(vb, GS_DYNAMIC);
    data->meshRoom = data->mesh ? need : 0;
    return data->mesh != nullptr;
}

void Render(void* raw, gs_effect_t*)
{
    auto* data = static_cast<GlassData*>(raw);

    float phase = fmodf(data->elapsed, data->cycle);
    float t = phase - data->hold;
    float lap = floorf(data->elapsed / data->cycle);

    // 割れる前はそのまま通す
    if (t <= 0.0f) {
        data->lastPhase = phase;
        if (lap != data->lap) data->kept = false;
        obs_source_skip_video_filter(data->context);
        return;
    }

    // この周でまだ控えていなければ、いまの絵を控えて割れ方を組む
    if (!data->kept || lap != data->lap) {
        KeepOne(data);
        if (!data->kept) { obs_source_skip_video_filter(data->context); return; }
        data->lap = lap;
        float aspect = (data->cy > 0) ? float(data->cx) / float(data->cy) : 1.0f;
        Vec2 impact{ 0.30f + 0.40f * Hash(lap, 2.0f), 0.28f + 0.36f * Hash(lap, 9.0f) };
        BuildShards(data, impact, aspect);
    }

    gs_texture_t* kept = gs_texrender_get_texture(data->keep);
    if (!kept || data->shards.empty()) {
        obs_source_skip_video_filter(data->context);
        return;
    }

    if (!data->effect) {
        char* problem = nullptr;
        data->effect = gs_effect_create(kDrawEffect, nullptr, &problem);
        if (problem) {
            blog(LOG_ERROR, "[NgolForObs] glass draw effect: %s", problem);
            bfree(problem);
        }
    }
    if (!data->effect) { obs_source_skip_video_filter(data->context); return; }

    const float aspect = (data->cy > 0) ? float(data->cx) / float(data->cy) : 1.0f;
    const float focal = 1.8f;

    // 破片ごとの姿勢を出し、奥のものから順に描けるよう並べ替える。
    // 前後を持たない画素の式では、これができない。
    struct Placed { size_t index; float depth; float shade; float alpha; Vec3 move; float ang; };
    std::vector<Placed> placed;
    placed.reserve(data->shards.size());

    for (size_t i = 0; i < data->shards.size(); i++) {
        const Shard& sh = data->shards[i];
        float ft = t - sh.delay;
        if (ft <= 0.0f) ft = 0.0f;

        float slow = expf(-ft * 1.7f);
        Vec3 move{ sh.push.x * ft * slow,
                   sh.push.y * ft * slow + 0.5f * data->gravity * ft * ft,
                   sh.push.z * ft * slow };

        float ang = sh.spin * ft;

        // 面の向き。真横を向くほど暗く、そこを過ぎる瞬間に光る。
        Vec3 face = Turn(Vec3{ 0.0f, 0.0f, 1.0f }, sh.axis, ang);
        float facing = fabsf(face.z);
        float shade = 0.42f + 0.58f * facing;
        float glint = powf(1.0f - facing, 10.0f);
        shade += glint * 0.9f;

        float alpha = 1.0f;
        float below = (sh.mid.y + move.y) - 1.35f;
        if (below > 0.0f) alpha = std::max(0.0f, 1.0f - below * 4.0f);

        placed.push_back(Placed{ i, move.z, shade, alpha, move, ang });
    }

    // 奥（手前へ来ていないもの）から先に描く
    std::sort(placed.begin(), placed.end(),
              [](const Placed& a, const Placed& b) { return a.depth < b.depth; });

    size_t need = 0;
    for (const auto& p : placed) {
        if (p.alpha <= 0.0f) continue;
        need += (data->shards[p.index].ring.size() - 2) * 3;
    }
    if (need == 0 || !Room(data, need)) {
        obs_source_skip_video_filter(data->context);
        return;
    }

    struct gs_vb_data* vb = gs_vertexbuffer_get_data(data->mesh);
    float* uvs = static_cast<float*>(vb->tvarray[0].array);
    size_t at = 0;

    for (const auto& p : placed) {
        if (p.alpha <= 0.0f) continue;
        const Shard& sh = data->shards[p.index];

        uint32_t colour;
        {
            int v = int(std::min(1.0f, p.shade) * 255.0f + 0.5f);
            int a = int(std::min(1.0f, p.alpha) * 255.0f + 0.5f);
            colour = (uint32_t(a) << 24) | (uint32_t(v) << 16) | (uint32_t(v) << 8) | uint32_t(v);
        }

        // 縁を扇状に三角へ割る
        auto place = [&](const Vec2& src) {
            Vec3 local{ (src.x - sh.mid.x) * aspect, src.y - sh.mid.y, 0.0f };
            Vec3 spun = Turn(local, sh.axis, p.ang);
            float z = spun.z + p.move.z;
            float shrink = focal / std::max(0.05f, focal + z);
            float sx = sh.mid.x + p.move.x + spun.x * shrink / aspect;
            float sy = sh.mid.y + p.move.y + spun.y * shrink;

            vb->points[at].x = sx * float(data->cx);
            vb->points[at].y = sy * float(data->cy);
            vb->points[at].z = 0.0f;
            uvs[at * 2 + 0] = src.x;
            uvs[at * 2 + 1] = src.y;
            vb->colors[at] = colour;
            at++;
        };

        for (size_t k = 1; k + 1 < sh.ring.size(); k++) {
            place(sh.ring[0]);
            place(sh.ring[k]);
            place(sh.ring[k + 1]);
        }
    }

    vb->num = at;
    vb->tvarray[0].width = 2;
    gs_vertexbuffer_flush(data->mesh);

    gs_blend_state_push();
    gs_blend_function(GS_BLEND_SRCALPHA, GS_BLEND_INVSRCALPHA);
    gs_enable_depth_test(false);
    gs_set_cull_mode(GS_NEITHER);          // 裏返っても消えないように

    gs_effect_set_texture(gs_effect_get_param_by_name(data->effect, "image"), kept);
    gs_load_vertexbuffer(data->mesh);
    while (gs_effect_loop(data->effect, "Draw"))
        gs_draw(GS_TRIS, 0, uint32_t(at));
    gs_load_vertexbuffer(nullptr);

    gs_blend_state_pop();
}

void Defaults(obs_data_t* settings)
{
    obs_data_set_default_double(settings, "cycle", 8.0);
    obs_data_set_default_double(settings, "hold", 1.0);
    obs_data_set_default_double(settings, "spokes", 13.0);
    obs_data_set_default_double(settings, "rings", 7.0);
    obs_data_set_default_double(settings, "burst", 0.55);
    obs_data_set_default_double(settings, "spin", 3.0);
    obs_data_set_default_double(settings, "gravity", 0.9);
}

obs_properties_t* Properties(void*)
{
    obs_properties_t* props = obs_properties_create();
    obs_properties_add_float(props, "cycle", "Breaks every (s)", 2.0, 600.0, 0.5);
    obs_properties_add_float(props, "hold", "Shows intact for (s)", 0.0, 60.0, 0.1);
    obs_properties_add_float(props, "spokes", "Cracks around", 4.0, 64.0, 1.0);
    obs_properties_add_float(props, "rings", "Rings across", 2.0, 32.0, 1.0);
    obs_properties_add_float(props, "burst", "Thrown out with", 0.0, 4.0, 0.05);
    obs_properties_add_float(props, "spin", "Tumbles at", 0.0, 20.0, 0.1);
    obs_properties_add_float(props, "gravity", "Falls at", 0.0, 8.0, 0.05);
    return props;
}

struct obs_source_info g_info = {
    .id = kId,
    .type = OBS_SOURCE_TYPE_FILTER,
    .output_flags = OBS_SOURCE_VIDEO,
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

namespace ObsGlassFilter {

void Register()
{
    obs_register_source(&g_info);
}

}
