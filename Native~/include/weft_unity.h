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

/* Owner: pointer input and hit tests (weft_view_pointer, weft_view_hit_test). */
weft_result weft_unity_view_pointer(weft_view_handle view, const weft_pointer_event* event, uint32_t* out_prevented);
weft_result weft_unity_view_hit_test(weft_view_handle view, float x, float y, weft_hit* out_hit);

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

#ifdef __cplusplus
}
#endif

#endif
