#ifndef WEFT_UNITY_H
#define WEFT_UNITY_H

#include "weft/weft.h"

#ifdef __cplusplus
extern "C" {
#endif

/* The plugin's own C functions, for Unity's C# side (Runtime/WeftNative.cs). Every one may be called from any thread.
 * Those marked "owner" run on the plugin's owner thread, a thread of its own with a 16 MiB stack (weft's pages need 8
 * MiB for React and D3, Unity's main thread has 1), and return once it has run them; weft's own functions marked "any
 * thread" (emit, set_model, complete_call, write_anchors, acquire_frame) are called directly with weft_unity_runtime.
 * Strings are UTF-8 with an explicit size, borrowed during the call. */

/* The weft commit the DLL was built from ("+changes" for a checkout with changes). Static text. */
const char* weft_unity_build(void);

/* Owner: starts the runtime (flags WEFT_RUNTIME_*, max_views 0 for weft's default, 64). WEFT_BUSY when it runs already. */
weft_result weft_unity_start(uint32_t flags, uint32_t max_views);
/* Owner: destroys the Views the plugin made and the runtime; draws queued and not yet drawn are dropped. Unity's
 * domain reloads and quitting call it, so that the next play starts on a new runtime (fonts are added before the
 * first page of a runtime). */
void weft_unity_stop(void);
/* The running runtime, NULL when stopped. */
weft_runtime* weft_unity_runtime(void);

/* Owner: fonts, before the runtime's first page loads (weft_runtime_add_font and the others). */
weft_result weft_unity_add_font(const weft_font_face* face);
weft_result weft_unity_set_generic_family(const char* generic, size_t generic_size, const char* family, size_t family_size);
weft_result weft_unity_add_fallback_family(const char* family, size_t family_size);

/* Owner: custom UI shader effects (background-image: -weft-effect(name)). */
weft_result weft_unity_add_effect(const weft_effect_desc* effect);

/* Owner: a View. Its resource reader reads the files its pages name (url() images) from the file system, by the paths
 * weft resolves against the page's base (an absolute path, as the C# side gives it). */
weft_result weft_unity_view_create(const weft_view_desc* desc, weft_view_handle* out_view);
weft_result weft_unity_view_destroy(weft_view_handle view);
weft_result weft_unity_view_load(weft_view_handle view, const weft_page_desc* page);
weft_result weft_unity_view_resize(weft_view_handle view, float logical_width, float logical_height, float device_scale);

/* Owner: runs the View's page up to time_seconds (weft_view_advance), then keeps the page's messages and diagnostics
 * since its last advance until its next one; their counts are written out. */
weft_result weft_unity_view_advance(weft_view_handle view, double time_seconds, uint32_t* out_messages, uint32_t* out_diagnostics);

/* A message of the page (weft.engine.send, or weft.engine.call with its call id), its value as JSON text. */
typedef struct weft_unity_message {
    uint32_t kind; /* WEFT_MESSAGE_SEND or WEFT_MESSAGE_CALL */
    uint32_t reserved;
    uint64_t call_id;
    const char* name;
    size_t name_size;
    const char* json; /* "null" for no value */
    size_t json_size;
} weft_unity_message;

/* A message or a diagnostic the View's last advance kept (index below its count); the text is the plugin's until the
 * View's next advance. */
weft_result weft_unity_view_message(weft_view_handle view, uint32_t index, weft_unity_message* out_message);
weft_result weft_unity_view_diagnostic(weft_view_handle view, uint32_t index, uint32_t* out_kind, const char** out_text,
                                       size_t* out_size);
/* Diagnostics dropped past the View's bound during its last advance. */
uint32_t weft_unity_view_dropped_diagnostics(weft_view_handle view);

/* Owner: the View's localStorage (WEFT_PAGE_WEB_PLATFORM). */
weft_result weft_unity_view_set_storage_item(weft_view_handle view, const char* key, size_t key_size, const char* value,
                                             size_t value_size);
weft_result weft_unity_view_clear_storage(weft_view_handle view);
weft_result weft_unity_view_get_storage(weft_view_handle view, uint64_t revision, weft_storage_item_fn fn, void* user,
                                        uint64_t* out_revision);

/* Owner: evaluates source in the page (weft_view_evaluate) for debug tools. */
weft_result weft_unity_view_evaluate(weft_view_handle view, const char* source, size_t source_size, weft_text_fn fn,
                                     void* user);

/* Any thread: metadata of the View's latest published frame (weft_view_frame_get_info). */
weft_result weft_unity_view_frame_info(weft_view_handle view, weft_view_frame_info* out_info);

/* Owner: developer tools (weft_devtools_start, weft_devtools_stop, weft_devtools_get_info). In the release build these
 * return WEFT_UNAVAILABLE. */
weft_result weft_unity_devtools_start(const weft_devtools_desc* desc);
weft_result weft_unity_devtools_stop(void);
weft_result weft_unity_devtools_get_info(weft_devtools_info* out_info);

/* Owner: pointer input and hit tests (weft_view_pointer, weft_view_hit_test). */
weft_result weft_unity_view_pointer(weft_view_handle view, const weft_pointer_event* event, uint32_t* out_prevented);
weft_result weft_unity_view_hit_test(weft_view_handle view, float x, float y, weft_hit* out_hit);

/* Owner: keyboard input (weft_view_key, weft_view_text) and the keyboard focus of the game's window coming to the View
 * and leaving it (weft_view_set_focus). weft_keyboard_key, the key value of a physical key in the system's layout, is
 * weft's own and any thread's. */
weft_result weft_unity_view_key(weft_view_handle view, const weft_key_event* event, uint32_t* out_handled);
weft_result weft_unity_view_text(weft_view_handle view, const char* text, size_t text_size, uint32_t* out_handled);
weft_result weft_unity_view_set_focus(weft_view_handle view, uint32_t focused);

/* Owner: where text input goes and an input method's composition and commit (weft_view_text_input,
 * weft_view_composition, weft_view_commit_text). */
weft_result weft_unity_view_text_input(weft_view_handle view, weft_text_input* out_input);
weft_result weft_unity_view_composition(weft_view_handle view, const char* text, size_t text_size,
                                        uint32_t selection_start, uint32_t selection_end, uint32_t* out_taken);
weft_result weft_unity_view_commit_text(weft_view_handle view, const char* text, size_t text_size,
                                        uint32_t* out_taken);

/* Owner: gamepads' states, in order (weft_view_gamepad for each); out_handled, when given, has count entries, each
 * state's WEFT_INPUT_* bits. The first state weft refuses ends the call with its result. */
weft_result weft_unity_view_gamepads(weft_view_handle view, const weft_gamepad_state* states, uint32_t count,
                                     uint32_t* out_handled);

/* ---- The engine's textures: a page's background-image: -weft-texture(name). ---- */

/* Binds a name the pages show by -weft-texture(name) to a texture of Unity's (Texture.GetNativeTexturePtr: an
 * ID3D11Texture2D, an ID3D12Resource or a VkImage*), width x height its size, or unbinds it (texture NULL). The texture
 * holds linear premultiplied RGBA, its bottom row first as Unity's render textures do: RGBA16F (ARGBHalf) on every API,
 * linear RGBA8 (not sRGB) and RGBA32F where weft's backend samples them, and sRGB RGBA8 or BGRA8 on Direct3D 12. It is
 * live: Unity may draw into it again before each draw that shows it (a camera rendered before the View's draw). Names
 * are letters, digits, '-' and '_', at most 64 bytes; 32 at once (WEFT_CAPACITY_EXCEEDED). Any thread; stopping the
 * runtime unbinds them all. A texture weft's backend cannot sample is logged once and draws nothing. */
weft_result weft_unity_set_texture(const char* name, size_t name_size, void* texture, uint32_t width, uint32_t height);

/* ---- Drawing, on Unity's render thread. ---- */

/* Queues a View's latest frame to draw into a texture: target is the texture's native pointer
 * (Texture.GetNativeTexturePtr: an ID3D11Texture2D, an ID3D12Resource or a VkImage*), of an 8-bit RGBA or BGRA UNORM
 * format (or its typeless one), width x height its size; scale is device px per CSS px (0: the View's); clear 1 clears
 * the texture to transparent first, 0 draws over what it holds. The frame is drawn as weft draws it: sRGB-encoded,
 * premultiplied. Returns the draw's id, the data of the render event that draws it (0: the View has no frame yet, or
 * too many draws wait). A draw no render event takes is dropped after 64 more are queued. */
uint32_t weft_unity_queue_draw(weft_view_handle view, void* target, uint32_t width, uint32_t height, float scale, uint32_t clear);

/* Unity's rendering event (a UnityRenderingEventAndData) and its event id, for
 * CommandBuffer.IssuePluginEventAndData(weft_unity_render_event(), weft_unity_render_event_id(), draw id). */
void* weft_unity_render_event(void);
int weft_unity_render_event_id(void);

/* What the render thread has done: the graphics API it draws with (UnityGfxRenderer: 2 D3D11, 18 D3D12, 21 Vulkan;
 * 0 before Unity's device starts), the draws done and failed, and the last failure (NUL-terminated, cut to fit). */
typedef struct weft_unity_render_stats {
    uint32_t renderer;
    uint32_t reserved;
    uint64_t draws;
    uint64_t failures;
    char last_failure[256];
} weft_unity_render_stats;

void weft_unity_render_get_stats(weft_unity_render_stats* out_stats);

/* Render-thread draw recording time accumulated for a View while measuring is on (0 cost when off). */
typedef struct weft_unity_draw_time {
    uint32_t slot;
    uint32_t generation;
    double seconds;
} weft_unity_draw_time;

void weft_unity_render_set_measuring(uint32_t enabled);
uint32_t weft_unity_render_take_draw_times(weft_unity_draw_time* out_times, uint32_t capacity);

#ifdef __cplusplus
}
#endif

#endif
