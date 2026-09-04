#include "ObsFrameSource.h"

#include <obs-module.h>

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <string>
#include <vector>

namespace {

const char* const kId = "ngol_frame";
const char* const kSettingName = "name";

// 見出しの並び。置く側（NgolSharedFrame.cs）と同じ位置を指していること。
const uint32_t kMagic0 = 0x4C4F474E;   // "NGOL"
const uint32_t kMagic1 = 0x004D5246;   // "FRM\0"
const uint32_t kVersion = 1;
const size_t kHeaderBytes = 64;

struct Header {
    uint32_t magic0;
    uint32_t magic1;
    uint32_t version;
    uint32_t width;
    uint32_t height;
    uint32_t stride;
    uint32_t format;
    uint32_t sequence;
    uint32_t byteCount;
};

struct FrameData {
    obs_source_t* context = nullptr;
    std::string name;

    HANDLE mapping = nullptr;
    const uint8_t* view = nullptr;

    gs_texture_t* texture = nullptr;
    uint32_t width = 0;
    uint32_t height = 0;
    uint32_t lastSequence = 0;

    std::vector<uint8_t> scratch;
};

void CloseMapping(FrameData* data)
{
    if (data->view) { UnmapViewOfFile(data->view); data->view = nullptr; }
    if (data->mapping) { CloseHandle(data->mapping); data->mapping = nullptr; }
}

// 置き場を開く。無くても失敗ではない--まだ誰も置いていないだけのことがある。
void OpenMapping(FrameData* data)
{
    CloseMapping(data);
    if (data->name.empty()) return;

    std::string path = "Local\\ngol.frame." + data->name;
    HANDLE mapping = OpenFileMappingA(FILE_MAP_READ, FALSE, path.c_str());
    if (!mapping) return;

    const void* view = MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, 0);
    if (!view) { CloseHandle(mapping); return; }

    data->mapping = mapping;
    data->view = static_cast<const uint8_t*>(view);
}

// 見出しを 1 つ写す。置き場が無い・目印が違う・版が違うなら false。
bool ReadHeader(FrameData* data, Header& out)
{
    if (!data->view) return false;
    memcpy(&out, data->view, sizeof(Header));
    if (out.magic0 != kMagic0 || out.magic1 != kMagic1) return false;
    if (out.version != kVersion) return false;
    if (out.width == 0 || out.height == 0) return false;
    if (out.stride < out.width * 4) return false;
    if (out.byteCount < out.stride * out.height) return false;
    return true;
}

void ReleaseTexture(FrameData* data)
{
    if (!data->texture) return;
    obs_enter_graphics();
    gs_texture_destroy(data->texture);
    obs_leave_graphics();
    data->texture = nullptr;
    data->width = 0;
    data->height = 0;
}

const char* GetName(void*)
{
    return obs_module_text("SharedFrameSource");
}

void Update(void* raw, obs_data_t* settings)
{
    auto* data = static_cast<FrameData*>(raw);
    const char* name = obs_data_get_string(settings, kSettingName);
    std::string given = name ? name : "";
    if (given == data->name && data->mapping) return;

    data->name = given;
    data->lastSequence = 0;
    ReleaseTexture(data);
    OpenMapping(data);
}

void* Create(obs_data_t* settings, obs_source_t* source)
{
    auto* data = new FrameData();
    data->context = source;
    Update(data, settings);
    return data;
}

void Destroy(void* raw)
{
    auto* data = static_cast<FrameData*>(raw);
    ReleaseTexture(data);
    CloseMapping(data);
    delete data;
}

uint32_t GetWidth(void* raw) { return static_cast<FrameData*>(raw)->width; }
uint32_t GetHeight(void* raw) { return static_cast<FrameData*>(raw)->height; }

// 置き場を毎フレーム見る。置く側とは待ち合わせをしないので、
// 書いている最中のものを掴まないよう、通し番号を前後で読んで挟む。
// 掴み損ねたら前の 1 枚をそのまま出す--ここで待つと描画そのものが止まる。
void Tick(void* raw, float)
{
    auto* data = static_cast<FrameData*>(raw);

    // 置く側が後から現れることがある。開けていなければ毎回試す。
    if (!data->view) {
        OpenMapping(data);
        if (!data->view) return;
    }

    Header head{};
    if (!ReadHeader(data, head)) {
        // 置き場ごと作り直されたかもしれない。開き直して次の機会にする。
        CloseMapping(data);
        ReleaseTexture(data);
        return;
    }

    if (head.sequence == data->lastSequence) return;   // まだ同じ絵
    if ((head.sequence & 1u) != 0u) return;            // いま書かれている

    const size_t need = head.stride * size_t(head.height);
    if (data->scratch.size() < need) data->scratch.resize(need);
    memcpy(data->scratch.data(), data->view + kHeaderBytes, need);

    Header after{};
    if (!ReadHeader(data, after)) return;
    if (after.sequence != head.sequence) return;       // 写している間に変わった

    obs_enter_graphics();
    if (!data->texture || data->width != head.width || data->height != head.height) {
        if (data->texture) gs_texture_destroy(data->texture);
        data->texture = gs_texture_create(head.width, head.height, GS_BGRA, 1, nullptr, GS_DYNAMIC);
        data->width = head.width;
        data->height = head.height;
    }
    if (data->texture)
        gs_texture_set_image(data->texture, data->scratch.data(), head.stride, false);
    obs_leave_graphics();

    data->lastSequence = head.sequence;
}

// 渡された効果に絵を差して描く。
//
// 自前で描く印を立てると、ホストは効果を用意せずに呼んでくる。
// その状態で描画の補助を呼んでも何も出ない--大きさも中身も正しいまま
// 真っ黒になるので、渡す側を疑って時間を使うことになる。
void Render(void* raw, gs_effect_t* effect)
{
    auto* data = static_cast<FrameData*>(raw);
    if (!data->texture || !effect) return;

    gs_blend_state_push();
    gs_blend_function(GS_BLEND_ONE, GS_BLEND_INVSRCALPHA);

    gs_eparam_t* image = gs_effect_get_param_by_name(effect, "image");
    if (image) gs_effect_set_texture(image, data->texture);
    gs_draw_sprite(data->texture, 0, data->width, data->height);

    gs_blend_state_pop();
}

void Defaults(obs_data_t* settings)
{
    obs_data_set_default_string(settings, kSettingName, "");
}

obs_properties_t* Properties(void*)
{
    obs_properties_t* props = obs_properties_create();
    obs_properties_add_text(props, kSettingName, "Shared name", OBS_TEXT_DEFAULT);
    return props;
}

struct obs_source_info g_info = {
    .id = kId,
    .type = OBS_SOURCE_TYPE_INPUT,
    .output_flags = OBS_SOURCE_VIDEO,
    .get_name = GetName,
    .create = Create,
    .destroy = Destroy,
    .get_width = GetWidth,
    .get_height = GetHeight,
    .get_defaults = Defaults,
    .get_properties = Properties,
    .update = Update,
    .video_tick = Tick,
    .video_render = Render,
};

} // namespace

namespace ObsFrameSource {

void Register()
{
    obs_register_source(&g_info);
}

}
