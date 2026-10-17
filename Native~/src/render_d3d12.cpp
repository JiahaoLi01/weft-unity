#include "render_device.hpp"

#include <d3d12.h>
#include <dxgi.h>

#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D12.h"
#include "IUnityInterface.h"
#include "weft/render_backend_d3d12.h"

namespace weft_unity {
    namespace {
        // weft's name of a texture's format; false for one weft does not draw into.
        bool target_format(DXGI_FORMAT format, std::uint32_t& weft) {
            switch (format) {
            case DXGI_FORMAT_R8G8B8A8_TYPELESS:
            case DXGI_FORMAT_R8G8B8A8_UNORM:
                weft = WEFT_RENDER_TARGET_LINEAR_RGBA8;
                return true;
            case DXGI_FORMAT_B8G8R8A8_TYPELESS:
            case DXGI_FORMAT_B8G8R8A8_UNORM:
                weft = WEFT_RENDER_TARGET_LINEAR_BGRA8;
                return true;
            default:
                return false;
            }
        }

        // D3D12: the pass records into the command list Unity is recording; the recording is done with the GPU when
        // Unity's frame fence reaches the value it gives the frame.
        class D3D12Device final : public RenderDevice {
        public:
            explicit D3D12Device(IUnityGraphicsD3D12v8* unity) : unity_(unity) {
            }

        protected:
            bool open(Slot& slot, weft_render_backend& table, std::string& why) override {
                const weft_render_d3d12_desc desc{sizeof(desc), WEFT_RENDER_ABI_VERSION, unity_->GetDevice(), queue, 0};
                weft_render_d3d12* backend = nullptr;
                if (const auto result = weft_render_d3d12_create(&desc, &backend, &table); result != WEFT_RENDER_OK) {
                    why = "no D3D12 backend: " + describe(result);
                    return false;
                }
                slot.backend = backend;
                return true;
            }

            void close(Slot& slot) override {
                if (slot.backend) {
                    weft_render_d3d12_destroy(static_cast<weft_render_d3d12*>(slot.backend));
                    slot.backend = nullptr;
                }
            }

            bool encode(Slot& slot, weft_render_handle prepared, const Draw& draw, std::string& why) override {
                auto* target = static_cast<ID3D12Resource*>(draw.target);
                const D3D12_RESOURCE_DESC desc = target->GetDesc();
                std::uint32_t weft = 0;
                if (!target_format(desc.Format, weft) || desc.Dimension != D3D12_RESOURCE_DIMENSION_TEXTURE2D ||
                    desc.SampleDesc.Count != 1 || !(desc.Flags & D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET) ||
                    desc.Width < draw.width || desc.Height < draw.height) {
                    why = "the target is not a single-sample RGBA8 or BGRA8 render texture of the view's size";
                    return false;
                }
                // The target as a render target in Unity's command list, which the pass records into and leaves it in.
                unity_->RequestResourceState(target, D3D12_RESOURCE_STATE_RENDER_TARGET);
                UnityGraphicsD3D12RecordingState state{};
                if (!unity_->CommandRecordingState(&state) || !state.commandList) {
                    why = "Unity records no command list";
                    return false;
                }
                const weft_render_d3d12_pass native{sizeof(native), WEFT_RENDER_ABI_VERSION, state.commandList, target,
                                                    draw.clear ? 1u : 0u, 0, {0, 0, 0, 0}};
                const weft_render_pass pass{sizeof(pass), WEFT_RENDER_ABI_VERSION, slot.identity, draw.width, draw.height, weft, 1,
                                            &native, sizeof(native)};
                weft_render_handle use{};
                if (const auto result = weft_renderer_encode(slot.renderer, prepared, &pass, &use); result != WEFT_RENDER_OK) {
                    why = "encode failed: " + describe(result);
                    return false;
                }
                unity_->NotifyResourceState(target, D3D12_RESOURCE_STATE_RENDER_TARGET, false);
                submit(slot, use);
                return true;
            }

            std::uint64_t mark() override {
                return unity_->GetNextFrameFenceValue();
            }

            std::uint64_t finished() override {
                ID3D12Fence* fence = unity_->GetFrameFence();
                return fence ? fence->GetCompletedValue() : 0;
            }

            void idle(const Slot& slot) override {
                ID3D12Fence* fence = unity_->GetFrameFence();
                if (!fence || slot.pending.empty()) {
                    return;
                }
                const std::uint64_t last = slot.pending.back().second;
                if (fence->GetCompletedValue() >= last) {
                    return;
                }
                if (HANDLE event = CreateEventW(nullptr, FALSE, FALSE, nullptr)) {
                    if (SUCCEEDED(fence->SetEventOnCompletion(last, event))) {
                        WaitForSingleObject(event, 2000);
                    }
                    CloseHandle(event);
                }
            }

        private:
            IUnityGraphicsD3D12v8* unity_;
        };
    } // namespace

    std::unique_ptr<RenderDevice> make_d3d12_device(IUnityInterfaces* unity, int event_id) {
        auto* graphics = unity->Get<IUnityGraphicsD3D12v8>();
        if (!graphics || !graphics->GetDevice()) {
            return nullptr;
        }
        // The draw event records into Unity's command list on the render thread; it changes the list's bindings.
        const UnityD3D12PluginEventConfig config{kUnityD3D12GraphicsQueueAccess_DontCare,
                                                 kUnityD3D12EventConfigFlag_SyncWorkerThreads |
                                                     kUnityD3D12EventConfigFlag_ModifiesCommandBuffersState,
                                                 false};
        graphics->ConfigureEvent(event_id, &config);
        return std::make_unique<D3D12Device>(graphics);
    }
} // namespace weft_unity
