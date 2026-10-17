#include "draw_queue.hpp"

#include "page_host.hpp"
#include "weft_unity.h"

#include <vector>

namespace weft_unity {
    DrawQueue& DrawQueue::get() {
        static DrawQueue* queue = new DrawQueue();
        return *queue;
    }

    std::uint32_t DrawQueue::push(const Draw& draw) {
        std::vector<weft_view_frame*> dropped;
        std::uint32_t id = 0;
        {
            std::lock_guard lock(lock_);
            while (waiting_.size() >= capacity) {
                dropped.push_back(waiting_.front().second.frame);
                waiting_.pop_front();
            }
            id = next_++;
            if (next_ == 0) {
                next_ = 1;
            }
            waiting_.emplace_back(id, draw);
        }
        for (auto* frame : dropped) {
            weft_view_frame_release(frame);
        }
        return id;
    }

    std::optional<Draw> DrawQueue::take(std::uint32_t id) {
        std::lock_guard lock(lock_);
        for (auto it = waiting_.begin(); it != waiting_.end(); ++it) {
            if (it->first == id) {
                const Draw draw = it->second;
                waiting_.erase(it);
                return draw;
            }
        }
        return std::nullopt;
    }

    void DrawQueue::drop_all() {
        std::deque<std::pair<std::uint32_t, Draw>> dropped;
        {
            std::lock_guard lock(lock_);
            dropped.swap(waiting_);
        }
        for (auto& [id, draw] : dropped) {
            weft_view_frame_release(draw.frame);
        }
    }
} // namespace weft_unity

extern "C" uint32_t weft_unity_queue_draw(weft_view_handle view, void* target, uint32_t width, uint32_t height, float scale, uint32_t clear) {
    auto& host = weft_unity::PageHost::get();
    weft_runtime* runtime = host.runtime();
    if (!runtime || !target || !width || !height) {
        return 0;
    }
    weft_view_frame* frame = nullptr;
    if (weft_view_acquire_frame(runtime, view, &frame) != WEFT_OK || !frame) {
        return 0;
    }
    return weft_unity::DrawQueue::get().push({frame, target, width, height, scale, clear != 0, host.epoch()});
}
