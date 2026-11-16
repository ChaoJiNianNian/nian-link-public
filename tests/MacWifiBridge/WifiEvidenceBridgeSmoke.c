#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include "WifiEvidenceBridge.h"

/* Explicit opt-in: invokes only the evidence bridge, never a network socket. */
int main(int argc, char **argv) {
    if (argc != 2 || strcmp(argv[1], "--run") != 0) {
        fputs("Explicit --run required.\n", stderr);
        return 2;
    }
    uint8_t output[16384];
    int32_t written = 99;
    memset(output, 0xa5, sizeof(output));
    if (nian_wifi_evidence_v1(output, 0, &written) == 0 || written != 0 || output[0] != 0xa5) {
        fputs("Invalid argument handling failed.\n", stderr);
        return 3;
    }
    if (nian_wifi_evidence_v1(output, sizeof(output), NULL) == 0) {
        fputs("Null length pointer was accepted.\n", stderr);
        return 3;
    }
    for (size_t i = 0; i < sizeof(output); ++i) if (output[i] != 0) return 3;
    int32_t status = nian_wifi_evidence_v1(output, sizeof(output), &written);
    if (status != 0) {
        if (written != 0) return 4;
        for (size_t i = 0; i < sizeof(output); ++i) if (output[i] != 0) return 4;
        fputs("System Wi-Fi evidence unavailable.\n", stderr);
        return 4;
    }
    if (written <= 0 || written > (int32_t)sizeof(output)) return 5;
    if (fwrite(output, 1, (size_t)written, stdout) != (size_t)written) return 6;
    if (fputc('\n', stdout) == EOF || fflush(stdout) == EOF) return 6;
    return 0;
}
