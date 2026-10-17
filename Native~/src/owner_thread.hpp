#pragma once

#include <condition_variable>
#include <cstddef>
#include <functional>
#include <mutex>
#include <thread>

namespace weft_unity {
    // The thread weft's runtime and Views belong to: a thread of its own with a large stack (weft's React and D3 pages
    // need 8 MiB; Unity's main thread has 1). Call runs work there, one call at a time, and returns once it has run;
    // a call from the owner thread itself runs at once.
    class OwnerThread {
    public:
        static constexpr std::size_t stack_bytes = std::size_t(16) << 20;

        OwnerThread();
        ~OwnerThread();
        OwnerThread(const OwnerThread&) = delete;
        OwnerThread& operator=(const OwnerThread&) = delete;

        void call(const std::function<void()>& work);
        bool on_owner() const;

    private:
        static unsigned long __stdcall entry(void* self);
        void run();

        void* handle_ = nullptr;
        unsigned long id_ = 0;
        std::mutex calls_;
        std::mutex lock_;
        std::condition_variable wake_;
        std::condition_variable done_;
        const std::function<void()>* pending_ = nullptr;
        bool finished_ = false;
        bool stopping_ = false;
    };
} // namespace weft_unity
