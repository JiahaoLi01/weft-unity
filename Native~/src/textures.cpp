#include "textures.hpp"

#include "weft_unity.h"

#include <algorithm>

namespace weft_unity {
    namespace {
        bool valid_name(std::string_view name) {
            constexpr std::size_t longest = 64;
            return !name.empty() && name.size() <= longest && std::all_of(name.begin(), name.end(), [](char c) {
                return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
            });
        }
    } // namespace

    TextureTable& TextureTable::get() {
        static TextureTable* table = new TextureTable();
        return *table;
    }

    weft_result TextureTable::set(std::string_view name, void* texture, std::uint32_t width, std::uint32_t height) {
        if (!valid_name(name) || (texture && (!width || !height))) {
            return WEFT_INVALID_ARGUMENT;
        }
        std::lock_guard lock(lock_);
        const auto it = std::find_if(bound_.begin(), bound_.end(), [&](const TextureBinding& b) { return b.name == name; });
        if (!texture) {
            if (it != bound_.end()) {
                bound_.erase(it);
            }
            return WEFT_OK;
        }
        if (it != bound_.end()) {
            if (it->texture != texture || it->width != width || it->height != height) {
                *it = {it->name, texture, width, height, next_++};
            }
            return WEFT_OK;
        }
        if (bound_.size() >= capacity) {
            return WEFT_CAPACITY_EXCEEDED;
        }
        bound_.push_back({std::string(name), texture, width, height, next_++});
        return WEFT_OK;
    }

    void TextureTable::clear() {
        std::lock_guard lock(lock_);
        bound_.clear();
    }

    std::vector<TextureBinding> TextureTable::snapshot() const {
        std::lock_guard lock(lock_);
        return bound_;
    }
} // namespace weft_unity

extern "C" weft_result weft_unity_set_texture(const char* name, size_t name_size, void* texture, uint32_t width, uint32_t height) {
    if (!name) {
        return WEFT_INVALID_ARGUMENT;
    }
    return weft_unity::TextureTable::get().set({name, name_size}, texture, width, height);
}
