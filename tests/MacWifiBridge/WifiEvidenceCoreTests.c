#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include "WifiEvidenceCore.h"

#define CHECK(expr) do { if (!(expr)) { fprintf(stderr, "check failed at line %d\n", __LINE__); return 1; } } while (0)

static int cleared(const uint8_t *bytes, int32_t n) {
    for (int32_t i = 0; i < n; ++i) if (bytes[i] != 0) return 0;
    return 1;
}

int main(void) {
    uint8_t out[256];
    int32_t written = -1;
    const char *core[] = { "en9", "en1" };
    const char *configured[] = { "en1", "en9" };
    const char expected[] = "{\"version\":1,\"wifi_candidates\":[\"en1\",\"en9\"]}";

    memset(out, 0xa5, sizeof(out));
    CHECK(nian_wifi_encode_v1(core, 2, configured, 2, out, sizeof(out), &written) == 0);
    CHECK(written == (int32_t)strlen(expected));
    CHECK(memcmp(out, expected, (size_t)written) == 0);
    CHECK(out[written] == 0);

    const char *duplicate[] = { "en1", "en1" };
    const char *different[] = { "en2" };
    const char *invalid[] = { "../en1" };
    const char *nonascii[] = { "\xc3\xa9" };
    char longName[65]; memset(longName, 'a', 64); longName[64] = 0;
    const char *tooLong[] = { longName };
    const char *single[] = { "en1" };
    const char **badLists[] = { duplicate, different, invalid, nonascii, tooLong };
    int badCounts[] = { 2, 1, 1, 1, 1 };
    for (int i = 0; i < 5; ++i) {
        memset(out, 0xa5, sizeof(out)); written = 99;
        CHECK(nian_wifi_encode_v1(badLists[i], badCounts[i], single, 1, out, sizeof(out), &written) != 0);
        CHECK(written == 0 && cleared(out, sizeof(out)));
    }
    memset(out, 0xa5, sizeof(out)); written = 99;
    CHECK(nian_wifi_encode_v1(NULL, 0, single, 1, out, sizeof(out), &written) != 0);
    CHECK(written == 0 && cleared(out, sizeof(out)));
    memset(out, 0xa5, sizeof(out)); written = 99;
    CHECK(nian_wifi_encode_v1(single, 129, single, 1, out, sizeof(out), &written) != 0);
    CHECK(written == 0 && cleared(out, sizeof(out)));

    memset(out, 0xa5, sizeof(out)); written = 99;
    CHECK(nian_wifi_encode_v1(single, 1, single, 1, out, 8, &written) != 0);
    CHECK(written == 0 && cleared(out, 8) && out[8] == 0xa5);
    written = 99;
    CHECK(nian_wifi_encode_v1(single, 1, single, 1, NULL, 8, &written) != 0 && written == 0);
    CHECK(nian_wifi_encode_v1(single, 1, single, 1, out, -1, &written) != 0 && written == 0);
    memset(out, 0xa5, sizeof(out));
    CHECK(nian_wifi_encode_v1(single, 1, single, 1, out, sizeof(out), NULL) != 0);
    CHECK(cleared(out, sizeof(out)));
    puts("Wi-Fi evidence core tests passed.");
    return 0;
}
