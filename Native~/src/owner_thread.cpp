#include "owner_thread.hpp"

#include <windows.h>

namespace weft_unity {
    OwnerThread::OwnerThread() {
        handle_ = CreateThread(nullptr, stack_bytes, &OwnerThread::entry, this, STACK_SIZE_PARAM_IS_A_RESERVATION, &id_);
    }

    OwnerThread::~OwnerThread() {
        {
            std::lock_guard lock(lock_);
            stopping_ = true;
        }
        wake_.notify_all();
        if (handle_) {
            WaitForSingleObject(handle_, INFINITE);
            CloseHandle(handle_);
        }
    }

    bool OwnerThread::on_owner() const {
        return GetCurrentThreadId() == id_;
    }

    void OwnerThread::call(const std::function<void()>& work) {
        if (on_owner() || !handle_) {
            work();
            return;
        }
        // One caller at a time hands its work over and waits for it.
        std::lock_guard caller(calls_);
        std::unique_lock lock(lock_);
        pending_ = &work;
        finished_ = false;
        wake_.notify_all();
        done_.wait(lock, [this] { return finished_; });
    }

    unsigned long __stdcall OwnerThread::entry(void* self) {
        static_cast<OwnerThread*>(self)->run();
        return 0;
    }

    void OwnerThread::run() {
        std::unique_lock lock(lock_);
        for (;;) {
            wake_.wait(lock, [this] { return pending_ || stopping_; });
            if (pending_) {
                const auto* work = pending_;
                pending_ = nullptr;
                lock.unlock();
                try {
                    (*work)();
                } catch (...) {
                    // The plugin's work reports through its results; nothing escapes to the thread.
                }
                lock.lock();
                finished_ = true;
                done_.notify_all();
                continue;
            }
            if (stopping_) {
                return;
            }
        }
    }
} // namespace weft_unity
