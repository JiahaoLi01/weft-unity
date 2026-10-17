#include "page_host.hpp"

#include "draw_queue.hpp"
#include "message_pack.hpp"
#include "weft_unity.h"

#include <filesystem>
#include <fstream>
#include <iterator>

namespace weft_unity {
    namespace {
        bool same(weft_view_handle a, weft_view_handle b) {
            return a.runtime_id == b.runtime_id && a.slot == b.slot && a.generation == b.generation;
        }

        // A file a page names (a url() image), by the path weft resolved against the page's base: read from the file
        // system. Left unwritten when there is none (weft diagnoses it).
        weft_result read_resource(void*, const char* path, size_t path_size, weft_resource_writer* file) {
            const std::u8string utf8(reinterpret_cast<const char8_t*>(path), path_size);
            std::ifstream in(std::filesystem::path(utf8), std::ios::binary);
            if (!in) {
                return WEFT_OK;
            }
            const std::string bytes{std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>()};
            return weft_resource_writer_set(file, bytes.data(), bytes.size());
        }
    } // namespace

    PageHost& PageHost::get() {
        // Made once and kept for the process's life, with its owner thread.
        static PageHost* host = new PageHost();
        return *host;
    }

    PageHost::PageHost() : owner_(new OwnerThread()) {
    }

    weft_result PageHost::start(std::uint32_t flags, std::uint32_t max_views) {
        if (runtime_.load()) {
            return WEFT_BUSY;
        }
        // 0 is weft's default (the count a runtime made without a description has).
        constexpr std::uint32_t default_max_views = 64;
        const weft_runtime_desc desc{sizeof(desc), WEFT_ABI_VERSION, max_views ? max_views : default_max_views, flags};
        weft_runtime* runtime = nullptr;
        const auto result = weft_runtime_create(&desc, &runtime);
        if (result == WEFT_OK) {
            ++epoch_;
            runtime_.store(runtime);
        }
        return result;
    }

    void PageHost::stop() {
        weft_runtime* runtime = runtime_.exchange(nullptr);
        if (!runtime) {
            return;
        }
        DrawQueue::get().drop_all();
        std::vector<Kept> views;
        {
            std::lock_guard lock(lock_);
            views.swap(views_);
        }
        for (const auto& kept : views) {
            weft_view_destroy(runtime, kept.view);
        }
        weft_runtime_destroy(runtime);
    }

    weft_result PageHost::create_view(const weft_view_desc& desc, weft_view_handle& out) {
        weft_runtime* runtime = runtime_.load();
        if (!runtime) {
            return WEFT_STALE_HANDLE;
        }
        const auto result = weft_view_create(runtime, &desc, &out);
        if (result != WEFT_OK) {
            return result;
        }
        weft_view_set_resource_reader(runtime, out, &read_resource, nullptr);
        std::lock_guard lock(lock_);
        views_.push_back({out, {}, {}});
        return WEFT_OK;
    }

    weft_result PageHost::destroy_view(weft_view_handle view) {
        weft_runtime* runtime = runtime_.load();
        if (!runtime) {
            return WEFT_STALE_HANDLE;
        }
        {
            std::lock_guard lock(lock_);
            std::erase_if(views_, [&](const Kept& kept) { return same(kept.view, view); });
        }
        return weft_view_destroy(runtime, view);
    }

    weft_result PageHost::advance(weft_view_handle view, double time_seconds, std::uint32_t& messages, std::uint32_t& diagnostics) {
        messages = diagnostics = 0;
        weft_runtime* runtime = runtime_.load();
        if (!runtime) {
            return WEFT_STALE_HANDLE;
        }
        const auto result = weft_view_advance(runtime, view, time_seconds);
        std::vector<Message> sent;
        std::vector<Diagnostic> said;
        weft_view_drain_messages(
            runtime, view,
            [](void* user, weft_view_handle, const weft_message* m) {
                static_cast<std::vector<Message>*>(user)->push_back(
                    {m->kind, m->call_id, std::string(m->name, m->name_size), message_pack_to_json(m->payload, m->payload_size)});
            },
            &sent);
        weft_view_drain_diagnostics(
            runtime, view,
            [](void* user, weft_view_handle, const weft_diagnostic* d) {
                static_cast<std::vector<Diagnostic>*>(user)->push_back({d->kind, std::string(d->message, d->message_size)});
            },
            &said, nullptr);
        std::lock_guard lock(lock_);
        if (Kept* kept = find(view)) {
            kept->messages = std::move(sent);
            kept->diagnostics = std::move(said);
            messages = std::uint32_t(kept->messages.size());
            diagnostics = std::uint32_t(kept->diagnostics.size());
        }
        return result;
    }

    const PageHost::Message* PageHost::message(weft_view_handle view, std::uint32_t index) const {
        std::lock_guard lock(lock_);
        const Kept* kept = find(view);
        return kept && index < kept->messages.size() ? &kept->messages[index] : nullptr;
    }

