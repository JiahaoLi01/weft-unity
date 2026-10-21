// The plugin's native contract outside Unity: weft.dll driven as Unity drives it, through stand-in Unity interfaces
// (IUnityGraphics, IUnityGraphicsD3D11 or IUnityGraphicsD3D12v8, IUnityLog) over a real device.
//
// D3D11: the runtime starts, a View loads a page, its frame is queued on the main thread and drawn by the render event
// on Unity's render thread, and its pixels are read back; messages go both ways; pointer input and hit tests; keys,
// their text and the window's focus at a page's focused element; a target weft cannot draw into is a failure counted
// and said, not a crash; the runtime stops and starts again (a new renderer for the new runtime).
// D3D12: the stand-in records Unity's frames into a command list, tracks resource states as Unity does
// (RequestResourceState, NotifyResourceState) and signals its frame fence: frames are drawn into a texture that was a
// shader resource, recordings complete by the fence, and a renderer of a stopped runtime is destroyed once they have.
// Every step has a deadline: a hang names its step. The API's debug layer runs where it is installed (Windows' Graphics
// Tools), and an error it reports fails the run.
//
//   weft_unity_tests [--api d3d11|d3d12] [--warp]
//   weft_unity_tests --page <folder> [--click x,y]...   (a page.js and page.css loaded and run, clicked at CSS px:
//                                                        the load's result, the page's diagnostics and messages)
#include <d3d11.h>
#include <d3d12.h>
#include <dxgi.h>
#include <dxgi1_4.h>

#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D11.h"
#include "IUnityGraphicsD3D12.h"
#include "IUnityInterface.h"
#include "IUnityLog.h"
#include "weft_unity.h"

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <functional>
#include <map>
#include <mutex>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* interfaces);
extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload();

namespace {
    int failures = 0;

    void check(bool ok, const std::string& what) {
        std::printf("%s %s\n", ok ? "ok  " : "FAIL", what.c_str());
        if (!ok) {
            ++failures;
        }
    }

    // Ends the process when a step outlives its deadline, naming the step.
    class Watchdog {
    public:
        Watchdog() : thread_([this] { run(); }) {
        }

        ~Watchdog() {
            {
                std::lock_guard lock(lock_);
                done_ = true;
            }
            wake_.notify_all();
            thread_.join();
        }

        void step(const std::string& name) {
            std::lock_guard lock(lock_);
            step_ = name;
            deadline_ = std::chrono::steady_clock::now() + std::chrono::seconds(30);
            wake_.notify_all();
        }

    private:
        void run() {
            std::unique_lock lock(lock_);
            while (!done_) {
                if (!step_.empty() && std::chrono::steady_clock::now() > deadline_) {
                    std::printf("FAIL hung in: %s\n", step_.c_str());
                    std::fflush(stdout);
                    std::_Exit(3);
                }
                wake_.wait_for(lock, std::chrono::milliseconds(200));
            }
        }

        std::mutex lock_;
        std::condition_variable wake_;
        std::string step_;
        std::chrono::steady_clock::time_point deadline_{};
        bool done_ = false;
        std::thread thread_;
    };

    // ---- Stand-in Unity interfaces. ----

    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* context = nullptr;
    IUnityGraphicsDeviceEventCallback device_callback = nullptr;
    std::vector<std::string> logged;
    std::mutex logged_lock;

    UnityGfxRenderer renderer_kind = kUnityGfxRendererD3D11;

    UnityGfxRenderer UNITY_INTERFACE_API get_renderer() {
        return renderer_kind;
    }
    void UNITY_INTERFACE_API register_callback(IUnityGraphicsDeviceEventCallback callback) {
        device_callback = callback;
    }
    void UNITY_INTERFACE_API unregister_callback(IUnityGraphicsDeviceEventCallback callback) {
        if (device_callback == callback) {
            device_callback = nullptr;
        }
    }
    int UNITY_INTERFACE_API reserve_events(int) {
        return 7000;
    }
    ID3D11Device* UNITY_INTERFACE_API get_device() {
        return device;
    }
    void UNITY_INTERFACE_API log_line(UnityLogType, const char* message, const char*, const int) {
        std::lock_guard lock(logged_lock);
        logged.emplace_back(message);
        std::printf("     unity log: %s\n", message);
    }

    IUnityGraphics graphics{};
    IUnityGraphicsD3D11 graphics_d3d11{};
    IUnityGraphicsD3D12v8 graphics_d3d12{};
    IUnityLog log_interface{};

    IUnityInterface* UNITY_INTERFACE_API get_interface(UnityInterfaceGUID guid) {
        if (guid == GetUnityInterfaceGUID<IUnityGraphics>()) {
            return &graphics;
        }
        if (guid == GetUnityInterfaceGUID<IUnityGraphicsD3D11>() && renderer_kind == kUnityGfxRendererD3D11) {
            return &graphics_d3d11;
        }
        if (guid == GetUnityInterfaceGUID<IUnityGraphicsD3D12v8>() && renderer_kind == kUnityGfxRendererD3D12) {
            return &graphics_d3d12;
        }
        if (guid == GetUnityInterfaceGUID<IUnityLog>()) {
            return &log_interface;
        }
        return nullptr;
    }
    IUnityInterface* UNITY_INTERFACE_API get_interface_split(unsigned long long high, unsigned long long low) {
        return get_interface(UnityInterfaceGUID(high, low));
    }
    void UNITY_INTERFACE_API register_interface(UnityInterfaceGUID, IUnityInterface*) {
    }
    void UNITY_INTERFACE_API register_interface_split(unsigned long long, unsigned long long, IUnityInterface*) {
    }

