#include "corert.h"
#include <cstdio>
#include <cstring>
#include <vector>

// Always on: the engine builds Release (NDEBUG), where CHECK() checks nothing.
static int g_failures = 0;
#define CHECK(cond) do { if (!(cond)) { printf("  FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond); ++g_failures; } } while (0)

static void test_opaque_dispatch() {
    printf("[corert] Running test_opaque_dispatch...\n");
    uint32_t resp = 0;
    int rc = sys_dispatch(SYS_OP_PING, nullptr, 0, &resp, sizeof(resp));
    CHECK(rc == 0);
    CHECK(resp == 0xAA55AA55);

    uint32_t ver = 0;
    rc = sys_dispatch(SYS_OP_GET_VERSION, nullptr, 0, &ver, sizeof(ver));
    CHECK(rc == 0);
    CHECK(ver == 0x02010000);

    // Bad opcode
    rc = sys_dispatch(0x9999, nullptr, 0, nullptr, 0);
    CHECK(rc == SYS_ERR_UNKNOWN_OP);

    printf("  -> PASS\n");
}

// Req 17.10 AUD-09: native asset encryption is retired; the exports and opcodes must fail and
// hand back no buffer, so nothing can rely on the old SHA-256 XOR envelope.
static void test_asset_crypto_retired() {
    printf("[corert] Running test_asset_crypto_retired...\n");
    const uint8_t data[8] = {1, 2, 3, 4, 5, 6, 7, 8};
    const uint8_t key[32] = {0x01, 0x23, 0x45, 0x67};

    uint8_t* blob = (uint8_t*)0x1;
    size_t blob_len = 99;
    int rc = sys_encrypt_asset(data, sizeof(data), key, sizeof(key), &blob, &blob_len);
    CHECK(rc == SYS_ERR_RETIRED);
    CHECK(blob == nullptr && blob_len == 0);

    uint8_t* plain = (uint8_t*)0x1;
    size_t plain_len = 99;
    rc = sys_decrypt_asset(data, sizeof(data), key, sizeof(key), &plain, &plain_len);
    CHECK(rc == SYS_ERR_RETIRED);
    CHECK(plain == nullptr && plain_len == 0);

    uint8_t out[64] = {0};
    CHECK(sys_dispatch(SYS_OP_ENCRYPT_ASSET, data, sizeof(data), out, sizeof(out)) == SYS_ERR_RETIRED);
    CHECK(sys_dispatch(SYS_OP_DECRYPT_ASSET, data, sizeof(data), out, sizeof(out)) == SYS_ERR_RETIRED);
    CHECK(sys_dispatch(SYS_OP_ENCRYPT_ASSET, nullptr, 0, nullptr, 0) == SYS_ERR_RETIRED);
    printf("  -> PASS\n");
}

int main() {
    printf("=========================================\n");
    printf("Running corert Opaque Runtime Unit Tests\n");
    printf("=========================================\n");
    test_opaque_dispatch();
    test_asset_crypto_retired();
    if (g_failures) {
        printf("corert unit tests FAILED: %d check(s)\n", g_failures);
        return 1;
    }
    printf("All corert unit tests PASSED successfully!\n");
    return 0;
}
