// pool.hpp - a persistent worker pool for one reader.
//
// A decode runs about nine parallel passes, each on every core. Creating and joining a pool of
// threads per pass costs a few hundred microseconds each time, several milliseconds per case
// on a line PC at a 100 ms target. One reader owns one pool for its lifetime; a pass hands the
// same callable to every worker (with the worker's index, for per-thread resources such as the
// enhancer nets) and waits for all of them. One decode runs at a time per reader, as the C API
// requires, so one job at a time is all the pool needs.
#pragma once

#include <condition_variable>
#include <functional>
#include <mutex>
#include <thread>
#include <vector>

namespace cr {

class WorkerPool {
public:
    explicit WorkerPool(int workers)
    {
        if (workers < 1) workers = 1;
        for (int i = 0; i < workers; ++i) threads_.emplace_back([this, i] { loop(i); });
    }
    ~WorkerPool()
    {
        {
            std::lock_guard<std::mutex> lock(mu_);
            quit_ = true;
            ++generation_;
        }
        cv_.notify_all();
        for (auto& t : threads_) t.join();
    }
    WorkerPool(const WorkerPool&) = delete;
    WorkerPool& operator=(const WorkerPool&) = delete;

    int size() const { return static_cast<int>(threads_.size()); }

    // fn(index) on `count` workers (at most size()), the calling thread waiting for all of them
    void runAll(int count, const std::function<void(int)>& fn)
    {
        count = count < 1 ? 1 : count > size() ? size() : count;
        {
            std::lock_guard<std::mutex> lock(mu_);
            job_ = &fn;
            jobCount_ = count;
            pending_ = count;
            ++generation_;
        }
        cv_.notify_all();
        std::unique_lock<std::mutex> lock(mu_);
        done_.wait(lock, [&] { return pending_ == 0; });
        job_ = nullptr;
    }

private:
    void loop(int index)
    {
        unsigned long seen = 0;
        for (;;) {
            const std::function<void(int)>* job = nullptr;
            {
                std::unique_lock<std::mutex> lock(mu_);
                cv_.wait(lock, [&] { return quit_ || generation_ != seen; });
                if (quit_) return;
                seen = generation_;
                if (index >= jobCount_) continue; // this job uses fewer workers
                job = job_;
            }
            if (job) (*job)(index);
            {
                std::lock_guard<std::mutex> lock(mu_);
                if (--pending_ == 0) done_.notify_all();
            }
        }
    }

    std::vector<std::thread> threads_;
    std::mutex mu_;
    std::condition_variable cv_, done_;
    const std::function<void(int)>* job_ = nullptr;
    int jobCount_ = 0, pending_ = 0;
    unsigned long generation_ = 0;
    bool quit_ = false;
};

} // namespace cr