    // ---- The plugin's side. ----

    // Unity's render thread: one thread that runs the render events, in order.
    class RenderThread {
    public:
        RenderThread() : thread_([this] { run(); }) {
        }

        ~RenderThread() {
            post({});
            thread_.join();
        }

        // Runs the render event for a draw there and waits for it.
        void render(std::uint32_t id) {
            using Event = void(UNITY_INTERFACE_API*)(int, void*);
            auto event = reinterpret_cast<Event>(weft_unity_render_event());
            const int event_id = weft_unity_render_event_id();
            post([=] { event(event_id, reinterpret_cast<void*>(std::uintptr_t(id))); });
        }

        // Runs work there (Unity's device events come from the render thread too) and waits for it.
        void post(std::function<void()> work) {
            std::unique_lock lock(lock_);
            work_ = std::move(work);
            stop_ = !work_;
            pending_ = true;
            wake_.notify_all();
            wake_.wait(lock, [this] { return !pending_; });
        }

    private:
        void run() {
            std::unique_lock lock(lock_);
            for (;;) {
                wake_.wait(lock, [this] { return pending_; });
                if (stop_) {
                    pending_ = false;
                    wake_.notify_all();
                    return;
                }
                work_();
                pending_ = false;
                wake_.notify_all();
            }
        }

        std::mutex lock_;
        std::condition_variable wake_;
        std::function<void()> work_;
        bool pending_ = false;
        bool stop_ = false;
        std::thread thread_;
    };

    weft_unity_render_stats stats() {
        weft_unity_render_stats s{};
        weft_unity_render_get_stats(&s);
        return s;
    }

    weft_view_handle make_view(float width, float height) {
        const weft_view_desc desc{sizeof(desc), 0, width, height, 1, 0};
        weft_view_handle view{};
        check(weft_unity_view_create(&desc, &view) == WEFT_OK, "a View is made");
        return view;
    }

    void load(weft_view_handle view, std::string_view script, std::string_view css) {
        const weft_script s{"page.js", 7, script.data(), script.size()};
        const weft_page_desc page{sizeof(page), 0, &s, 1, 0, css.data(), css.size(), nullptr, 0, nullptr, 0, nullptr, 0};
        check(weft_unity_view_load(view, &page) == WEFT_OK, "the page loads");
    }

    struct Advanced {
        std::vector<std::pair<std::string, std::string>> messages;
        std::vector<std::string> diagnostics;
    };

    Advanced advance(weft_view_handle view, double time) {
        std::uint32_t messages = 0, diagnostics = 0;
        Advanced out;
        if (weft_unity_view_advance(view, time, &messages, &diagnostics) != WEFT_OK) {
            out.diagnostics.emplace_back("advance failed");
        }
        for (std::uint32_t i = 0; i < messages; ++i) {
            weft_unity_message m{};
            if (weft_unity_view_message(view, i, &m) == WEFT_OK) {
                out.messages.emplace_back(std::string(m.name, m.name_size), std::string(m.json, m.json_size));
            }
        }
        for (std::uint32_t i = 0; i < diagnostics; ++i) {
            std::uint32_t kind = 0;
            const char* text = nullptr;
            size_t size = 0;
            if (weft_unity_view_diagnostic(view, i, &kind, &text, &size) == WEFT_OK) {
                out.diagnostics.emplace_back(text, size);
            }
        }
        return out;
    }

    ID3D11Texture2D* make_target(DXGI_FORMAT format, UINT width, UINT height) {
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = width;
        desc.Height = height;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = format;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        ID3D11Texture2D* texture = nullptr;
        device->CreateTexture2D(&desc, nullptr, &texture);
        return texture;
    }

    // The texture's BGRA texels, top row first.
    std::vector<std::uint32_t> read(ID3D11Texture2D* texture) {
        D3D11_TEXTURE2D_DESC desc{};
        texture->GetDesc(&desc);
        desc.Usage = D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        ID3D11Texture2D* staging = nullptr;
        device->CreateTexture2D(&desc, nullptr, &staging);
        context->CopyResource(staging, texture);
        D3D11_MAPPED_SUBRESOURCE mapped{};
        std::vector<std::uint32_t> out(desc.Width * desc.Height);
        if (SUCCEEDED(context->Map(staging, 0, D3D11_MAP_READ, 0, &mapped))) {
            for (UINT y = 0; y < desc.Height; ++y) {
                std::memcpy(&out[y * desc.Width], static_cast<const std::uint8_t*>(mapped.pData) + y * mapped.RowPitch, desc.Width * 4);
            }
            context->Unmap(staging, 0);
        }
        staging->Release();
        return out;
    }

    // A camera's 8x8 render texture as Unity's are on D3D11: RGBA16F, its bottom row first in memory (Unity draws a
    // camera's image into it upside down), the image's top half red and its bottom half grey (0.5, linear).
    ID3D11Texture2D* make_camera_texture() {
        constexpr std::uint16_t one = 0x3c00;
        constexpr std::uint16_t half = 0x3800;
        std::vector<std::uint16_t> texels(8 * 8 * 4);
        for (UINT row = 0; row < 8; ++row) {
            const bool top = row >= 4;
            for (UINT x = 0; x < 8; ++x) {
                auto* t = &texels[(row * 8 + x) * 4];
                t[0] = top ? one : half;
                t[1] = top ? 0 : half;
                t[2] = top ? 0 : half;
                t[3] = one;
            }
        }
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = 8;
        desc.Height = 8;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_R16G16B16A16_TYPELESS;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        const D3D11_SUBRESOURCE_DATA data{texels.data(), 8 * 4 * sizeof(std::uint16_t), 0};
        ID3D11Texture2D* texture = nullptr;
        device->CreateTexture2D(&desc, &data, &texture);
        return texture;
    }

