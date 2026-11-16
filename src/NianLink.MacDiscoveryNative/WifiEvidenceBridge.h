#ifndef NIAN_WIFI_EVIDENCE_BRIDGE_H
#define NIAN_WIFI_EVIDENCE_BRIDGE_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Evidence only, not an authorization or a physical-device attestation.
 * The caller supplies valid, non-overlapping storage. On successful return,
 * *written bytes contain UTF-8 JSON without a trailing NUL. With a non-NULL
 * written pointer, failures set *written to zero; a non-NULL output of valid
 * capacity is cleared. The bridge retains no caller storage after return.
 */
__attribute__((visibility("default")))
int32_t nian_wifi_evidence_v1(uint8_t *out, int32_t capacity, int32_t *written);

#ifdef __cplusplus
}
#endif
#endif
