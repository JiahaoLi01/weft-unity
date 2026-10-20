#include "render_device.hpp"

#include <vulkan/vulkan.h>

#include "IUnityGraphics.h"
#include "IUnityGraphicsVulkan.h"
#include "IUnityInterface.h"
#include "weft/render_backend_vulkan.h"

#include <atomic>

namespace weft_unity {
    namespace {
        // weft's name of a texture's format as it samples it; false for one it does not.
        bool texture_format(VkFormat format, std::uint32_t& weft) {
            switch (format) {
            case VK_FORMAT_R16G16B16A16_SFLOAT:
                weft = WEFT_RENDER_TARGET_LINEAR_RGBA16F;
                return true;
            case VK_FORMAT_R8G8B8A8_UNORM:
                weft = WEFT_RENDER_TARGET_LINEAR_RGBA8;
                return true;
            case VK_FORMAT_R32G32B32A32_SFLOAT:
                weft = WEFT_RENDER_TARGET_LINEAR_RGBA32F;
                return true;
            default:
                return false;
            }
        }

        // An engine texture's image view, which weft samples through until it retires the texture.
        struct SampledView {
            VkImageView view = VK_NULL_HANDLE;
            std::atomic<bool> retired{false};
        };

        void on_retired(void* user, std::uint32_t) noexcept {
            static_cast<SampledView*>(user)->retired.store(true);
        }

        // Vulkan: the pass records into the command buffer Unity is recording, outside a render pass; the recording
        // is done with the GPU once Unity's safe frame number reaches the frame it was recorded in. The image view the
        // pass draws through is the plugin's, made for the draw and destroyed when its frame is safe; an engine
        // texture's view is made when it is imported and destroyed once weft retires it.
        class VulkanDevice final : public RenderDevice {
        public:
            explicit VulkanDevice(IUnityGraphicsVulkanV2* unity) : unity_(unity), instance_(unity->Instance()) {
                auto get_device = reinterpret_cast<PFN_vkGetDeviceProcAddr>(
                    instance_.getInstanceProcAddr(instance_.instance, "vkGetDeviceProcAddr"));
                if (get_device) {
                    create_view_ = reinterpret_cast<PFN_vkCreateImageView>(get_device(instance_.device, "vkCreateImageView"));
                    destroy_view_ = reinterpret_cast<PFN_vkDestroyImageView>(get_device(instance_.device, "vkDestroyImageView"));
                }
            }

            ~VulkanDevice() override {
                // Unity's device goes: its work is done.
                for (const auto& [view, frame] : views_) {
                    destroy_view_(instance_.device, view, nullptr);
                }
                for (SampledView* sampled : released_) {
                    destroy_view_(instance_.device, sampled->view, nullptr);
                    delete sampled;
                }
            }

        protected:
            bool open(Slot& slot, weft_render_backend& table, std::string& why) override {
                if (!create_view_ || !destroy_view_) {
                    why = "no vkCreateImageView on Unity's device";
                    return false;
                }
                const weft_render_vulkan_desc desc{sizeof(desc),
                                                   WEFT_RENDER_ABI_VERSION,
                                                   std::uint64_t(reinterpret_cast<std::uintptr_t>(instance_.instance)),
                                                   std::uint64_t(reinterpret_cast<std::uintptr_t>(instance_.physicalDevice)),
                                                   std::uint64_t(reinterpret_cast<std::uintptr_t>(instance_.device)),
                                                   std::uint64_t(reinterpret_cast<std::uintptr_t>(instance_.graphicsQueue)),
                                                   instance_.queueFamilyIndex,
                                                   0,
                                                   reinterpret_cast<void*>(instance_.getInstanceProcAddr),
                                                   queue};
                weft_render_vulkan* backend = nullptr;
                if (const auto result = weft_render_vulkan_create(&desc, &backend, &table); result != WEFT_RENDER_OK) {
                    why = "no Vulkan backend: " + describe(result);
                    return false;
                }
                slot.backend = backend;
                return true;
            }

            void close(Slot& slot) override {
                if (slot.backend) {
                    weft_render_vulkan_destroy(static_cast<weft_render_vulkan*>(slot.backend));
                    slot.backend = nullptr;
                }
            }

