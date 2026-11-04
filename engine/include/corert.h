#ifndef CORERT_H
#define CORERT_H

#include <stdint.h>
#include <stddef.h>

#ifdef _WIN32
#  ifdef CORERT_BUILD
#    define CORERT_API __declspec(dllexport)
#  else
#    define CORERT_API __declspec(dllimport)
#  endif
#else
#  define CORERT_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Opaque System Operation Codes (Competitor-Proof Numeric Dispatches).
 * Symbol tables, binary exports, and strings do not disclose proprietary algorithm names.
 */
#define SYS_OP_PING                 0x0001
#define SYS_OP_GET_VERSION          0x0002
#define SYS_OP_LOCATE               0x1001
#define SYS_OP_MEASURE              0x1002
#define SYS_OP_BLOB                 0x1003
#define SYS_OP_HAYSTACK_PROJECT     0x2001
#define SYS_OP_HAYSTACK_TRIAGE      0x2002
#define SYS_OP_THERMAL_INSPECT      0x3001
#define SYS_OP_POLAR_DECOUPLE       0x3002
#define SYS_OP_PHOTOMETRIC_STEREO   0x3003
#define SYS_OP_DEFORM_MATCH         0x3004
#define SYS_OP_ENCRYPT_ASSET        0x4001 /* retired: returns SYS_ERR_RETIRED */
#define SYS_OP_DECRYPT_ASSET        0x4002 /* retired: returns SYS_ERR_RETIRED */

/* Error codes. */
#define SYS_ERR_UNKNOWN_OP          (-100)
#define SYS_ERR_RETIRED             (-101)

/*
 * Single Unified Opaque Dispatcher.
 * Returns 0 on success, negative error code on failure.
 */
CORERT_API int sys_dispatch(uint32_t op_code,
                            const void* in_params,
                            size_t in_size,
                            void* out_results,
                            size_t out_size);

/*
 * Retired (Req 17.10 AUD-09). Asset encryption is AES-256-GCM in .NET only
 * (VisionCore.Security.SecureAssetVault over BarcodeReading.Protection.ContentCipher).
 * Both always return SYS_ERR_RETIRED and set *out_data / *out_blob to NULL and *out_len to 0.
 */
CORERT_API int sys_decrypt_asset(const uint8_t* in_blob,
                                 size_t in_len,
                                 const uint8_t* key,
                                 size_t key_len,
                                 uint8_t** out_data,
                                 size_t* out_len);

CORERT_API int sys_encrypt_asset(const uint8_t* in_plain,
                                 size_t in_len,
                                 const uint8_t* key,
                                 size_t key_len,
                                 uint8_t** out_blob,
                                 size_t* out_len);

CORERT_API void sys_free(void* ptr);

#ifdef __cplusplus
}
#endif

#endif /* CORERT_H */
