#include "message_pack.hpp"

#include <charconv>
#include <cmath>
#include <cstdint>
#include <cstring>

namespace weft_unity {
    namespace {
        constexpr int max_depth = 64;

        class Reader {
        public:
            Reader(const std::uint8_t* at, const std::uint8_t* end) : at_(at), end_(end) {
            }

            bool bad() const {
                return bad_;
            }

            // One value appended to out as JSON.
            void value(std::string& out, int depth) {
                if (depth > max_depth || !need(1)) {
                    fail(out);
                    return;
                }
                const std::uint8_t tag = *at_++;
                if (tag <= 0x7f) {
                    integer(out, tag);
                } else if (tag >= 0xe0) {
                    integer(out, std::int8_t(tag));
                } else if ((tag & 0xf0) == 0x80) {
                    map(out, tag & 0x0f, depth);
                } else if ((tag & 0xf0) == 0x90) {
                    array(out, tag & 0x0f, depth);
                } else if ((tag & 0xe0) == 0xa0) {
                    text(out, tag & 0x1f);
                } else {
                    switch (tag) {
                        case 0xc0:
                            out += "null";
                            break;
                        case 0xc2:
                            out += "false";
                            break;
                        case 0xc3:
                            out += "true";
                            break;
                        case 0xc4:
                            bytes(out, unsigned_bytes(1));
                            break;
                        case 0xc5:
                            bytes(out, unsigned_bytes(2));
                            break;
                        case 0xc6:
                            bytes(out, unsigned_bytes(4));
                            break;
                        case 0xca: {
                            const auto bits = std::uint32_t(unsigned_bytes(4));
                            float number;
                            std::memcpy(&number, &bits, sizeof(number));
                            real(out, number);
                            break;
                        }
                        case 0xcb: {
                            const auto bits = unsigned_bytes(8);
                            double number;
                            std::memcpy(&number, &bits, sizeof(number));
                            real(out, number);
                            break;
                        }
                        case 0xcc:
                            whole(out, unsigned_bytes(1));
                            break;
                        case 0xcd:
                            whole(out, unsigned_bytes(2));
                            break;
                        case 0xce:
                            whole(out, unsigned_bytes(4));
                            break;
                        case 0xcf:
                            whole(out, unsigned_bytes(8));
                            break;
                        case 0xd0:
                            integer(out, std::int8_t(unsigned_bytes(1)));
                            break;
                        case 0xd1:
                            integer(out, std::int16_t(unsigned_bytes(2)));
                            break;
                        case 0xd2:
                            integer(out, std::int32_t(unsigned_bytes(4)));
                            break;
                        case 0xd3:
                            integer(out, std::int64_t(unsigned_bytes(8)));
                            break;
                        case 0xd9:
                            text(out, unsigned_bytes(1));
                            break;
                        case 0xda:
                            text(out, unsigned_bytes(2));
                            break;
                        case 0xdb:
                            text(out, unsigned_bytes(4));
                            break;
                        case 0xdc:
                            array(out, unsigned_bytes(2), depth);
                            break;
                        case 0xdd:
                            array(out, unsigned_bytes(4), depth);
                            break;
                        case 0xde:
                            map(out, unsigned_bytes(2), depth);
                            break;
                        case 0xdf:
                            map(out, unsigned_bytes(4), depth);
                            break;
                        default:
                            fail(out);
                            break;
                    }
                }
            }

        private:
            bool need(std::uint64_t count) {
                if (std::uint64_t(end_ - at_) < count) {
                    bad_ = true;
                }
                return !bad_;
            }

            void fail(std::string& out) {
                bad_ = true;
                out += "null";
            }

            std::uint64_t unsigned_bytes(int count) {
                std::uint64_t value = 0;
                if (need(std::uint64_t(count))) {
                    for (int i = 0; i < count; ++i) {
                        value = (value << 8) | *at_++;
                    }
                }
                return value;
            }