    std::string hex(std::uint32_t bgra) {
        char text[16];
        std::snprintf(text, sizeof(text), "%08x", bgra);
        return text;
    }

    constexpr UINT width = 64;
    constexpr UINT height = 32;
    // Page colours as BGRA texels in memory (little-endian: 0xAARRGGBB).
    constexpr std::uint32_t red = 0xffff0000;
    constexpr std::uint32_t blue = 0xff0000ff;

    std::string box_page(const char* colour) {
        return std::string("#root{position:relative;width:100%;height:100%}"
                           ".a{position:absolute;left:0;top:0;width:32px;height:16px;background-color:") +
               colour + "}";
    }

    constexpr std::string_view box_script =
        "document.getElementById('root').appendChild(document.createElement('div')).className = 'a';"
        "weft.engine.on('ping', v => weft.engine.send('pong', {n: v.n + 1, s: 'é\"'}));";
} // namespace

namespace {
    void stand_in_interfaces(IUnityInterfaces& interfaces) {
        graphics.GetRenderer = &get_renderer;
        graphics.RegisterDeviceEventCallback = &register_callback;
        graphics.UnregisterDeviceEventCallback = &unregister_callback;
        graphics.ReserveEventIDRange = &reserve_events;
        log_interface.Log = &log_line;
        interfaces = {&get_interface, &register_interface, &get_interface_split, &register_interface_split};
    }

