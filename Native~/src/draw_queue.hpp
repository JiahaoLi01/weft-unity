#pragma once

#include "weft/weft.h"

#include <cstdint>
#include <deque>
#include <mutex>
#include <optional>

namespace weft_unity {
    // A View's frame to draw into a texture on Unity's render thread, and the runtime it is of.
    struct Draw {
        weft_view_frame* frame = nullptr; // a reference of the draw's, released when it is drawn or dropped
        void* target = nullptr;           // the texture's native pointer
        std::uint32_t width = 0;
        std::uint32_t height = 0;
        float scale = 0;
        bool clear = false;
        std::uint64_t epoch = 0;
    };

    // The draws Unity's main thread queues (weft_unity_queue_draw) for its render thread's events to take by id. A
    // draw no event takes is dropped (its frame released) once 64 newer ones wait; stopping the runtime drops them all.
    class DrawQueue {
    public:
        static constexpr std::size_t capacity = 64;

        static DrawQueue& get();

        std::uint32_t push(const Draw& draw);
        std::optional<Draw> take(std::uint32_t id);
        void drop_all();

    private:
        std::mutex lock_;
        std::deque<std::pair<std::uint32_t, Draw>> waiting_;
        std::uint32_t next_ = 1;
    };
} // namespace weft_unity
