#include "draw_queue.hpp"
#include "render_device.hpp"
#include "weft_unity.h"

#include "IUnityGraphics.h"
#include "IUnityInterface.h"
#include "IUnityLog.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstring>
#include <mutex>
#include <unordered_map>

// Unity's side of the plugin: its graphics device's events make and end the render device of its API, and its render
// thread's plugin event (weft_unity_render_event, with a draw's id as its data) draws the draws the main thread queued.
// What it draws with, and why a draw failed (when the reason changes), go to Unity's log.
namespace {
    IUnityInterfaces* unity = nullptr;
    IUnityGraphics* graphics = nullptr;
    IUnityLog* unity_log = nullptr;
    int event_id = 0;
    std::unique_ptr<weft_unity::RenderDevice> device; // the render thread's

    std::mutex stats_lock;
    weft_unity_render_stats stats{};

    std::atomic<bool> measuring_draws{false};
    std::mutex draw_times_lock;
    std::unordered_map<std::uint64_t, double> draw_times;

    void say(UnityLogType type, const std::string& text) {
        if (unity_log) {
            const std::string line = "weft: " + text;
            unity_log->Log(type, line.c_str(), __FILE__, __LINE__);
        }
    }

    void note(bool drawn, const std::string& why) {
        bool changed = false;
        {
            std::lock_guard lock(stats_lock);
            if (drawn) {
                ++stats.draws;
                return;
            }
            ++stats.failures;
            changed = why != stats.last_failure;
            const auto size = std::min(why.size(), sizeof(stats.last_failure) - 1);
            std::memcpy(stats.last_failure, why.data(), size);
            stats.last_failure[size] = 0;
        }
        if (changed) {
            say(kUnityLogTypeWarning, "a frame did not draw: " + why);
        }
    }

    const char* api_name(UnityGfxRenderer renderer) {
        switch (renderer) {
        case kUnityGfxRendererD3D11:
            return "Direct3D 11";
        case kUnityGfxRendererD3D12:
            return "Direct3D 12";
        case kUnityGfxRendererVulkan:
            return "Vulkan";
        default:
            return "an API weft has no backend for";
        }
    }

    void UNITY_INTERFACE_API on_device_event(UnityGfxDeviceEventType type) {
        if (type == kUnityGfxDeviceEventInitialize) {
            const UnityGfxRenderer renderer = graphics->GetRenderer();
            if (renderer == kUnityGfxRendererNull || device) {
                // No device yet (the plugin preloads before it: its own event follows), or one already.
                return;
            }
            switch (renderer) {
            case kUnityGfxRendererD3D11:
                device = weft_unity::make_d3d11_device(unity);
                break;
            case kUnityGfxRendererD3D12:
                device = weft_unity::make_d3d12_device(unity, event_id);
                break;
#if WEFT_UNITY_VULKAN
            case kUnityGfxRendererVulkan:
                device = weft_unity::make_vulkan_device(unity, event_id);
                break;
#endif
            default:
                break;
            }
            {
                std::lock_guard lock(stats_lock);
                stats.renderer = std::uint32_t(renderer);
            }
            if (device) {
                device->warn = [](const std::string& text) { say(kUnityLogTypeWarning, text); };
                say(kUnityLogTypeLog, std::string("drawing with ") + api_name(renderer) + " (weft " + WEFT_UNITY_COMMIT + ")");
            } else {
                note(false, std::string("no renderer for ") + api_name(renderer) + " (" + std::to_string(int(renderer)) + ")");
            }
        } else if (type == kUnityGfxDeviceEventShutdown) {
            if (device) {
                device->shutdown();
                device.reset();
            }
        }
    }

    void UNITY_INTERFACE_API on_render(int id, void* data) {
        if (id != event_id) {
            return;
        }
        const auto draw = weft_unity::DrawQueue::get().take(std::uint32_t(reinterpret_cast<std::uintptr_t>(data)));
        if (!draw) {
            return;
        }
        std::string why = "no renderer for Unity's graphics API";
        const bool measure = measuring_draws.load(std::memory_order_relaxed);
        const auto started = measure ? std::chrono::steady_clock::now() : std::chrono::steady_clock::time_point{};
        const bool drawn = device && device->draw(*draw, why);
        if (measure && drawn) {
            const double seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count();
            const std::uint64_t key = (std::uint64_t(draw->view.slot) << 32) | draw->view.generation;
            std::lock_guard lock(draw_times_lock);
            draw_times[key] += seconds;
        }
        weft_view_frame_release(draw->frame);
        note(drawn, why);
    }
} // namespace

extern "C" {
void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* interfaces) {
    unity = interfaces;
    graphics = unity->Get<IUnityGraphics>();
    unity_log = unity->Get<IUnityLog>();
    event_id = graphics->ReserveEventIDRange(1);
    graphics->RegisterDeviceEventCallback(on_device_event);
    // The device is up already when the plugin loads on first use.
    on_device_event(kUnityGfxDeviceEventInitialize);
}

void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload() {
    graphics->UnregisterDeviceEventCallback(on_device_event);
}

void* weft_unity_render_event(void) {
    return reinterpret_cast<void*>(&on_render);
}

int weft_unity_render_event_id(void) {
    return event_id;
}

void weft_unity_render_get_stats(weft_unity_render_stats* out_stats) {
    if (out_stats) {
        std::lock_guard lock(stats_lock);
        *out_stats = stats;
    }
}

void weft_unity_render_set_measuring(uint32_t enabled) {
    const bool on = enabled != 0;
    measuring_draws.store(on, std::memory_order_relaxed);
    if (!on) {
        std::lock_guard lock(draw_times_lock);
        draw_times.clear();
    }
}

uint32_t weft_unity_render_take_draw_times(weft_unity_draw_time* out_times, uint32_t capacity) {
    std::unordered_map<std::uint64_t, double> taken;
    {
        std::lock_guard lock(draw_times_lock);
        taken.swap(draw_times);
    }
    if (!out_times || !capacity) {
        return 0;
    }
    uint32_t count = 0;
    for (const auto& [key, seconds] : taken) {
        if (count >= capacity) {
            break;
        }
        out_times[count++] = {std::uint32_t(key >> 32), std::uint32_t(key & 0xffffffffu), seconds};
    }
    return count;
}
}