    bool logged_line(std::string_view text) {
        std::lock_guard lock(logged_lock);
        for (const auto& line : logged) {
            if (line.find(text) != std::string::npos) {
                return true;
            }
        }
        return false;
    }

int run_d3d11(bool warp, Watchdog& watchdog, RenderThread& render_thread) {
    const auto render = [&](std::uint32_t id) { render_thread.render(id); };

    watchdog.step("the D3D11 device");
    const D3D_FEATURE_LEVEL levels[]{D3D_FEATURE_LEVEL_11_0};
    const auto driver = warp ? D3D_DRIVER_TYPE_WARP : D3D_DRIVER_TYPE_HARDWARE;
    const bool debug = SUCCEEDED(D3D11CreateDevice(nullptr, driver, nullptr, D3D11_CREATE_DEVICE_DEBUG, levels, 1, D3D11_SDK_VERSION,
                                                   &device, nullptr, &context));
    if (!debug && FAILED(D3D11CreateDevice(nullptr, driver, nullptr, 0, levels, 1, D3D11_SDK_VERSION, &device, nullptr, &context))) {
        std::printf("FAIL no D3D11 device\n");
        return 2;
    }
    std::printf("     the D3D11 debug layer is %s\n", debug ? "on" : "not installed");
    graphics_d3d11.GetDevice = &get_device;
    IUnityInterfaces interfaces{};
    stand_in_interfaces(interfaces);

    watchdog.step("UnityPluginLoad");
    UnityPluginLoad(&interfaces);
    check(device_callback != nullptr, "the plugin listens to Unity's device events");
    check(stats().renderer == kUnityGfxRendererD3D11, "it draws with D3D11");
    check(logged_line("drawing with Direct3D 11"), "it says so in Unity's log");

    watchdog.step("weft_unity_start");
    check(weft_unity_start(WEFT_RUNTIME_SYSTEM_FONTS, 0) == WEFT_OK, "the runtime starts (max_views 0: weft's default)");
    check(weft_unity_start(WEFT_RUNTIME_SYSTEM_FONTS, 0) == WEFT_BUSY, "a second start is busy");
    check(weft_unity_runtime() != nullptr, "the runtime runs");

    watchdog.step("the first page");
    auto view = make_view(float(width), float(height));
    load(view, box_script, box_page("#ff0000"));
    auto first = advance(view, 0.016);
    check(first.diagnostics.empty(), "no diagnostics" + (first.diagnostics.empty() ? "" : ": " + first.diagnostics.front()));

    watchdog.step("the first draw");
    ID3D11Texture2D* target = make_target(DXGI_FORMAT_B8G8R8A8_TYPELESS, width, height);
    const std::uint32_t id = weft_unity_queue_draw(view, target, width, height, 1, 1);
    check(id != 0, "the frame is queued");
    render(id);
    auto pixels = read(target);
    check(stats().draws == 1 && stats().failures == 0, std::string("it is drawn (") + stats().last_failure + ")");
    check(pixels[4 * width + 8] == red, "the box at the page's top left, the texture's first rows: " + hex(pixels[4 * width + 8]));
    check(pixels[4 * width + 48] == 0, "the top right is cleared: " + hex(pixels[4 * width + 48]));
    check(pixels[27 * width + 8] == 0, "the bottom left is cleared: " + hex(pixels[27 * width + 8]));
    render(id);
    check(stats().draws == 1, "a draw id is taken once");

    watchdog.step("Unity's context state");
    // Unity's renderer keeps a copy of the immediate context's state and does not set again what it believes is set:
    // a pass leaves the context's state as it found it.
    const D3D11_VIEWPORT unity_viewport{3, 5, 40, 20, 0, 1};
    render_thread.post([&] { context->RSSetViewports(1, &unity_viewport); });
    render(weft_unity_queue_draw(view, target, width, height, 1, 1));
    D3D11_VIEWPORT kept_viewport{};
    UINT viewports = 1;
    render_thread.post([&] { context->RSGetViewports(&viewports, &kept_viewport); });
    check(viewports == 1 && kept_viewport.TopLeftX == 3 && kept_viewport.TopLeftY == 5 && kept_viewport.Width == 40 &&
              kept_viewport.Height == 20,
          "the pass leaves Unity's viewport as it was (" + std::to_string(viewports) + " viewports)");
    render_thread.post([&] { context->ClearState(); });

    watchdog.step("messages");
    const std::string ping = "{\"n\":41}";
    check(weft_view_emit(weft_unity_runtime(), view, "ping", 4, WEFT_VALUE_JSON, ping.data(), ping.size(), 0) == WEFT_OK,
          "an event is emitted from the main thread");
    auto second = advance(view, 0.032);
    check(second.messages.size() == 1 && second.messages[0].first == "pong", "the page answers");
    if (!second.messages.empty()) {
        check(second.messages[0].second == "{\"n\":42,\"s\":\"é\\\"\"}", "its value as JSON: " + second.messages[0].second);
    }

    watchdog.step("pointer and hit test");
    weft_hit hit{sizeof(hit)};
    check(weft_unity_view_hit_test(view, 8, 4, &hit) == WEFT_OK && hit.element == 1, "the box is hit");
    hit = {sizeof(hit)};
    check(weft_unity_view_hit_test(view, 48, 4, &hit) == WEFT_OK && hit.element == 0, "beside it is the root");
    const weft_pointer_event move{sizeof(move), WEFT_POINTER_MOVE, 8, 4, 0, 0, 0, 0};
    std::uint32_t prevented = 1;
    check(weft_unity_view_pointer(view, &move, &prevented) == WEFT_OK && prevented == 0, "a pointer move");

    watchdog.step("keyboard");
    // Keys go to the page's focused element with Chrome's default actions (Tab moves the focus to the button, Enter
    // clicks it), the text a key typed is a keypress, and the window's focus leaving the View blurs the element.
    auto keys = make_view(float(width), float(height));
    load(keys,
         "const b = document.getElementById('root').appendChild(document.createElement('button'));"
         "for (const type of ['focus', 'blur', 'click']) b.addEventListener(type, () => weft.engine.send('log', type));"
         "document.addEventListener('keypress', e => weft.engine.send('log', 'keypress:' + e.key));",
         "#root{position:relative;width:100%;height:100%}button{position:absolute;left:0;top:0;width:20px;height:10px}");
    advance(keys, 0.016);
    const auto key = [&](weft_key_kind kind, std::string_view value, std::string_view code) {
        const weft_key_event e{sizeof(e), std::uint32_t(kind), value.data(), value.size(), code.data(), code.size(), 0, 0, 0, 0};
        std::uint32_t handled = 0;
        return weft_unity_view_key(keys, &e, &handled) == WEFT_OK ? handled : 0xffffffffu;
    };
    check(key(WEFT_KEY_DOWN, "Tab", "Tab") == WEFT_INPUT_DEFAULT, "Tab moves the focus");
    check(key(WEFT_KEY_UP, "Tab", "Tab") == 0, "its keyup is the game's");
    check(key(WEFT_KEY_DOWN, "Enter", "Enter") == WEFT_INPUT_DEFAULT, "Enter clicks the focused button");
    key(WEFT_KEY_UP, "Enter", "Enter");
    std::uint32_t handled = 1;
    check(key(WEFT_KEY_DOWN, "a", "KeyA") == 0 && weft_unity_view_text(keys, "a", 1, &handled) == WEFT_OK && handled == 0,
          "a letter's keydown and keypress, which the page leaves");
    key(WEFT_KEY_UP, "a", "KeyA");
    check(weft_unity_view_set_focus(keys, 0) == WEFT_OK, "the window's focus leaves the View");
    std::string log;
    for (const auto& [name, json] : advance(keys, 0.032).messages) {
        log += (log.empty() ? "" : ",") + json;
    }
    check(log == "\"focus\",\"keypress:Enter\",\"click\",\"keypress:a\",\"blur\"", "the page's events: " + log);
    char enter[32]{};
    check(weft_keyboard_key("Enter", 5, 0, enter) == WEFT_OK && std::string_view(enter) == "Enter",
          "the key value of a physical key (weft's own, exported)");
    weft_unity_view_destroy(keys);

    watchdog.step("a target weft cannot draw into");
    ID3D11Texture2D* wrong = make_target(DXGI_FORMAT_R16G16B16A16_FLOAT, width, height);
    const std::uint32_t refused = weft_unity_queue_draw(view, wrong, width, height, 1, 1);
    render(refused);
    check(stats().failures == 1 && std::strstr(stats().last_failure, "not a single-sample") != nullptr,
          std::string("a failure, counted and said: ") + stats().last_failure);
    wrong->Release();

    watchdog.step("draws past the queue's capacity");
    for (int i = 0; i < 70; ++i) {
        weft_unity_queue_draw(view, target, width, height, 1, 1);
    }
    check(weft_unity_queue_draw(view, target, width, height, 1, 1) != 0, "old draws are dropped, new ones queue");

    watchdog.step("an engine texture");
    // Bound by name, drawn upright by a View after the first, and by one after that View is gone, as a game's Views
    // come and go.
    ID3D11Texture2D* camera = make_camera_texture();
    check(weft_unity_set_texture("probe", 5, camera, 8, 8) == WEFT_OK, "a texture is bound to a name");
    constexpr std::string_view textured_script =
        "document.getElementById('root').appendChild(document.createElement('div')).className = 't';";
    const std::string textured_page = "#root{position:relative;width:100%;height:100%}"
                                      ".t{position:absolute;left:0;top:0;width:64px;height:32px;"
                                      "background-image:-weft-texture(probe);background-size:100% 100%}";
    constexpr std::uint32_t grey = 0xffbcbcbc;
    for (const char* which : {"a second View", "a View after it"}) {
        auto textured = make_view(float(width), float(height));
        load(textured, textured_script, textured_page);
        advance(textured, 0.016);
        const auto drawn = stats().draws;
        render(weft_unity_queue_draw(textured, target, width, height, 1, 1));
        pixels = read(target);
        check(stats().draws == drawn + 1, std::string(which) + " draws (" + stats().last_failure + ")");
        check(pixels[4 * width + 16] == red, std::string(which) + ": the texture's top at the page's top: " + hex(pixels[4 * width + 16]));
        check(pixels[27 * width + 16] == grey,
              std::string(which) + ": its bottom at the page's bottom, linear grey encoded: " + hex(pixels[27 * width + 16]));
        weft_unity_view_destroy(textured);
    }
    check(weft_unity_set_texture("probe", 5, nullptr, 0, 0) == WEFT_OK, "the name is unbound");
    camera->Release();

    watchdog.step("stopping");
    weft_unity_stop();
    check(weft_unity_runtime() == nullptr, "the runtime stops");
    check(weft_unity_queue_draw(view, target, width, height, 1, 1) == 0, "nothing queues without a runtime");

    watchdog.step("a second runtime");
    check(weft_unity_start(WEFT_RUNTIME_SYSTEM_FONTS, 0) == WEFT_OK, "the runtime starts again");
    view = make_view(float(width), float(height));
    load(view, box_script, box_page("#0000ff"));
    advance(view, 0.016);
    const auto before = stats().draws;
    render(weft_unity_queue_draw(view, target, width, height, 1, 1));
    pixels = read(target);
    check(stats().draws == before + 1, std::string("the new runtime's frame is drawn (") + stats().last_failure + ")");
    check(pixels[4 * width + 8] == blue, "by a renderer of its own: " + hex(pixels[4 * width + 8]));

    watchdog.step("shutting down");
    weft_unity_stop();
    if (device_callback) {
        render_thread.post([] { device_callback(kUnityGfxDeviceEventShutdown); });
    }
    UnityPluginUnload();
    target->Release();
    if (debug) {
        ID3D11InfoQueue* queue = nullptr;
        if (SUCCEEDED(device->QueryInterface(__uuidof(ID3D11InfoQueue), reinterpret_cast<void**>(&queue)))) {
            int errors = 0;
            for (UINT64 i = 0, n = queue->GetNumStoredMessages(); i < n; ++i) {
                SIZE_T size = 0;
                queue->GetMessage(i, nullptr, &size);
                std::vector<char> bytes(size);
                auto* message = reinterpret_cast<D3D11_MESSAGE*>(bytes.data());
                if (SUCCEEDED(queue->GetMessage(i, message, &size)) && message->Severity <= D3D11_MESSAGE_SEVERITY_ERROR) {
                    std::printf("     D3D11: %.*s\n", int(message->DescriptionByteLength), message->pDescription);
                    ++errors;
                }
            }
            check(errors == 0, "the debug layer reports no errors");
            queue->Release();
        }
    }
    context->Release();
    device->Release();
    return 0;
}