            bool external(Slot& slot, weft_render_external_backend& table, std::string& why) override {
                if (const auto result = weft_render_vulkan_external(static_cast<weft_render_vulkan*>(slot.backend), &table);
                    result != WEFT_RENDER_OK) {
                    why = "the Vulkan backend has no external textures: " + describe(result);
                    return false;
                }
                return true;
            }

            // A view of the texture's image (in the layout shaders read, by Unity's barrier), kept until weft retires it.
            bool import_texture(Slot& slot, const TextureBinding& binding, Import& added, std::string& why) override {
                UnityVulkanImage image{};
                if (!access(binding, image)) {
                    why = "Unity gives no access to it";
                    return false;
                }
                std::uint32_t weft = 0;
                if (!texture_format(image.format, weft)) {
                    why = "its format (" + std::to_string(int(image.format)) + ") is not linear RGBA16F, RGBA8 or RGBA32F";
                    return false;
                }
                if (image.samples != VK_SAMPLE_COUNT_1_BIT || !(image.usage & VK_IMAGE_USAGE_SAMPLED_BIT) ||
                    image.extent.width != binding.width || image.extent.height != binding.height) {
                    why = "it is not a single-sample image of its size that shaders sample";
                    return false;
                }
                VkImageViewCreateInfo info{VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO};
                info.image = image.image;
                info.viewType = VK_IMAGE_VIEW_TYPE_2D;
                info.format = image.format;
                info.subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
                auto* sampled = new SampledView();
                if (create_view_(instance_.device, &info, nullptr, &sampled->view) != VK_SUCCESS) {
                    delete sampled;
                    why = "no image view of it";
                    return false;
                }
                const weft_render_vulkan_texture native{sizeof(native), WEFT_RENDER_EXTERNAL_ABI_VERSION,
                                                        std::uint64_t(reinterpret_cast<std::uintptr_t>(instance_.device)),
                                                        std::uint64_t(reinterpret_cast<std::uintptr_t>(sampled->view)),
                                                        std::uint64_t(reinterpret_cast<std::uintptr_t>(image.image))};
                weft_render_external_texture description = live_texture(slot, binding, weft, &native, sizeof(native));
                description.retirement_user_data = sampled;
                description.retired = &on_retired;
                if (const auto result = weft_renderer_import_texture(slot.renderer, &description, &added.lease); result != WEFT_RENDER_OK) {
                    destroy_view_(instance_.device, sampled->view, nullptr);
                    delete sampled;
                    why = "import_texture failed: " + describe(result);
                    return false;
                }
                added.native = sampled;
                return true;
            }

            // A camera left it a colour attachment: Unity's barrier makes its writes visible to the pass's fragment reads.
            bool sample_texture(const TextureBinding& binding, std::string& why) override {
                UnityVulkanImage image{};
                if (!access(binding, image)) {
                    why = "Unity gives no access to it";
                    return false;
                }
                return true;
            }

            void forget_texture(Import& released) override {
                if (released.native) {
                    released_.push_back(static_cast<SampledView*>(released.native));
                    released.native = nullptr;
                }
            }

