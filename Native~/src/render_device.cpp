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

    std::string describe(weft_render_result result, weft_renderer* renderer) {
        std::string text = describe(result);
        std::string reason;
        if (renderer) {
            weft_renderer_last_failure(
                renderer,
                [](void* user, const char* chars, std::size_t size) { static_cast<std::string*>(user)->assign(chars, size); },
                &reason);
        }
        return reason.empty() ? text : text + ": " + reason;
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
        const std::vector<weft_render_scene_texture> textures = bind(slot);
        const weft_render_scene_frame scene{sizeof(scene), WEFT_RENDER_ABI_VERSION, draw.frame, draw.scale,
                                            std::uint32_t(textures.size()), textures.data()};
        weft_render_handle prepared{};
        if (const auto result = weft_renderer_prepare_scene(slot.renderer, &scene, &prepared); result != WEFT_RENDER_OK) {
            why = "prepare_scene failed: " + describe(result, slot.renderer);
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

    bool RenderDevice::sample_texture(const TextureBinding&, std::string&) {
        return true;
    }

    void RenderDevice::forget_texture(Import&) {
    }

    void RenderDevice::idle(const Slot&) {
    }

    void RenderDevice::submit(Slot& slot, weft_render_handle use) {
        ++slot.serial;
        weft_renderer_notify_submitted(slot.renderer, use, queue, slot.serial);
        slot.pending.emplace_back(slot.serial, mark());
    }

    void RenderDevice::say(const std::string& text) {
        if (warn) {
            warn(text);
        }
    }

    weft_render_external_texture RenderDevice::live_texture(const Slot& slot, const TextureBinding& binding, std::uint32_t format,
                                                            const void* native, std::uint64_t native_size) {
        // Unity's render textures hold their bottom row first (it draws into them upside down on these APIs), and a
        // camera draws into one again between the frames that show it.
        return {sizeof(weft_render_external_texture),
                WEFT_RENDER_EXTERNAL_ABI_VERSION,
                slot.identity,
                binding.serial,
                1,
                binding.width,
                binding.height,
                format,
                WEFT_RENDER_EXTERNAL_LINEAR_PREMULTIPLIED,
                WEFT_RENDER_EXTERNAL_BOTTOM_LEFT,
                WEFT_RENDER_EXTERNAL_SAME_QUEUE_LIVE,
                1,
                0,
                native,
                native_size,
                nullptr,
                nullptr};
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
        // The engine's textures, before the renderer's first frame; without them pages draw no -weft-texture().
        weft_render_external_backend external_table{};
        std::string no_textures;
        if (!external(*slot, external_table, no_textures)) {
            say("no engine textures: " + no_textures);
        } else if (const auto result = weft_renderer_install_external_backend(slot->renderer, &external_table);
                   result != WEFT_RENDER_OK) {
            say("no engine textures: install_external_backend failed: " + describe(result));
        } else {
            slot->textures = true;
        }
        current_ = std::move(slot);
        return true;
    }

    std::vector<weft_render_scene_texture> RenderDevice::bind(Slot& slot) {
        std::vector<weft_render_scene_texture> textures;
        if (!slot.textures) {
            return textures;
        }
        const std::vector<TextureBinding> bound = TextureTable::get().snapshot();
        // Imports of names unbound, or bound to another texture since, are released.
        std::erase_if(slot.imports, [&](Import& held) {
            const bool kept = std::any_of(bound.begin(), bound.end(), [&](const TextureBinding& binding) {
                return binding.name == held.name && binding.serial == held.serial;
            });
            if (!kept) {
                release(slot, held);
            }
            return !kept;
        });
        for (const TextureBinding& binding : bound) {
            if (std::find(refused_.begin(), refused_.end(), binding.serial) != refused_.end()) {
                continue;
            }
            auto it = std::find_if(slot.imports.begin(), slot.imports.end(), [&](const Import& i) { return i.serial == binding.serial; });
            std::string why;
            if (it == slot.imports.end()) {
                Import added{binding.name, binding.serial, {}, nullptr};
                if (!import_texture(slot, binding, added, why)) {
                    refused_.push_back(binding.serial);
                    say("the texture " + binding.name + " is not shown: " + why);
                    continue;
                }
                slot.imports.push_back(added);
                it = std::prev(slot.imports.end());
            }
            if (!sample_texture(binding, why)) {
                refused_.push_back(binding.serial);
                say("the texture " + binding.name + " is not shown: " + why);
                release(slot, *it);
                slot.imports.erase(it);
            }
        }
        // The imports are settled: their names stay put while the frame is prepared.
        textures.reserve(slot.imports.size());
        for (const Import& held : slot.imports) {
            textures.push_back({held.name.data(), std::uint32_t(held.name.size()), 0, held.lease});
        }
        return textures;
    }

    void RenderDevice::release(Slot& slot, Import& released) {
        // Frames prepared, encoded or submitted with it keep using it until they complete.
        weft_renderer_release_texture(slot.renderer, released.lease);
        forget_texture(released);
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
            for (Import& held : slot.imports) {
                release(slot, held);
            }
            slot.imports.clear();
            if (weft_renderer_destroy(slot.renderer) != WEFT_RENDER_OK) {
                return false;
            }
            slot.renderer = nullptr;
        }
        close(slot);
        return true;
    }
} // namespace weft_unity
