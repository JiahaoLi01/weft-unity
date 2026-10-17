#pragma once

#include "draw_queue.hpp"
#include "weft/render_backend.h"

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
    // recordings have.
    class RenderDevice {
    public:
        virtual ~RenderDevice();

        // On the render thread: draws a frame (the draw's frame reference stays the caller's); false and why.
        bool draw(const Draw& draw, std::string& why);
        // On the render thread, when Unity's device goes: every renderer destroyed.
        void shutdown();

    protected:
        // The queue name the renderers report submissions on.
        static constexpr std::uint64_t queue = 1;

        struct Slot {
            weft_renderer* renderer = nullptr;
            void* backend = nullptr; // the API's weft backend
            weft_render_identity identity{};
            std::uint64_t epoch = 0;
            std::uint64_t serial = 0;    // the last submitted
            std::uint64_t completed = 0; // the last reported complete
            std::deque<std::pair<std::uint64_t, std::uint64_t>> pending; // serial, the API's mark of it
        };

        // The API's weft backend for a new renderer, and its table; false and why.
        virtual bool open(Slot& slot, weft_render_backend& table, std::string& why) = 0;
        // After its renderer is destroyed.
        virtual void close(Slot& slot) = 0;
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

    private:
        bool start(std::uint64_t epoch, std::string& why);
        void retire(Slot& slot);
        // false keeps the slot: its renderer still drains.
        bool destroy(Slot& slot);

        std::unique_ptr<Slot> current_;
        std::vector<std::unique_ptr<Slot>> retiring_;
    };

    // A weft renderer result as text: its name and number ("WRONG_THREAD (4)").
    std::string describe(weft_render_result result);

    std::unique_ptr<RenderDevice> make_d3d11_device(IUnityInterfaces* unity);
    std::unique_ptr<RenderDevice> make_d3d12_device(IUnityInterfaces* unity, int event_id);
#if WEFT_UNITY_VULKAN
    std::unique_ptr<RenderDevice> make_vulkan_device(IUnityInterfaces* unity, int event_id);
#endif
} // namespace weft_unity
