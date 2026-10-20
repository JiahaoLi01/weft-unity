#pragma once

#include "weft/weft.h"

#include <cstdint>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

namespace weft_unity {
    // A texture of Unity's that the runtime's pages show by name (background-image: -weft-texture(name)).
    struct TextureBinding {
        std::string name;
        void* texture = nullptr; // Texture.GetNativeTexturePtr: an ID3D11Texture2D, an ID3D12Resource or a VkImage*
        std::uint32_t width = 0;
        std::uint32_t height = 0;
        std::uint64_t serial = 0; // new for each binding, so a texture bound again is imported again
    };

    // The names Unity's main thread binds (weft_unity_set_texture) for its render thread's draws to show, as weft names
    // a page's textures: letters, digits, '-' and '_', at most 64 bytes; 32 at once, as weft's backends import.
    class TextureTable {
    public:
        static constexpr std::size_t capacity = 32;

        static TextureTable& get();

        // Binds the name to the texture, or unbinds it (texture null).
        weft_result set(std::string_view name, void* texture, std::uint32_t width, std::uint32_t height);
        void clear();
        std::vector<TextureBinding> snapshot() const;

    private:
        mutable std::mutex lock_;
        std::vector<TextureBinding> bound_;
        std::uint64_t next_ = 1;
    };
} // namespace weft_unity
