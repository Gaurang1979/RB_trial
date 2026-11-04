#include "corert.h"
#include <vector>
#include <cstring>
#include <cstdlib>
#include <cstdint>

// Asset encryption is not done natively (Req 17.10 AUD-09). The one asset envelope in the
// product is AES-256-GCM in .NET (BarcodeReading.Protection.ContentCipher, wrapped by
// VisionCore.Security.SecureAssetVault); the engine links no AES-GCM library, and the former
// SHA-256 XOR stream here was not AES-GCM and used a repeating salt. The exports stay so a host
// built against the old header still links, but they always fail and hand back nothing.

int sys_encrypt_asset(const uint8_t* in_plain,
                      size_t in_len,
                      const uint8_t* key,
                      size_t key_len,
                      uint8_t** out_blob,
                      size_t* out_len) {
    (void)in_plain; (void)in_len; (void)key; (void)key_len;
    if (out_blob) *out_blob = nullptr;
    if (out_len) *out_len = 0;
    return SYS_ERR_RETIRED;
}

int sys_decrypt_asset(const uint8_t* in_blob,
                      size_t in_len,
                      const uint8_t* key,
                      size_t key_len,
                      uint8_t** out_data,
                      size_t* out_len) {
    (void)in_blob; (void)in_len; (void)key; (void)key_len;
    if (out_data) *out_data = nullptr;
    if (out_len) *out_len = 0;
    return SYS_ERR_RETIRED;
}

void sys_free(void* ptr) {
    if (ptr) {
        std::free(ptr);
    }
}

int sys_dispatch(uint32_t op_code,
                 const void* in_params,
                 size_t in_size,
                 void* out_results,
                 size_t out_size) {
    (void)in_params; (void)in_size;
    switch (op_code) {
        case SYS_OP_PING: {
            if (out_results && out_size >= sizeof(uint32_t)) {
                uint32_t resp = 0xAA55AA55;
                std::memcpy(out_results, &resp, sizeof(uint32_t));
            }
            return 0;
        }
        case SYS_OP_GET_VERSION: {
            if (out_results && out_size >= sizeof(uint32_t)) {
                uint32_t ver = 0x02010000; // v2.1.0
                std::memcpy(out_results, &ver, sizeof(uint32_t));
            }
            return 0;
        }
        case SYS_OP_ENCRYPT_ASSET:
        case SYS_OP_DECRYPT_ASSET:
            return SYS_ERR_RETIRED; // Req 17.10 AUD-09: retired, see sys_encrypt_asset
        default:
            return SYS_ERR_UNKNOWN_OP;
    }
}
