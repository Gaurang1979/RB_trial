// fuzz_common.hpp - shared parts of the engine's C API fuzzers (engine/tests/fuzz/).
//
// Each fuzzer is one function, fuzzOne(Input&), that turns bytes into a sequence of C API calls:
// odd images (0x0, 1xN, huge, short or odd strides, noise, all black or white, drawn codes and
// text), malformed settings (struct_size, NaN and infinite floats, extreme integers) and mutated
// text blobs (learned state, fonts, print references, patterns). Built with -fsanitize=address,
// undefined (scripts/fuzz-engine.sh), every crash, leak, overflow or undefined behaviour stops the
// run with the case's seed printed.
//
// Two drivers:
//   - the built-in one (default): random byte buffers from a seed for a given time;
//       fuzz_x --seconds 600 --seed 1      run for 10 minutes
//       fuzz_x --replay 12345              rerun the one case with this seed (from a crash report)
//   - libFuzzer (clang with -fsanitize=fuzzer, not Apple's clang): build with -DFUZZ_LIBFUZZER and
//     the same fuzzOne is the LLVMFuzzerTestOneInput.
//
// A case that runs longer than its watchdog (FUZZ_SLOW_SECONDS, default 60 s) is reported as a hang:
// on the line a call that never returns stops the line as surely as a crash.
#pragma once

#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <exception>
#include <limits>
#include <string>
#include <thread>
#include <vector>

// the seed is printed from the sanitizer's death callback, when built with one
#if defined(__has_feature)
#  if __has_feature(address_sanitizer) || __has_feature(undefined_behavior_sanitizer)
#    include <sanitizer/common_interface_defs.h>
#    define FUZZ_HAVE_SANITIZER_IFACE 1
#  endif
#endif
#if !defined(FUZZ_HAVE_SANITIZER_IFACE) && defined(__SANITIZE_ADDRESS__)
#  include <sanitizer/common_interface_defs.h>
#  define FUZZ_HAVE_SANITIZER_IFACE 1
#endif

namespace fuzz {

// Bytes in, values out; past the end every value is 0 (a short input is still a valid case).
class Input {
public:
    Input(const uint8_t* data, size_t size) : d_(data), n_(size) {}

    uint8_t byte() { return pos_ < n_ ? d_[pos_++] : 0; }
    uint32_t u32()
    {
        uint32_t v = 0;
        for (int i = 0; i < 4; ++i) v = (v << 8) | byte();
        return v;
    }
    int32_t i32() { return static_cast<int32_t>(u32()); }
    bool chance(int percent) { return byte() % 100 < percent; }
    int range(int lo, int hi) { return hi <= lo ? lo : lo + static_cast<int>(u32() % static_cast<uint32_t>(hi - lo + 1)); }
    template <typename T, size_t N>
    T pick(const T (&a)[N]) { return a[byte() % N]; }
    size_t left() const { return pos_ < n_ ? n_ - pos_ : 0; }

    // an integer that breaks things: limits, signs, off-by-one, or anything
    int32_t nastyInt()
    {
        static const int32_t v[] = {0, 1, -1, 2, 3, 4, 7, 8, 16, 31, 32, 63, 64, 100, 255, 256, 1000, 4096, 65535, 65536,
                                    1 << 20, 1 << 30, std::numeric_limits<int32_t>::max(), std::numeric_limits<int32_t>::max() - 1,
                                    std::numeric_limits<int32_t>::min(), std::numeric_limits<int32_t>::min() + 1, -2, -100, -65536};
        return chance(70) ? pick(v) : i32();
    }
    // a float that breaks things: NaN, infinities, denormals, huge, negative, or anything
    float nastyFloat()
    {
        static const float v[] = {std::numeric_limits<float>::quiet_NaN(), -std::numeric_limits<float>::quiet_NaN(),
                                  std::numeric_limits<float>::infinity(), -std::numeric_limits<float>::infinity(),
                                  std::numeric_limits<float>::denorm_min(), std::numeric_limits<float>::max(),
                                  std::numeric_limits<float>::lowest(), 0.f, -0.f, 1.f, -1.f, 0.5f, 1e-30f, 1e30f, 2e9f,
                                  -2e9f, 100.f, 180.f, 360.f, 1e6f};
        if (chance(70)) return pick(v);
        const uint32_t bits = u32();
        float f;
        std::memcpy(&f, &bits, sizeof f);
        return f;
    }
    // an int field: usually a sensible value near `sane`, sometimes a nasty one
    int32_t field(int32_t sane, int nastyPercent = 25) { return chance(nastyPercent) ? nastyInt() : sane; }
    float fieldf(float sane, int nastyPercent = 25) { return chance(nastyPercent) ? nastyFloat() : sane; }

