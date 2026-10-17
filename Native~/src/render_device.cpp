#include "render_device.hpp"

#include <algorithm>
#include <iterator>

namespace weft_unity {
    std::string describe(weft_render_result result) {
        static constexpr const char* names[]{"OK", "INVALID_ARGUMENT", "UNSUPPORTED_ABI", "UNSUPPORTED_FEATURE", "WRONG_THREAD",
                                             "STALE_HANDLE", "WRONG_BACKEND", "WRONG_EPOCH", "BUSY", "INVALID_STATE", "INVALID_SERIAL",
                                             "BACKEND_ERROR", "OUT_OF_MEMORY", "SHUTTING_DOWN", "DEVICE_UNAVAILABLE",
                                             "HOST_CONFIRMATION_REQUIRED"};
        const auto index = std::size_t(result);
        return std::string(index < std::size(names) ? names[index] : "UNKNOWN") + " (" + std::to_string(int(result)) + ")";
    }

    RenderDevice::~RenderDevice() = default;

    bool RenderDevice::draw(const Draw& draw, std::string& why) {
        // Renderers of earlier runtimes go once the GPU is past their recordings.
        std::erase_if(retiring_, [this](std::unique_ptr<Slot>& slot) {
            retire(*slot);
            return slot->pending.empty() && destroy(*slot);
        });
        if (!current_ || current_->epoch != draw.epoch) {
            if (current_) {
                retiring_.push_back(std::move(current_));
            }
            if (!start(draw.epoch, why)) {
                return false;
            }
        }
        Slot& slot = *current_;
        retire(slot);
        const weft_render_scene_frame scene{sizeof(scene), WEFT_RENDER_ABI_VERSION, draw.frame, draw.scale, 0, nullptr};
        weft_render_handle prepared{};
        if (const auto result = weft_renderer_prepare_scene(slot.renderer, &scene, &prepared); result != WEFT_RENDER_OK) {
            why = "prepare_scene failed: " + describe(result);
            return false;
        }
        const bool encoded = encode(slot, prepared, draw, why);
        weft_renderer_release_prepared(slot.renderer, prepared);
        return encoded;
    }

    void RenderDevice::shutdown() {
        // Unity's device goes: it has finished all of its work.
        if (current_) {
            retiring_.push_back(std::move(current_));
        }
        for (auto& slot : retiring_) {
            idle(*slot);
            slot->pending.clear();
            if (slot->renderer && slot->serial > slot->completed) {
                weft_renderer_notify_completed(slot->renderer, slot->identity.device_epoch, queue, slot->serial);
            }
            destroy(*slot);
        }
        retiring_.clear();
    }

    void RenderDevice::idle(const Slot&) {
    }

    void RenderDevice::submit(Slot& slot, weft_render_handle use) {
        ++slot.serial;
        weft_renderer_notify_submitted(slot.renderer, use, queue, slot.serial);
        slot.pending.emplace_back(slot.serial, mark());
    }

    bool RenderDevice::start(std::uint64_t epoch, std::string& why) {
        auto slot = std::make_unique<Slot>();
        slot->epoch = epoch;
        weft_render_backend table{};
        if (!open(*slot, table, why)) {
            return false;
        }
        if (const auto result = weft_renderer_create(&table, nullptr, &slot->renderer); result != WEFT_RENDER_OK) {
            why = "renderer_create failed: " + describe(result);
            slot->renderer = nullptr;
            close(*slot);
            return false;
        }
        weft_render_info info{sizeof(info)};
        weft_renderer_get_info(slot->renderer, &info);
        slot->identity = info.identity;
        current_ = std::move(slot);
        return true;
    }

    void RenderDevice::retire(Slot& slot) {
        if (slot.pending.empty()) {
            return;
        }
        const std::uint64_t done = finished();
        std::uint64_t completed = slot.completed;
        while (!slot.pending.empty() && slot.pending.front().second <= done) {
            completed = slot.pending.front().first;
            slot.pending.pop_front();
        }
        if (completed > slot.completed) {
            slot.completed = completed;
            weft_renderer_notify_completed(slot.renderer, slot.identity.device_epoch, queue, completed);
        }
    }

    bool RenderDevice::destroy(Slot& slot) {
        if (slot.renderer) {
            if (weft_renderer_destroy(slot.renderer) != WEFT_RENDER_OK) {
                return false;
            }
            slot.renderer = nullptr;
        }
        close(slot);
        return true;
    }
} // namespace weft_unity