    // ---- D3D12: Unity's frames on a stand-in. ----

    // A device, its direct queue, and Unity's frame: one command list recorded between begin and end, executed and
    // followed by a signal of the frame fence (GetNextFrameFenceValue is the value the frame being recorded gets).
    // Resource states are tracked as Unity tracks them: RequestResourceState records a barrier when the state differs,
    // NotifyResourceState takes the state a plugin left.
    struct D3D12Frames {
        ID3D12Device* device = nullptr;
        ID3D12CommandQueue* queue = nullptr;
        ID3D12CommandAllocator* allocator = nullptr;
        ID3D12GraphicsCommandList* list = nullptr;
        ID3D12Fence* fence = nullptr;
        HANDLE event = nullptr;
        std::uint64_t next = 1;
        bool recording = false;
        std::map<ID3D12Resource*, D3D12_RESOURCE_STATES> states;
        int configured_event = -1;
        std::uint32_t configured_flags = 0;
        int requests = 0;

        bool debug = false;

        bool open(bool warp) {
            ID3D12Debug* layer = nullptr;
            if (SUCCEEDED(D3D12GetDebugInterface(__uuidof(ID3D12Debug), reinterpret_cast<void**>(&layer)))) {
                layer->EnableDebugLayer();
                layer->Release();
                debug = true;
            }
            IDXGIAdapter* adapter = nullptr;
            if (warp) {
                IDXGIFactory4* factory = nullptr;
                if (FAILED(CreateDXGIFactory1(__uuidof(IDXGIFactory4), reinterpret_cast<void**>(&factory)))) {
                    return false;
                }
                factory->EnumWarpAdapter(__uuidof(IDXGIAdapter), reinterpret_cast<void**>(&adapter));
                factory->Release();
            }
            const HRESULT made =
                D3D12CreateDevice(adapter, D3D_FEATURE_LEVEL_11_0, __uuidof(ID3D12Device), reinterpret_cast<void**>(&device));
            if (adapter) {
                adapter->Release();
            }
            if (FAILED(made)) {
                return false;
            }
            D3D12_COMMAND_QUEUE_DESC desc{};
            desc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
            device->CreateCommandQueue(&desc, __uuidof(ID3D12CommandQueue), reinterpret_cast<void**>(&queue));
            device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, __uuidof(ID3D12CommandAllocator),
                                           reinterpret_cast<void**>(&allocator));
            device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator, nullptr, __uuidof(ID3D12GraphicsCommandList),
                                      reinterpret_cast<void**>(&list));
            if (list) {
                list->Close();
            }
            device->CreateFence(0, D3D12_FENCE_FLAG_NONE, __uuidof(ID3D12Fence), reinterpret_cast<void**>(&fence));
            event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
            return queue && allocator && list && fence && event;
        }

        void wait(std::uint64_t value) {
            if (fence->GetCompletedValue() < value) {
                fence->SetEventOnCompletion(value, event);
                WaitForSingleObject(event, INFINITE);
            }
        }

        void begin() {
            wait(next - 1);
            allocator->Reset();
            list->Reset(allocator, nullptr);
            recording = true;
        }

        void barrier(ID3D12Resource* resource, D3D12_RESOURCE_STATES to) {
            auto& state = states[resource];
            if (state == to) {
                return;
            }
            D3D12_RESOURCE_BARRIER b{};
            b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            b.Transition.pResource = resource;
            b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
            b.Transition.StateBefore = state;
            b.Transition.StateAfter = to;
            list->ResourceBarrier(1, &b);
            state = to;
        }

        // Ends the frame: the list executes, then the fence is signalled with the frame's value.
        void end() {
            list->Close();
            ID3D12CommandList* lists[]{list};
            queue->ExecuteCommandLists(1, lists);
            queue->Signal(fence, next++);
            recording = false;
        }

        // The errors the debug layer stored, printed.
        int errors() {
            ID3D12InfoQueue* info = nullptr;
            if (!debug || FAILED(device->QueryInterface(__uuidof(ID3D12InfoQueue), reinterpret_cast<void**>(&info)))) {
                return 0;
            }
            int count = 0;
            for (UINT64 i = 0, n = info->GetNumStoredMessages(); i < n; ++i) {
                SIZE_T size = 0;
                info->GetMessage(i, nullptr, &size);
                std::vector<char> bytes(size);
                auto* message = reinterpret_cast<D3D12_MESSAGE*>(bytes.data());
                if (SUCCEEDED(info->GetMessage(i, message, &size)) && message->Severity <= D3D12_MESSAGE_SEVERITY_ERROR) {
                    std::printf("     D3D12: %.*s\n", int(message->DescriptionByteLength), message->pDescription);
                    ++count;
                }
            }
            info->Release();
            return count;
        }

        void close() {
            wait(next - 1);
            for (IUnknown* object : {static_cast<IUnknown*>(list), static_cast<IUnknown*>(allocator), static_cast<IUnknown*>(fence),
                                     static_cast<IUnknown*>(queue), static_cast<IUnknown*>(device)}) {
                if (object) {
                    object->Release();
                }
            }
            CloseHandle(event);
        }
    };

    D3D12Frames frames12;

    ID3D12Device* UNITY_INTERFACE_API d3d12_device() {
        return frames12.device;
    }
    ID3D12Fence* UNITY_INTERFACE_API d3d12_fence() {
        return frames12.fence;
    }
    UINT64 UNITY_INTERFACE_API d3d12_next_value() {
        return frames12.next;
    }
    void UNITY_INTERFACE_API d3d12_configure(int event_id, const UnityD3D12PluginEventConfig* config) {
        frames12.configured_event = event_id;
        frames12.configured_flags = config ? config->flags : 0;
    }
    bool UNITY_INTERFACE_API d3d12_recording(UnityGraphicsD3D12RecordingState* state) {
        if (!frames12.recording) {
            return false;
        }
        state->commandList = frames12.list;
        return true;
    }
    void UNITY_INTERFACE_API d3d12_request(ID3D12Resource* resource, D3D12_RESOURCE_STATES state) {
        ++frames12.requests;
        frames12.barrier(resource, state);
    }
    void UNITY_INTERFACE_API d3d12_notify(ID3D12Resource* resource, D3D12_RESOURCE_STATES state, bool) {
        frames12.states[resource] = state;
    }

    ID3D12Resource* make_target12(UINT target_width, UINT target_height) {
        D3D12_HEAP_PROPERTIES heap{D3D12_HEAP_TYPE_DEFAULT};
        D3D12_RESOURCE_DESC desc{};
        desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        desc.Width = target_width;
        desc.Height = target_height;
        desc.DepthOrArraySize = 1;
        desc.MipLevels = 1;
        desc.Format = DXGI_FORMAT_B8G8R8A8_TYPELESS;
        desc.SampleDesc.Count = 1;
        desc.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
        ID3D12Resource* target = nullptr;
        // As a texture Unity samples: a shader resource until it is asked for as a render target.
        frames12.device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE,
                                                 nullptr, __uuidof(ID3D12Resource), reinterpret_cast<void**>(&target));
        if (target) {
            frames12.states[target] = D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
        }
        return target;
    }

    // The target's BGRA texels, top row first: copied in a frame of its own (as Unity's readback is).
    std::vector<std::uint32_t> read12(ID3D12Resource* target) {
        D3D12_RESOURCE_DESC desc = target->GetDesc();
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        D3D12_PLACED_SUBRESOURCE_FOOTPRINT footprint{};
        UINT64 total = 0;
        frames12.device->GetCopyableFootprints(&desc, 0, 1, 0, &footprint, nullptr, nullptr, &total);
        D3D12_HEAP_PROPERTIES heap{D3D12_HEAP_TYPE_READBACK};
        D3D12_RESOURCE_DESC buffer{};
        buffer.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        buffer.Width = total;
        buffer.Height = 1;
        buffer.DepthOrArraySize = 1;
        buffer.MipLevels = 1;
        buffer.SampleDesc.Count = 1;
        buffer.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        ID3D12Resource* readback = nullptr;
        frames12.device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &buffer, D3D12_RESOURCE_STATE_COPY_DEST, nullptr,
                                                 __uuidof(ID3D12Resource), reinterpret_cast<void**>(&readback));
        frames12.begin();
        frames12.barrier(target, D3D12_RESOURCE_STATE_COPY_SOURCE);
        D3D12_TEXTURE_COPY_LOCATION to{};
        to.pResource = readback;
        to.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
        to.PlacedFootprint = footprint;
        D3D12_TEXTURE_COPY_LOCATION from{};
        from.pResource = target;
        from.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
        from.SubresourceIndex = 0;
        frames12.list->CopyTextureRegion(&to, 0, 0, 0, &from, nullptr);
        frames12.barrier(target, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
        frames12.end();
        frames12.wait(frames12.next - 1);
        std::vector<std::uint32_t> out(width * height);
        void* mapped = nullptr;
        if (SUCCEEDED(readback->Map(0, nullptr, &mapped))) {
            for (UINT y = 0; y < height; ++y) {
                std::memcpy(&out[y * width],
                            static_cast<const std::uint8_t*>(mapped) + footprint.Offset + y * footprint.Footprint.RowPitch, width * 4);
            }
            readback->Unmap(0, nullptr);
        }
        readback->Release();
        return out;
    }

