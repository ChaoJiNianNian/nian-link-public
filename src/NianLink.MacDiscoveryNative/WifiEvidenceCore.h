#ifndef NIAN_WIFI_EVIDENCE_CORE_H
#define NIAN_WIFI_EVIDENCE_CORE_H

#include <stdint.h>

enum {
    NIAN_WIFI_OK = 0,
    NIAN_WIFI_INVALID_ARGUMENT = 1,
    NIAN_WIFI_INVALID_EVIDENCE = 2,
    NIAN_WIFI_BUFFER_TOO_SMALL = 3,
    NIAN_WIFI_MAX_BUFFER = 16384,
    NIAN_WIFI_MAX_INTERFACES = 128
};

/* Internal pure encoder. All input names are borrowed, NUL-terminated strings. */
__attribute__((visibility("hidden"))) int32_t nian_wifi_encode_v1(const char *const *core, int32_t core_count,
                            const char *const *configured, int32_t configured_count,
                            uint8_t *out, int32_t capacity, int32_t *written);

#endif
