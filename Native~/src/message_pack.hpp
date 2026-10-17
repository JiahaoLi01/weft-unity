#pragma once

#include <cstddef>
#include <string>

namespace weft_unity {
    // A value weft wrote as MessagePack (its messages' payloads: nil, booleans, integers, floats, UTF-8 strings, bytes,
    // arrays and maps; docs/engine-communication.md 6.1) as JSON text: integers stay whole, floats print the shortest
    // text that reads back the same (NaN and infinities as null), bytes as Base64 strings, a map's keys as strings.
    // Nothing (no payload) and bytes that are no MessagePack value read as "null".
    std::string message_pack_to_json(const void* data, std::size_t size);
} // namespace weft_unity
