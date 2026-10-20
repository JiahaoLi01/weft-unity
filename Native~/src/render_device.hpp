#pragma once

#include "draw_queue.hpp"
#include "textures.hpp"
#include "weft/render_backend.h"
#include "weft/render_external_texture.h"

#include <cstdint>
#include <deque>
#include <memory>
#include <string>
#include <vector>

struct IUnityInterfaces;

namespace weft_unity {
    // Draws View frames on Unity's render thread with one graphics API: a weft renderer over the API's built-in weft
    // backend, made on the render thread (its owner thread) for a runtime and made again for the next one (Unity's
    // play sessions each start a runtime). A renderer's recordings retire when the GPU is past them, by marks the API
    // gives (a frame fence's value, a frame number); a renderer of an earlier runtime is destroyed once all of its
    // recordings have. The engine's textures (TextureTable) are imported into a renderer the first time it draws with
    // them bound, as live textures, and released when their name is unbound or bound to another texture.
    class RenderDevice {
    public:
        virtual ~RenderDevice();

        // On the render thread: draws a frame (the draw's frame reference stays the caller's); false and why.
        bool draw(const Draw& draw, std::string& why);
        // On the render thread, when Unity's device goes: every renderer destroyed.
        void shutdown();

        // Where the device says what it could not do that does not fail a draw (a texture it cannot sample), once each.
        void (*warn)(const std::string& text) = nullptr;

    protected:
        // The queue name the renderers report submissions on.
        static constexpr std::uint64_t queue = 1;

        // A texture of the table imported into a renderer: its caller lease, and what the API keeps for it.
        struct Import {
            std::string name;
            std::uint64_t serial = 0; // the binding's
            weft_render_handle lease{};
            void* native = nullptr; // the API's
        };

        struct Slot {
            weft_renderer* renderer = nullptr;
            void* backend = nullptr; // the API's weft backend
            weft_render_identity identity{};
            std::uint64_t epoch = 0;
            std::uint64_t serial = 0;    // the last submitted
            std::uint64_t completed = 0; // the last reported complete
            std::deque<std::pair<std::uint64_t, std::uint64_t>> pending; // serial, the API's mark of it
            bool textures = false; // the renderer has the external texture extension
            std::vector<Import> imports;
        };

        // The API's weft backend for a new renderer, and its table; false and why.
        virtual bool open(Slot& slot, weft_render_backend& table, std::string& why) = 0;
        // After its renderer is destroyed.
        virtual void close(Slot& slot) = 0;
        // The backend's external texture extension, for the slot's new renderer; false and why.
        virtual bool external(Slot& slot, weft_render_external_backend& table, std::string& why) = 0;
        // Imports a bound texture into the slot's renderer as a live texture: the import's lease (and native); false
        // and why.
        virtual bool import_texture(Slot& slot, const TextureBinding& binding, Import& added, std::string& why) = 0;
        // Before a recording samples an imported texture: its state or layout for shader reads; false and why.
        virtual bool sample_texture(const TextureBinding& binding, std::string& why);
        // After an import's lease is released: what the API keeps for it goes once weft retires the texture.
        virtual void forget_texture(Import& released);
        // Records a prepared frame into the draw's target: the pass, then the submission (submit(slot, use) once the
        // recording is the API's); false and why.
        virtual bool encode(Slot& slot, weft_render_handle prepared, const Draw& draw, std::string& why) = 0;
        // The API's mark of what is recorded now, and of what the GPU has finished.
        virtual std::uint64_t mark() = 0;
        virtual std::uint64_t finished() = 0;
        // When Unity's device goes: waits (a while at most) until the GPU is past the slot's recordings.
        virtual void idle(const Slot& slot);

        // Reports a recording submitted at the API's current mark.
        void submit(Slot& slot, weft_render_handle use);
        void say(const std::string& text);

        // The external texture description of a live texture of Unity's (linear premultiplied, bottom row first).
        static weft_render_external_texture live_texture(const Slot& slot, const TextureBinding& binding, std::uint32_t format,
                                                         const void* native, std::uint64_t native_size);

    private:
        bool start(std::uint64_t epoch, std::string& why);
        void retire(Slot& slot);
        // The scene textures of this draw: the table's bindings, imported and ready to sample.
        std::vector<weft_render_scene_texture> bind(Slot& slot);
        void release(Slot& slot, Import& released);
        // false keeps the slot: its renderer still drains.
        bool destroy(Slot& slot);

        std::unique_ptr<Slot> current_;
        std::vector<std::unique_ptr<Slot>> retiring_;
        std::vector<std::uint64_t> refused_; // bindings the API could not import or sample, said once each
    };

    // A weft renderer result as text: its name and number ("WRONG_THREAD (4)").
    std::string describe(weft_render_result result);

    // A failed prepare's or encode's result as text, with the backend's reason when it gives one ("BACKEND_ERROR (11):
    // the native pass failed: ...").
    std::string describe(weft_render_result result, weft_renderer* renderer);

    std::unique_ptr<RenderDevice> make_d3d11_device(IUnityInterfaces* unity);
    std::unique_ptr<RenderDevice> make_d3d12_device(IUnityInterfaces* unity, int event_id);
#if WEFT_UNITY_VULKAN
    std::unique_ptr<RenderDevice> make_vulkan_device(IUnityInterfaces* unity, int event_id);
#endif
} // namespace weft_unity