int run_d3d12(bool warp, Watchdog& watchdog, RenderThread& render_thread) {
    watchdog.step("the D3D12 device");
    renderer_kind = kUnityGfxRendererD3D12;
    if (!frames12.open(warp)) {
        std::printf("FAIL no D3D12 device\n");
        return 2;
    }
    std::printf("     the D3D12 debug layer is %s\n", frames12.debug ? "on" : "not installed");
    graphics_d3d12.GetDevice = &d3d12_device;
    graphics_d3d12.GetFrameFence = &d3d12_fence;
    graphics_d3d12.GetNextFrameFenceValue = &d3d12_next_value;
    graphics_d3d12.ConfigureEvent = &d3d12_configure;
    graphics_d3d12.CommandRecordingState = &d3d12_recording;
    graphics_d3d12.RequestResourceState = &d3d12_request;
    graphics_d3d12.NotifyResourceState = &d3d12_notify;
    IUnityInterfaces interfaces{};
    stand_in_interfaces(interfaces);

    watchdog.step("UnityPluginLoad");
    UnityPluginLoad(&interfaces);
    check(stats().renderer == kUnityGfxRendererD3D12, "it draws with D3D12");
    check(logged_line("drawing with Direct3D 12"), "it says so in Unity's log");
    check(frames12.configured_event == weft_unity_render_event_id(), "its render event is configured");
    check((frames12.configured_flags & kUnityD3D12EventConfigFlag_ModifiesCommandBuffersState) != 0,
          "as one that changes the command list's state");

    watchdog.step("a page");
    check(weft_unity_start(WEFT_RUNTIME_SYSTEM_FONTS, 0) == WEFT_OK, "the runtime starts");
    auto view = make_view(float(width), float(height));
    load(view, box_script, box_page("#ff0000"));
    advance(view, 0.016);

    // A frame of Unity's with the draw in it: the render event runs while the frame records.
    const auto frame = [&](std::uint32_t id) {
        render_thread.post([&] {
            frames12.begin();
            using Event = void(UNITY_INTERFACE_API*)(int, void*);
            reinterpret_cast<Event>(weft_unity_render_event())(weft_unity_render_event_id(), reinterpret_cast<void*>(std::uintptr_t(id)));
            frames12.end();
        });
    };

    watchdog.step("the first draw");
    ID3D12Resource* target = make_target12(width, height);
    frame(weft_unity_queue_draw(view, target, width, height, 1, 1));
    check(stats().draws == 1 && stats().failures == 0, std::string("it is drawn (") + stats().last_failure + ")");
    check(frames12.requests >= 1, "the target is asked for as a render target");
    check(frames12.states[target] == D3D12_RESOURCE_STATE_RENDER_TARGET, "and is left one");
    auto pixels = read12(target);
    check(pixels[4 * width + 8] == red, "the box at the page's top left: " + hex(pixels[4 * width + 8]));
    check(pixels[4 * width + 48] == 0 && pixels[27 * width + 8] == 0, "the rest is cleared");

    watchdog.step("frames in flight");
    for (int i = 0; i < 8; ++i) {
        advance(view, 0.032 + i * 0.016);
        frame(weft_unity_queue_draw(view, target, width, height, 1, 1));
    }
    check(stats().draws == 9 && stats().failures == 0,
          std::string("frames keep drawing as earlier ones complete (") + stats().last_failure + ")");

    watchdog.step("a second runtime");
    weft_unity_stop();
    check(weft_unity_start(WEFT_RUNTIME_SYSTEM_FONTS, 0) == WEFT_OK, "the runtime starts again");
    view = make_view(float(width), float(height));
    load(view, box_script, box_page("#0000ff"));
    advance(view, 0.016);
    frame(weft_unity_queue_draw(view, target, width, height, 1, 1));
    frame(weft_unity_queue_draw(view, target, width, height, 1, 1));
    check(stats().draws == 11 && stats().failures == 0, std::string("the new runtime's frames are drawn (") + stats().last_failure + ")");
    pixels = read12(target);
    check(pixels[4 * width + 8] == blue, "by a renderer of its own: " + hex(pixels[4 * width + 8]));

    watchdog.step("shutting down");
    weft_unity_stop();
    frames12.wait(frames12.next - 1);
    render_thread.post([] { device_callback(kUnityGfxDeviceEventShutdown); });
    UnityPluginUnload();
    target->Release();
    check(frames12.errors() == 0, "the debug layer reports no errors");
    frames12.close();
    return 0;
}
} // namespace