            static void integer(std::string& out, std::int64_t value) {
                char buffer[32];
                const auto end = std::to_chars(buffer, buffer + sizeof(buffer), value).ptr;
                out.append(buffer, end);
            }

            static void whole(std::string& out, std::uint64_t value) {
                char buffer[32];
                const auto end = std::to_chars(buffer, buffer + sizeof(buffer), value).ptr;
                out.append(buffer, end);
            }

            static void real(std::string& out, double value) {
                if (!std::isfinite(value)) {
                    out += "null";
                    return;
                }
                char buffer[64];
                const auto end = std::to_chars(buffer, buffer + sizeof(buffer), value).ptr;
                out.append(buffer, end);
            }

            void text(std::string& out, std::uint64_t size) {
                if (!need(size)) {
                    out += "null";
                    return;
                }
                quote(out, reinterpret_cast<const char*>(at_), std::size_t(size));
                at_ += size;
            }

            static void quote(std::string& out, const char* text, std::size_t size) {
                static const char* hex = "0123456789abcdef";
                out += '"';
                for (std::size_t i = 0; i < size; ++i) {
                    const auto c = static_cast<unsigned char>(text[i]);
                    switch (c) {
                        case '"':
                            out += "\\\"";
                            break;
                        case '\\':
                            out += "\\\\";
                            break;
                        case '\n':
                            out += "\\n";
                            break;
                        case '\r':
                            out += "\\r";
                            break;
                        case '\t':
                            out += "\\t";
                            break;
                        default:
                            if (c < 0x20) {
                                out += "\\u00";
                                out += hex[c >> 4];
                                out += hex[c & 15];
                            } else {
                                out += char(c);
                            }
                    }
                }
                out += '"';
            }

            void bytes(std::string& out, std::uint64_t size) {
                if (!need(size)) {
                    out += "null";
                    return;
                }
                static const char* digits = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
                out += '"';
                for (std::uint64_t i = 0; i < size; i += 3) {
                    const std::uint32_t a = at_[i];
                    const std::uint32_t b = i + 1 < size ? at_[i + 1] : 0;
                    const std::uint32_t c = i + 2 < size ? at_[i + 2] : 0;
                    const std::uint32_t triple = (a << 16) | (b << 8) | c;
                    out += digits[(triple >> 18) & 63];
                    out += digits[(triple >> 12) & 63];
                    out += i + 1 < size ? digits[(triple >> 6) & 63] : '=';
                    out += i + 2 < size ? digits[triple & 63] : '=';
                }
                out += '"';
                at_ += size;
            }

            void array(std::string& out, std::uint64_t count, int depth) {
                out += '[';
                for (std::uint64_t i = 0; i < count && !bad_; ++i) {
                    if (i) {
                        out += ',';
                    }
                    value(out, depth + 1);
                }
                out += ']';
            }

            void map(std::string& out, std::uint64_t count, int depth) {
                out += '{';
                for (std::uint64_t i = 0; i < count && !bad_; ++i) {
                    if (i) {
                        out += ',';
                    }
                    // A key that is no string becomes the string of its JSON.
                    std::string key;
                    value(key, depth + 1);
                    if (key.empty() || key.front() != '"') {
                        std::string quoted;
                        quote(quoted, key.data(), key.size());
                        key = std::move(quoted);
                    }
                    out += key;
                    out += ':';
                    value(out, depth + 1);
                }
                out += '}';
            }

            const std::uint8_t* at_;
            const std::uint8_t* end_;
            bool bad_ = false;
        };
    } // namespace

    std::string message_pack_to_json(const void* data, std::size_t size) {
        if (!data || !size) {
            return "null";
        }
        const auto* bytes = static_cast<const std::uint8_t*>(data);
        Reader reader(bytes, bytes + size);
        std::string out;
        reader.value(out, 0);
        return reader.bad() ? std::string("null") : out;
    }
} // namespace weft_unity