            bool encode(Slot& slot, weft_render_handle prepared, const Draw& draw, std::string& why) override {
                // The target as a colour attachment (Unity's barrier), then the command buffer (which that access may
                // have changed).
                UnityVulkanImage image{};
                if (!unity_->AccessTexture(draw.target, UnityVulkanWholeImage, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
                                           VK_PIPELINE_STAGE_COLOR_ATTACHMENT_OUTPUT_BIT,
                                           VK_ACCESS_COLOR_ATTACHMENT_READ_BIT | VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT,
                                           kUnityVulkanResourceAccess_PipelineBarrier, &image)) {
                    why = "Unity gives no access to the target";
                    return false;
                }
                if (image.format != VK_FORMAT_B8G8R8A8_UNORM || image.samples != VK_SAMPLE_COUNT_1_BIT ||
                    !(image.usage & VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT) || image.extent.width < draw.width ||
                    image.extent.height < draw.height) {
                    why = "the target is not a single-sample BGRA8 UNORM render texture of the view's size";
                    return false;
                }
                UnityVulkanRecordingState state{};
                if (!unity_->CommandRecordingState(&state, kUnityVulkanGraphicsQueueAccess_DontCare) || !state.commandBuffer) {
                    why = "Unity records no command buffer";
                    return false;
                }
                frame_ = state.currentFrameNumber;
                VkImageViewCreateInfo info{VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO};
                info.image = image.image;
                info.viewType = VK_IMAGE_VIEW_TYPE_2D;
                info.format = VK_FORMAT_B8G8R8A8_UNORM;
                info.subresourceRange = {VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1};
                VkImageView view = VK_NULL_HANDLE;
                if (create_view_(instance_.device, &info, nullptr, &view) != VK_SUCCESS) {
                    why = "no image view of the target";
                    return false;
                }
                views_.emplace_back(view, frame_);
                const std::uint64_t source =
                    image.usage & VK_IMAGE_USAGE_TRANSFER_SRC_BIT ? std::uint64_t(reinterpret_cast<std::uintptr_t>(image.image)) : 0;
                const weft_render_vulkan_pass native{sizeof(native),
                                                     WEFT_RENDER_ABI_VERSION,
                                                     std::uint64_t(reinterpret_cast<std::uintptr_t>(instance_.device)),
                                                     std::uint64_t(reinterpret_cast<std::uintptr_t>(state.commandBuffer)),
                                                     std::uint64_t(reinterpret_cast<std::uintptr_t>(view)),
                                                     draw.clear ? 1u : 0u,
                                                     0,
                                                     {0, 0, 0, 0},
                                                     source};
                const weft_render_pass pass{sizeof(pass), WEFT_RENDER_ABI_VERSION, slot.identity, draw.width, draw.height,
                                            WEFT_RENDER_TARGET_LINEAR_BGRA8, 1, &native, sizeof(native)};
                weft_render_handle use{};
                if (const auto result = weft_renderer_encode(slot.renderer, prepared, &pass, &use); result != WEFT_RENDER_OK) {
                    why = "encode failed: " + describe(result, slot.renderer);
                    return false;
                }
                submit(slot, use);
                return true;
            }

            std::uint64_t mark() override {
                return frame_;
            }

            std::uint64_t finished() override {
                UnityVulkanRecordingState state{};
                if (!unity_->CommandRecordingState(&state, kUnityVulkanGraphicsQueueAccess_DontCare)) {
                    return 0;
                }
                // The views of safe frames go with them, and those of the engine textures weft has retired.
                std::erase_if(views_, [&](const std::pair<VkImageView, std::uint64_t>& kept) {
                    if (kept.second > state.safeFrameNumber) {
                        return false;
                    }
                    destroy_view_(instance_.device, kept.first, nullptr);
                    return true;
                });
                std::erase_if(released_, [&](SampledView* sampled) {
                    if (!sampled->retired.load()) {
                        return false;
                    }
                    destroy_view_(instance_.device, sampled->view, nullptr);
                    delete sampled;
                    return true;
                });
                return state.safeFrameNumber;
            }

        private:
            // The texture in the layout fragment shaders read, by Unity's barrier in the command buffer it records.
            bool access(const TextureBinding& binding, UnityVulkanImage& image) {
                return unity_->AccessTexture(binding.texture, UnityVulkanWholeImage, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
                                             VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT, VK_ACCESS_SHADER_READ_BIT,
                                             kUnityVulkanResourceAccess_PipelineBarrier, &image);
            }

            IUnityGraphicsVulkanV2* unity_;
            UnityVulkanInstance instance_;
            PFN_vkCreateImageView create_view_ = nullptr;
            PFN_vkDestroyImageView destroy_view_ = nullptr;
            std::uint64_t frame_ = 0;
            std::vector<std::pair<VkImageView, std::uint64_t>> views_;
            std::vector<SampledView*> released_; // engine textures' views, until weft retires them
        };
    } // namespace

    std::unique_ptr<RenderDevice> make_vulkan_device(IUnityInterfaces* unity, int event_id) {
        auto* graphics = unity->Get<IUnityGraphicsVulkanV2>();
        if (!graphics || !graphics->Instance().device) {
            return nullptr;
        }
        // The draw event records outside a render pass (the backend begins its own) into Unity's command buffer.
        const UnityVulkanPluginEventConfig config{kUnityVulkanRenderPass_EnsureOutside, kUnityVulkanGraphicsQueueAccess_DontCare,
                                                  kUnityVulkanEventConfigFlag_EnsurePreviousFrameSubmission |
                                                      kUnityVulkanEventConfigFlag_ModifiesCommandBuffersState};
        graphics->ConfigureEvent(event_id, &config);
        return std::make_unique<VulkanDevice>(graphics);
    }
} // namespace weft_unity