    // a string: random bytes (invalid UTF-8, control characters, embedded NUL cut it short) up to max
    std::string text(size_t max)
    {
        const size_t len = static_cast<size_t>(range(0, static_cast<int>(max)));
        std::string s;
        for (size_t i = 0; i < len && left(); ++i) {
            const uint8_t b = byte();
            if (b == 0) break;
            s.push_back(static_cast<char>(b));
        }
        return s;
    }

    // mutate a text blob: flip bytes, cut, duplicate a part, put a number in the middle, insert junk
    std::string mutate(std::string s)
    {
        const int rounds = range(1, 6);
        for (int k = 0; k < rounds; ++k) {
            const size_t at = s.empty() ? 0 : u32() % (s.size() + 1);
            switch (byte() % 8) {
            case 0: if (!s.empty() && at < s.size()) s[at] = static_cast<char>(byte() | 1); break;
            case 1: s.resize(at); break;
            case 2: if (at < s.size()) s.insert(at, s.substr(at, static_cast<size_t>(range(1, 64)))); break;
            case 3: {
                static const char* nums[] = {"-1", "0", "4294967295", "4294967296", "-2147483648", "2147483647", "1e999",
                                             "nan", "inf", "-inf", "99999999999999999999", "0x7fffffff", "1e-999", "-0"};
                s.insert(at, std::string(" ") + nums[byte() % (sizeof nums / sizeof *nums)] + " ");
                break;
            }
            case 4: s.insert(at, text(32)); break;
            case 5: s.insert(at, "\n"); break;
            case 6: { // replace a number by a nasty one
                size_t b = at;
                while (b < s.size() && !(s[b] >= '0' && s[b] <= '9')) ++b;
                size_t e = b;
                while (e < s.size() && ((s[e] >= '0' && s[e] <= '9') || s[e] == '.')) ++e;
                if (b < s.size()) s.replace(b, e - b, std::to_string(nastyInt()));
                break;
            }
            default: if (at < s.size()) s.erase(at, static_cast<size_t>(range(1, 16))); break;
            }
        }
        return s;
    }

private:
    const uint8_t* d_;
    size_t n_;
    size_t pos_ = 0;
};

// An 8-bit grey image as a caller hands it over: its buffer holds exactly what width, height and
// stride promise (stride * (height - 1) + width bytes), so a read past it is an ASan error.
struct Image {
    std::vector<uint8_t> buf;
    int32_t width = 0, height = 0, stride = 0;
    bool valid = false; // the sizes are good and buf holds the image (else only drawn on never)
    const uint8_t* data() const { return buf.empty() ? nullptr : buf.data(); }
    uint8_t& at(int x, int y) { return buf[static_cast<size_t>(y) * static_cast<size_t>(stride) + static_cast<size_t>(x)]; }
};

// Size of the image the arguments promise, or 0 when they are invalid (the API must refuse them).
inline size_t promisedBytes(int32_t w, int32_t h, int32_t stride)
{
    if (w <= 0 || h <= 0 || stride < w) return 0;
    return static_cast<size_t>(stride) * static_cast<size_t>(h - 1) + static_cast<size_t>(w);
}

// Sizes: 0x0 and negative (refused), 1xN and Nx1, odd, small, and now and then a huge frame.
inline void imageSize(Input& in, int32_t& w, int32_t& h, int32_t& stride, int maxSide)
{
    static const int32_t sides[] = {1, 2, 3, 7, 8, 15, 16, 17, 31, 33, 64, 100, 127, 255, 256, 320, 480, 640};
    switch (in.byte() % 10) {
    case 0: w = in.nastyInt() % 4; h = in.nastyInt() % 4; break; // 0, negative, tiny
    case 1: w = 1; h = in.range(1, 2000); break;
    case 2: w = in.range(1, 2000); h = 1; break;
    case 3: w = in.range(1, maxSide); h = in.range(1, maxSide); break;
    case 4: w = in.chance(50) ? 5472 : 4000; h = in.chance(50) ? 3648 : 3000; // 20 MP and 12 MP frames
        if (w > maxSide * 8) { w = maxSide * 2; h = maxSide * 2; }
        break;
    default: w = in.pick(sides); h = in.pick(sides); break;
    }
    switch (in.byte() % 8) {
    case 0: stride = w + in.range(1, 64); break;
    case 1: stride = in.chance(50) ? w - 1 : in.nastyInt(); break; // short or anything: must be refused unless >= w
    case 2: stride = (w + 63) / 64 * 64; break;
    default: stride = w; break;
    }
}

// Pixel contents: flat black, white or grey, noise, sparse specks, stripes, blocks, gradients.
inline void fillImage(Input& in, Image& img)
{
    const int mode = in.byte() % 9;
    const uint8_t a = in.byte(), b = in.byte();
    const int period = in.range(1, 24);
    uint32_t rng = in.u32() | 1u;
    if (std::getenv("FUZZ_DUMP"))
        std::fprintf(stderr, "fill: %d x %d stride %d, mode %d, a %d b %d period %d seed %u\n", img.width, img.height, img.stride, mode, a,
                     b, period, rng);
    auto rnd = [&rng]() { rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; return rng; };
    for (int y = 0; y < img.height; ++y)
        for (int x = 0; x < img.width; ++x) {
            uint8_t v = 0;
            switch (mode) {
            case 0: v = 0; break;
            case 1: v = 255; break;
            case 2: v = a; break;
            case 3: v = static_cast<uint8_t>(rnd()); break;
            case 4: v = (rnd() % 97 == 0) ? 0 : 255; break;
            case 5: v = ((x / period) & 1) ? a : b; break;
            case 6: v = (((x / period) + (y / period)) & 1) ? a : b; break;
            case 7: v = static_cast<uint8_t>((x * 255) / std::max(1, img.width - 1)); break;
            default: v = ((rnd() & 3) == 0) ? a : b; break;
            }
            img.at(x, y) = v;
        }
}

// A whole image from the input: its size, stride and contents (the buffer is empty when the
// arguments are invalid; the call is still made with them, and must be refused).
inline Image makeImage(Input& in, int maxSide)
{
    Image img;
    imageSize(in, img.width, img.height, img.stride, maxSide);
    size_t bytes = promisedBytes(img.width, img.height, img.stride);
    if (bytes > (size_t(256) << 20)) { // no caller holds a buffer that large: keep the size, drop the padding
        img.stride = img.width;
        bytes = promisedBytes(img.width, img.height, img.stride);
    }
    if (bytes == 0) {
        if (in.chance(50)) img.buf.assign(16, 0x80); // a real pointer with bad sizes
        return img;
    }
    img.buf.resize(bytes);
    img.valid = true;
    fillImage(in, img);
    return img;
}

// A replayed case's images as PGM files in FUZZ_DUMP (a folder), so a found image can become a test.
inline void dumpImage(const Image& img, const char* what)
{
    const char* dir = std::getenv("FUZZ_DUMP");
    if (!dir || !img.valid) return;
    static int n = 0;
    const std::string path = std::string(dir) + "/" + std::to_string(n++) + "-" + what + ".pgm";
    if (FILE* f = std::fopen(path.c_str(), "wb")) {
        std::fprintf(f, "P5\n%d %d\n255\n", img.width, img.height);
        for (int y = 0; y < img.height; ++y)
            std::fwrite(img.buf.data() + static_cast<size_t>(y) * static_cast<size_t>(img.stride), 1, static_cast<size_t>(img.width), f);
        std::fclose(f);
        std::fprintf(stderr, "dumped %s\n", path.c_str());
    }
}

// ---- the built-in driver ----

struct Watch {
    std::atomic<uint64_t> seed{0};
    std::atomic<int64_t> startedMs{-1};
};
inline Watch& watch()
{
    static Watch w;
    return w;
}

inline int64_t nowMs()
{
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

// the case running now, also in a file (FUZZ_CURRENT): a process the system kills (memory) prints nothing
inline const char*& currentFile()
{
    static const char* path = nullptr;
    return path;
}
inline void noteCase(uint64_t seed)
{
    if (!currentFile()) return;
    if (FILE* f = std::fopen(currentFile(), "w")) {
        std::fprintf(f, "%llu\n", static_cast<unsigned long long>(seed));
        std::fclose(f);
    }
}

inline void printSeedOnDeath()
{
    std::fprintf(stderr, "\n== fuzz case seed %llu (replay: --replay %llu)\n", static_cast<unsigned long long>(watch().seed.load()),
                 static_cast<unsigned long long>(watch().seed.load()));
}

// bytes of one case from its seed: lengths vary so short inputs (defaults everywhere) are common too
inline std::vector<uint8_t> caseBytes(uint64_t seed)
{
    uint64_t s = seed * 0x9E3779B97F4A7C15ull + 0x632BE59BD9B4E019ull;
    auto next = [&s]() { s ^= s >> 12; s ^= s << 25; s ^= s >> 27; return s * 0x2545F4914F6CDD1Dull; };
    const size_t n = static_cast<size_t>(next() % 4) == 0 ? next() % 64 : 256 + next() % 3840;
    std::vector<uint8_t> b(n);
    for (auto& v : b) v = static_cast<uint8_t>(next() >> 56);
    return b;
}

} // namespace fuzz

int fuzzOne(fuzz::Input& in);

#ifdef FUZZ_LIBFUZZER
extern "C" int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size)
{
    fuzz::Input in(data, size);
    fuzzOne(in);
    return 0;
}
#else
int main(int argc, char** argv)
{
    double seconds = 60;
    uint64_t seed = static_cast<uint64_t>(std::chrono::system_clock::now().time_since_epoch().count());
    long long replay = -1;
    long long maxCases = -1;
    for (int i = 1; i + 1 < argc; i += 2) {
        if (!std::strcmp(argv[i], "--seconds")) seconds = std::atof(argv[i + 1]);
        else if (!std::strcmp(argv[i], "--seed")) seed = std::strtoull(argv[i + 1], nullptr, 10);
        else if (!std::strcmp(argv[i], "--replay")) replay = std::atoll(argv[i + 1]);
        else if (!std::strcmp(argv[i], "--cases")) maxCases = std::atoll(argv[i + 1]);
    }
#ifdef FUZZ_HAVE_SANITIZER_IFACE
    __sanitizer_set_death_callback(fuzz::printSeedOnDeath);
#endif
    // an exception out of the C API ends in std::terminate: report it with the seed too
    std::set_terminate([] {
        if (auto e = std::current_exception()) {
            try { std::rethrow_exception(e); }
            catch (const std::exception& x) { std::fprintf(stderr, "\n== UNCAUGHT EXCEPTION: %s\n", x.what()); }
            catch (...) { std::fprintf(stderr, "\n== UNCAUGHT EXCEPTION\n"); }
        }
        fuzz::printSeedOnDeath();
        std::abort();
    });
    fuzz::currentFile() = std::getenv("FUZZ_CURRENT");
    const char* slowEnv = std::getenv("FUZZ_SLOW_SECONDS");
    const int64_t slowMs = static_cast<int64_t>((slowEnv ? std::atof(slowEnv) : 60.0) * 1000);
    // the watchdog: a case that never returns is reported with its seed, then the run stops
    std::thread([slowMs] {
        for (;;) {
            std::this_thread::sleep_for(std::chrono::milliseconds(500));
            const int64_t t = fuzz::watch().startedMs.load();
            if (t >= 0 && fuzz::nowMs() - t > slowMs) {
                std::fprintf(stderr, "\n== HANG: case ran over %lld ms\n", static_cast<long long>(slowMs));
                fuzz::printSeedOnDeath();
                std::_Exit(3);
            }
        }
    }).detach();

    if (replay >= 0) {
        const auto bytes = fuzz::caseBytes(static_cast<uint64_t>(replay));
        fuzz::watch().seed = static_cast<uint64_t>(replay);
        fuzz::watch().startedMs = fuzz::nowMs();
        fuzz::Input in(bytes.data(), bytes.size());
        fuzzOne(in);
        std::printf("case %lld ran clean\n", replay);
        return 0;
    }
    const int64_t end = fuzz::nowMs() + static_cast<int64_t>(seconds * 1000);
    long long cases = 0;
    int64_t lastReport = fuzz::nowMs();
    std::printf("fuzz: seed base %llu, %.0f s\n", static_cast<unsigned long long>(seed), seconds);
    std::fflush(stdout);
    while (fuzz::nowMs() < end && (maxCases < 0 || cases < maxCases)) {
        const uint64_t s = seed + static_cast<uint64_t>(cases);
        const auto bytes = fuzz::caseBytes(s);
        fuzz::watch().seed = s;
        fuzz::noteCase(s);
        fuzz::watch().startedMs = fuzz::nowMs();
        fuzz::Input in(bytes.data(), bytes.size());
        fuzzOne(in);
        fuzz::watch().startedMs = -1;
        ++cases;
        if (fuzz::nowMs() - lastReport > 30000) {
            std::printf("fuzz: %lld cases\n", cases);
            std::fflush(stdout);
            lastReport = fuzz::nowMs();
        }
    }
    std::printf("fuzz: %lld cases, no crash, leak or undefined behaviour (seeds %llu..%llu)\n", cases,
                static_cast<unsigned long long>(seed), static_cast<unsigned long long>(seed + static_cast<uint64_t>(cases) - 1));
    return 0;
}
#endif
