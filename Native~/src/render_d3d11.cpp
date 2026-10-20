#include "render_device.hpp"

#include <d3d11_1.h>

#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D11.h"
#include "IUnityInterface.h"
#include "weft/render_backend_d3d11.h"

namespace weft_unity {
    namespace {
        // The UNORM view format of a texture's format, and weft's name of it; false for one weft does not draw into.
        bool target_format(DXGI_FORMAT format, DXGI_FORMAT& view, std::uint32_t& weft) {
            switch (format) {
            case DXGI_FORMAT_R8G8B8A8_TYPELESS:
            case DXGI_FORMAT_R8G8B8A8_UNORM:
            case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
                view = DXGI_FORMAT_R8G8B8A8_UNORM;
                weft = WEFT_RENDER_TARGET_LINEAR_RGBA8;
                return true;
            case DXGI_FORMAT_B8G8R8A8_TYPELESS:
            case DXGI_FORMAT_B8G8R8A8_UNORM:
            case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
                view = DXGI_FORMAT_B8G8R8A8_UNORM;
                weft = WEFT_RENDER_TARGET_LINEAR_BGRA8;
                return true;
            default:
                return false;
            }
        }

        // D3D11: the pass draws on Unity's immediate context while it encodes, so a recording is done with the GPU as
        // far as weft is concerned once it is recorded (the context keeps what its commands use). The pass sets the
        // state it needs and clears the context's state after; Unity's renderer keeps a copy of that state and does not
        // set again what it believes is set, so the pass draws in a context state of its own, swapped in for Unity's
        // and out again (D3D11.1).
        class D3D11Device final : public RenderDevice {
        public:
            explicit D3D11Device(ID3D11Device* device) : device_(device) {
            }

            ~D3D11Device() override {
                if (state_) {
                    state_->Release();
                }
            }

        protected:
            bool open(Slot& slot, weft_render_backend& table, std::string& why) override {
                const weft_render_d3d11_desc desc{sizeof(desc), WEFT_RENDER_ABI_VERSION, device_, queue, 0};
                weft_render_d3d11* backend = nullptr;
                if (const auto result = weft_render_d3d11_create(&desc, &backend, &table); result != WEFT_RENDER_OK) {
                    why = "no D3D11 backend: " + describe(result);
                    return false;
                }
                slot.backend = backend;
                return true;
            }

            void close(Slot& slot) override {
                if (slot.backend) {
                    weft_render_d3d11_destroy(static_cast<weft_render_d3d11*>(slot.backend));
                    slot.backend = nullptr;
                }
            }

            bool encode(Slot& slot, weft_render_handle prepared, const Draw& draw, std::string& why) override {
                auto* texture = static_cast<ID3D11Texture2D*>(draw.target);
                D3D11_TEXTURE2D_DESC desc{};
                texture->GetDesc(&desc);
                DXGI_FORMAT format{};
                std::uint32_t weft = 0;
                if (!target_format(desc.Format, format, weft) || desc.SampleDesc.Count != 1 ||
                    !(desc.BindFlags & D3D11_BIND_RENDER_TARGET) || desc.Width < draw.width || desc.Height < draw.height) {
                    why = "the target is not a single-sample RGBA8 or BGRA8 render texture of the view's size";
                    return false;
                }
                // A view of the texture for the pass alone: the context keeps it while its commands use it.
                D3D11_RENDER_TARGET_VIEW_DESC view_desc{};
                view_desc.Format = format;
                view_desc.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
                ID3D11RenderTargetView* view = nullptr;
                if (FAILED(device_->CreateRenderTargetView(texture, &view_desc, &view))) {
                    why = "no render target view of the target";
                    return false;
                }
                ID3D11DeviceContext1* context = immediate(why);
                if (!context) {
                    view->Release();
                    return false;
                }
                void* command_list = nullptr;
                const weft_render_d3d11_pass native{sizeof(native), WEFT_RENDER_ABI_VERSION, nullptr, view, &command_list,
                                                    draw.clear ? 1u : 0u, 0, {0, 0, 0, 0}};
                const weft_render_pass pass{sizeof(pass), WEFT_RENDER_ABI_VERSION, slot.identity, draw.width, draw.height, weft, 1,
                                            &native, sizeof(native)};
                weft_render_handle use{};
                ID3DDeviceContextState* unity = nullptr;
                context->SwapDeviceContextState(state_, &unity);
                const auto result = weft_renderer_encode(slot.renderer, prepared, &pass, &use);
                context->SwapDeviceContextState(unity, nullptr);
                if (unity) {
                    unity->Release();
                }
                context->Release();
                view->Release();
                if (result != WEFT_RENDER_OK) {
                    why = "encode failed: " + describe(result);
                    return false;
                }
                submit(slot, use);
                return true;
            }

            std::uint64_t mark() override {
                return 0;
            }

            std::uint64_t finished() override {
                return 0;
            }

        private:
            // Unity's immediate context, with the state the passes draw in made the first time (single-threaded as
            // Unity's device is); null, and why, without D3D11.1.
            ID3D11DeviceContext1* immediate(std::string& why) {
                if (!state_) {
                    ID3D11Device1* device = nullptr;
                    if (FAILED(device_->QueryInterface(__uuidof(ID3D11Device1), reinterpret_cast<void**>(&device)))) {
                        why = "the D3D11 device is not a D3D11.1 one";
                        return nullptr;
                    }
                    const D3D_FEATURE_LEVEL level = device_->GetFeatureLevel();
                    const UINT flags = (device_->GetCreationFlags() & D3D11_CREATE_DEVICE_SINGLETHREADED)
                                           ? D3D11_1_CREATE_DEVICE_CONTEXT_STATE_SINGLETHREADED
                                           : 0;
                    const HRESULT made = device->CreateDeviceContextState(flags, &level, 1, D3D11_SDK_VERSION,
                                                                          __uuidof(ID3D11Device1), nullptr, &state_);
                    device->Release();
                    if (FAILED(made)) {
                        state_ = nullptr;
                        why = "no context state for the pass";
                        return nullptr;
                    }
                }
                ID3D11DeviceContext* context = nullptr;
                device_->GetImmediateContext(&context);
                ID3D11DeviceContext1* context1 = nullptr;
                const HRESULT found = context->QueryInterface(__uuidof(ID3D11DeviceContext1), reinterpret_cast<void**>(&context1));
                context->Release();
                if (FAILED(found)) {
                    why = "the immediate context is not a D3D11.1 one";
                    return nullptr;
                }
                return context1;
            }

            ID3D11Device* device_;
            ID3DDeviceContextState* state_ = nullptr;
        };
    } // namespace

    std::unique_ptr<RenderDevice> make_d3d11_device(IUnityInterfaces* unity) {
        auto* graphics = unity->Get<IUnityGraphicsD3D11>();
        ID3D11Device* device = graphics ? graphics->GetDevice() : nullptr;
        if (!device) {
            return nullptr;
        }
        return std::make_unique<D3D11Device>(device);
    }
} // namespace weft_unity
