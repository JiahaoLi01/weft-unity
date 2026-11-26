#pragma once

#include "owner_thread.hpp"
#include "weft/weft.h"

#include <atomic>
#include <cstdint>
#include <mutex>
#include <string>
#include <vector>

namespace weft_unity {
    // The runtime and the Views the plugin made, on the owner thread: weft_unity_start makes a runtime, and every View
    // keeps the messages (their values as JSON) and diagnostics of its last advance for the C# side to read. Each
    // runtime has an epoch, so that the render thread knows to make its renderer again for a new one.
    class PageHost {
    public:
        static PageHost& get();

        OwnerThread& owner() {
            return *owner_;
        }
        weft_runtime* runtime() const {
            return runtime_.load();
        }
        std::uint64_t epoch() const {
            return epoch_.load();
        }

        // On the owner thread.
        weft_result start(std::uint32_t flags, std::uint32_t max_views);
        void stop();
        weft_result create_view(const weft_view_desc& desc, weft_view_handle& out);
        weft_result destroy_view(weft_view_handle view);
        weft_result advance(weft_view_handle view, double time_seconds, std::uint32_t& messages, std::uint32_t& diagnostics);

        struct Message {
            std::uint32_t kind = 0;
            std::uint64_t call_id = 0;
            std::string name;
            std::string json;
        };
        struct Diagnostic {
            std::uint32_t kind = 0;
            std::string text;
        };
        // What the View's last advance kept, valid until its next advance (any thread).
        const Message* message(weft_view_handle view, std::uint32_t index) const;
        const Diagnostic* diagnostic(weft_view_handle view, std::uint32_t index) const;
        std::uint32_t dropped_diagnostics(weft_view_handle view) const;

    private:
        PageHost();

        struct Kept {
            weft_view_handle view{};
            std::vector<Message> messages;
            std::vector<Diagnostic> diagnostics;
            std::uint32_t dropped_diagnostics = 0;
        };
        Kept* find(weft_view_handle view);
        const Kept* find(weft_view_handle view) const;

        // Lives as long as the process: never joined from a static destructor under the loader's lock.
        OwnerThread* owner_;
        std::atomic<weft_runtime*> runtime_{nullptr};
        std::atomic<std::uint64_t> epoch_{0};
        mutable std::mutex lock_;
        std::vector<Kept> views_;
    };
} // namespace weft_unity