    const PageHost::Diagnostic* PageHost::diagnostic(weft_view_handle view, std::uint32_t index) const {
        std::lock_guard lock(lock_);
        const Kept* kept = find(view);
        return kept && index < kept->diagnostics.size() ? &kept->diagnostics[index] : nullptr;
    }

    PageHost::Kept* PageHost::find(weft_view_handle view) {
        for (auto& kept : views_) {
            if (same(kept.view, view)) {
                return &kept;
            }
        }
        return nullptr;
    }

    const PageHost::Kept* PageHost::find(weft_view_handle view) const {
        for (const auto& kept : views_) {
            if (same(kept.view, view)) {
                return &kept;
            }
        }
        return nullptr;
    }
} // namespace weft_unity

using weft_unity::PageHost;

namespace {
    // Runs work on the owner thread and returns its result.
    template <typename Work>
    weft_result on_owner(Work&& work) {
        weft_result result = WEFT_INTERNAL_ERROR;
        PageHost::get().owner().call([&] { result = work(); });
        return result;
    }

    weft_runtime* running() {
        return PageHost::get().runtime();
    }
} // namespace

extern "C" {
const char* weft_unity_build(void) {
    return WEFT_UNITY_COMMIT;
}

weft_result weft_unity_start(uint32_t flags, uint32_t max_views) {
    return on_owner([&] { return PageHost::get().start(flags, max_views); });
}

void weft_unity_stop(void) {
    on_owner([] {
        PageHost::get().stop();
        return WEFT_OK;
    });
}

weft_runtime* weft_unity_runtime(void) {
    return running();
}

weft_result weft_unity_add_font(const weft_font_face* face) {
    return on_owner([&] { return running() ? weft_runtime_add_font(running(), face) : WEFT_STALE_HANDLE; });
}

weft_result weft_unity_set_generic_family(const char* generic, size_t generic_size, const char* family, size_t family_size) {
    return on_owner([&] {
        return running() ? weft_runtime_set_generic_family(running(), generic, generic_size, family, family_size) : WEFT_STALE_HANDLE;
    });
}

weft_result weft_unity_add_fallback_family(const char* family, size_t family_size) {
    return on_owner([&] { return running() ? weft_runtime_add_fallback_family(running(), family, family_size) : WEFT_STALE_HANDLE; });
}

weft_result weft_unity_view_create(const weft_view_desc* desc, weft_view_handle* out_view) {
    if (!desc || !out_view) {
        return WEFT_INVALID_ARGUMENT;
    }
    *out_view = {};
    return on_owner([&] { return PageHost::get().create_view(*desc, *out_view); });
}

weft_result weft_unity_view_destroy(weft_view_handle view) {
    return on_owner([&] { return PageHost::get().destroy_view(view); });
}

weft_result weft_unity_view_load(weft_view_handle view, const weft_page_desc* page) {
    return on_owner([&] { return running() ? weft_view_load(running(), view, page) : WEFT_STALE_HANDLE; });
}

weft_result weft_unity_view_resize(weft_view_handle view, float logical_width, float logical_height, float device_scale) {
    return on_owner([&] {
        return running() ? weft_view_resize(running(), view, logical_width, logical_height, device_scale) : WEFT_STALE_HANDLE;
    });
}

weft_result weft_unity_view_advance(weft_view_handle view, double time_seconds, uint32_t* out_messages, uint32_t* out_diagnostics) {
    std::uint32_t messages = 0, diagnostics = 0;
    const auto result = on_owner([&] { return PageHost::get().advance(view, time_seconds, messages, diagnostics); });
    if (out_messages) {
        *out_messages = messages;
    }
    if (out_diagnostics) {
        *out_diagnostics = diagnostics;
    }
    return result;
}

weft_result weft_unity_view_message(weft_view_handle view, uint32_t index, weft_unity_message* out_message) {
    const PageHost::Message* kept = PageHost::get().message(view, index);
    if (!kept || !out_message) {
        return WEFT_INVALID_ARGUMENT;
    }
    *out_message = {kept->kind, 0, kept->call_id, kept->name.data(), kept->name.size(), kept->json.data(), kept->json.size()};
    return WEFT_OK;
}

weft_result weft_unity_view_diagnostic(weft_view_handle view, uint32_t index, uint32_t* out_kind, const char** out_text, size_t* out_size) {
    const PageHost::Diagnostic* kept = PageHost::get().diagnostic(view, index);
    if (!kept || !out_kind || !out_text || !out_size) {
        return WEFT_INVALID_ARGUMENT;
    }
    *out_kind = kept->kind;
    *out_text = kept->text.data();
    *out_size = kept->text.size();
    return WEFT_OK;
}

weft_result weft_unity_view_pointer(weft_view_handle view, const weft_pointer_event* event, uint32_t* out_prevented) {
    return on_owner([&] { return running() ? weft_view_pointer(running(), view, event, out_prevented) : WEFT_STALE_HANDLE; });
}

weft_result weft_unity_view_hit_test(weft_view_handle view, float x, float y, weft_hit* out_hit) {
    return on_owner([&] { return running() ? weft_view_hit_test(running(), view, x, y, out_hit) : WEFT_STALE_HANDLE; });
}
}
