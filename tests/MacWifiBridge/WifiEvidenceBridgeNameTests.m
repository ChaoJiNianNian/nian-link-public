/* Includes the bridge to exercise its private string validator without exporting it. */
#include "WifiEvidenceBridge.m"
#include <stdio.h>

int main(void) {
    @autoreleasepool {
        const char embedded[] = { 'e', 'n', '1', 0, 'x' };
        NSString *bad = [[NSString alloc] initWithBytes:embedded length:sizeof(embedded)
                                               encoding:NSUTF8StringEncoding];
        char out[64];
        if (!nian_wifi_copy_name(@"en1", out) || strcmp(out, "en1") != 0 ||
            nian_wifi_copy_name(bad, out) || nian_wifi_copy_name(@"é", out) ||
            nian_wifi_copy_name(@"", out) || nian_wifi_copy_name(@"../en1", out)) return 1;
        NSMutableString *mutable = [NSMutableString stringWithString:@"en1"];
        if (!nian_wifi_copy_name(mutable, out)) return 1;
        [mutable setString:@"changed"];
        if (strcmp(out, "en1") != 0) return 1;
        NSMutableString *longName = [NSMutableString string];
        for (int i = 0; i < 63; ++i) [longName appendString:@"a"];
        if (!nian_wifi_copy_name(longName, out) || strlen(out) != 63) return 1;
        [longName appendString:@"a"];
        if (nian_wifi_copy_name(longName, out)) return 1;
        puts("Wi-Fi evidence string tests passed.");
    }
    return 0;
}