namespace {
    std::string read_file(const std::string& path) {
        std::string text;
        FILE* file = nullptr;
        if (fopen_s(&file, path.c_str(), "rb") == 0 && file) {
            char buffer[65536];
            for (size_t n; (n = std::fread(buffer, 1, sizeof(buffer), file)) > 0;) {
                text.append(buffer, n);
            }
            std::fclose(file);
        }
        return text;
    }

    // --page <folder>: a folder's page.js and page.css loaded into a 1280 x 720 View and run for a few frames; the load's
    // result and the page's diagnostics are printed.
    int check_page(const std::string& folder, const std::vector<std::pair<float, float>>& clicks) {
        const std::string script = read_file(folder + "/page.js");
        const std::string css = read_file(folder + "/page.css");
        const std::string base = folder + "/page.css";
        if (weft_unity_start(WEFT_RUNTIME_SYSTEM_FONTS, 0) != WEFT_OK) {
            std::printf("FAIL no runtime\n");
            return 2;
        }
        const weft_view_desc desc{sizeof(desc), 0, 1280, 720, 1, 0};
        weft_view_handle view{};
        weft_unity_view_create(&desc, &view);
        const weft_script s{"page.js", 7, script.data(), script.size()};
        const weft_page_desc page{sizeof(page), 0, &s, 1, 0, css.data(), css.size(), nullptr, 0, nullptr, 0, base.data(), base.size()};
        const weft_result loaded = weft_unity_view_load(view, &page);
        std::printf("load: %s\n", weft_result_string(loaded));
        for (int frame = 0; frame < 3 + int(clicks.size()); ++frame) {
            if (frame >= 3) {
                // A click: a move, a press and a release of the primary button at the point.
                const auto [x, y] = clicks[std::size_t(frame - 3)];
                weft_hit hit{sizeof(hit)};
                weft_unity_view_hit_test(view, x, y, &hit);
                std::printf("click %.0f,%.0f on %s (cursor %s)\n", x, y, hit.element ? "an element" : "the root", hit.cursor);
                const std::pair<weft_pointer_kind, std::uint32_t> steps[]{
                    {WEFT_POINTER_MOVE, 0u}, {WEFT_POINTER_DOWN, 1u}, {WEFT_POINTER_UP, 0u}};
                for (const auto& [kind, buttons] : steps) {
                    const weft_pointer_event e{sizeof(e), std::uint32_t(kind), x, y, buttons, 0, 0, 0};
                    std::uint32_t prevented = 0;
                    const auto result = weft_unity_view_pointer(view, &e, &prevented);
                    if (result != WEFT_OK) {
                        std::printf("pointer %u: %s\n", std::uint32_t(kind), weft_result_string(result));
                    }
                }
            }
            const auto out = advance(view, 0.016 * (frame + 1));
            for (const auto& d : out.diagnostics) {
                std::printf("diagnostic: %s\n", d.c_str());
            }
            for (const auto& [name, json] : out.messages) {
                std::printf("message: %s %s\n", name.c_str(), json.c_str());
            }
        }
        weft_unity_stop();
        return loaded == WEFT_OK ? 0 : 1;
    }
} // namespace

