#include "WifiEvidenceCore.h"
#include <stddef.h>
#include <string.h>

static int name_is_valid(const char *name) {
    if (!name) return 0;
    for (size_t i = 0; i <= 63; ++i) {
        const unsigned char c = (unsigned char)name[i];
        if (c == 0) return i > 0 && i <= 63;
        if (i == 63 || !((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') ||
                         (c >= 'a' && c <= 'z') || c == '-' || c == '_' || c == '.')) return 0;
    }
    return 0;
}

static int distinct_valid_names(const char *const *names, int32_t count) {
    if (!names || count <= 0 || count > NIAN_WIFI_MAX_INTERFACES) return 0;
    for (int32_t i = 0; i < count; ++i) {
        if (!name_is_valid(names[i])) return 0;
        for (int32_t j = 0; j < i; ++j) if (strcmp(names[i], names[j]) == 0) return 0;
    }
    return 1;
}

int32_t nian_wifi_encode_v1(const char *const *core, int32_t core_count,
                            const char *const *configured, int32_t configured_count,
                            uint8_t *out, int32_t capacity, int32_t *written) {
    if (out && capacity > 0 && capacity <= NIAN_WIFI_MAX_BUFFER) memset(out, 0, (size_t)capacity);
    if (!written) return NIAN_WIFI_INVALID_ARGUMENT;
    *written = 0;
    if (!out || capacity <= 0 || capacity > NIAN_WIFI_MAX_BUFFER) return NIAN_WIFI_INVALID_ARGUMENT;
    if (!distinct_valid_names(core, core_count) || !distinct_valid_names(configured, configured_count) ||
        core_count != configured_count) return NIAN_WIFI_INVALID_EVIDENCE;
    for (int32_t i = 0; i < core_count; ++i) {
        int found = 0;
        for (int32_t j = 0; j < configured_count; ++j) if (strcmp(core[i], configured[j]) == 0) { found = 1; break; }
        if (!found) return NIAN_WIFI_INVALID_EVIDENCE;
    }
    const char *ordered[NIAN_WIFI_MAX_INTERFACES];
    for (int32_t i = 0; i < core_count; ++i) {
        int32_t j = i;
        while (j > 0 && strcmp(ordered[j - 1], core[i]) > 0) { ordered[j] = ordered[j - 1]; --j; }
        ordered[j] = core[i];
    }
    uint8_t payload[NIAN_WIFI_MAX_BUFFER];
    size_t used = 0;
    const char prefix[] = "{\"version\":1,\"wifi_candidates\":[";
    const char suffix[] = "]}";
#define APPEND(ptr, count) do { size_t n_ = (count); if (n_ > sizeof(payload) - used) return NIAN_WIFI_BUFFER_TOO_SMALL; memcpy(payload + used, (ptr), n_); used += n_; } while (0)
    APPEND(prefix, sizeof(prefix) - 1);
    for (int32_t i = 0; i < core_count; ++i) {
        if (i != 0) APPEND(",", 1);
        APPEND("\"", 1);
        APPEND(ordered[i], strlen(ordered[i]));
        APPEND("\"", 1);
    }
    APPEND(suffix, sizeof(suffix) - 1);
#undef APPEND
    if (used > (size_t)capacity) return NIAN_WIFI_BUFFER_TOO_SMALL;
    memcpy(out, payload, used);
    *written = (int32_t)used;
    return NIAN_WIFI_OK;
}