int main(int argc, char** argv) {
    bool warp = false;
    std::string api = "d3d11";
    for (int i = 1; i < argc; ++i) {
        const std::string_view arg = argv[i];
        if (arg == "--warp") {
            warp = true;
        } else if (arg == "--api" && i + 1 < argc) {
            api = argv[++i];
        } else if (arg == "--page" && i + 1 < argc) {
            // Then --click x,y ... (CSS px).
            std::vector<std::pair<float, float>> clicks;
            for (int j = i + 2; j + 1 < argc && std::string_view(argv[j]) == "--click"; j += 2) {
                float x = 0, y = 0;
                if (sscanf_s(argv[j + 1], "%f,%f", &x, &y) == 2) {
                    clicks.emplace_back(x, y);
                }
            }
            return check_page(argv[i + 1], clicks);
        }
    }
    std::printf("weft_unity_tests on %s%s\n", api.c_str(), warp ? " (WARP)" : "");
    Watchdog watchdog;
    RenderThread render_thread;
    const int setup = api == "d3d12" ? run_d3d12(warp, watchdog, render_thread) : run_d3d11(warp, watchdog, render_thread);
    if (setup) {
        return setup;
    }
    std::printf("%s\n", failures ? "FAILED" : "passed");
    return failures ? 1 : 0;
}
