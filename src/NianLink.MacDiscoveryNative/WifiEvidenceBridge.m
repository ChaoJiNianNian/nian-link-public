#import <Foundation/Foundation.h>
#import <CoreWLAN/CoreWLAN.h>
#import <SystemConfiguration/SystemConfiguration.h>
#include <string.h>
#include "WifiEvidenceBridge.h"
#include "WifiEvidenceCore.h"

static int nian_wifi_copy_name(NSString *name, char out[64]) {
    if (![name isKindOfClass:[NSString class]] || name.length == 0 || name.length > 63) return 0;
    NSUInteger length = name.length;
    for (NSUInteger i = 0; i < length; ++i) {
        unichar c = [name characterAtIndex:i];
        if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') ||
              (c >= 'a' && c <= 'z') || c == '-' || c == '_' || c == '.')) return 0;
        out[i] = (char)c;
    }
    out[length] = '\0';
    return 1;
}

int32_t nian_wifi_evidence_v1(uint8_t *out, int32_t capacity, int32_t *written) {
    if (out && capacity > 0 && capacity <= NIAN_WIFI_MAX_BUFFER) memset(out, 0, (size_t)capacity);
    if (!written) return NIAN_WIFI_INVALID_ARGUMENT;
    *written = 0;
    if (!out || capacity <= 0 || capacity > NIAN_WIFI_MAX_BUFFER) return NIAN_WIFI_INVALID_ARGUMENT;
    @autoreleasepool {
        @try {
            NSArray<CWInterface *> *interfaces = [[CWWiFiClient sharedWiFiClient] interfaces];
            if (!interfaces || interfaces.count == 0 || interfaces.count > NIAN_WIFI_MAX_INTERFACES)
                return NIAN_WIFI_INVALID_EVIDENCE;
            __attribute__((objc_precise_lifetime)) NSMutableArray<NSString *> *coreNames = [NSMutableArray arrayWithCapacity:interfaces.count];
            for (CWInterface *interface in interfaces) {
                NSString *name = interface.interfaceName;
                if (![name isKindOfClass:[NSString class]]) return NIAN_WIFI_INVALID_EVIDENCE;
                [coreNames addObject:[name copy]];
            }
            CFArrayRef raw = SCNetworkInterfaceCopyAll();
            if (!raw) return NIAN_WIFI_INVALID_EVIDENCE;
            NSArray *systemInterfaces = CFBridgingRelease(raw);
            if (systemInterfaces.count > 1024) return NIAN_WIFI_INVALID_EVIDENCE;
            __attribute__((objc_precise_lifetime)) NSMutableArray<NSString *> *configuredNames = [NSMutableArray array];
            for (id object in systemInterfaces) {
                if (CFGetTypeID((__bridge CFTypeRef)object) != SCNetworkInterfaceGetTypeID())
                    return NIAN_WIFI_INVALID_EVIDENCE;
                SCNetworkInterfaceRef interface = (__bridge SCNetworkInterfaceRef)object;
                CFStringRef type = SCNetworkInterfaceGetInterfaceType(interface);
                if (!type) return NIAN_WIFI_INVALID_EVIDENCE;
                if (!CFEqual(type, kSCNetworkInterfaceTypeIEEE80211)) continue;
                CFStringRef name = SCNetworkInterfaceGetBSDName(interface);
                if (!name || CFGetTypeID(name) != CFStringGetTypeID() || configuredNames.count >= NIAN_WIFI_MAX_INTERFACES)
                    return NIAN_WIFI_INVALID_EVIDENCE;
                [configuredNames addObject:[(__bridge NSString *)name copy]];
            }
            const char *core[NIAN_WIFI_MAX_INTERFACES];
            const char *configured[NIAN_WIFI_MAX_INTERFACES];
            char coreStorage[NIAN_WIFI_MAX_INTERFACES][64];
            char configuredStorage[NIAN_WIFI_MAX_INTERFACES][64];
            for (NSUInteger i = 0; i < coreNames.count; ++i) {
                if (!nian_wifi_copy_name(coreNames[i], coreStorage[i])) return NIAN_WIFI_INVALID_EVIDENCE;
                core[i] = coreStorage[i];
            }
            for (NSUInteger i = 0; i < configuredNames.count; ++i) {
                if (!nian_wifi_copy_name(configuredNames[i], configuredStorage[i])) return NIAN_WIFI_INVALID_EVIDENCE;
                configured[i] = configuredStorage[i];
            }
            return nian_wifi_encode_v1(core, (int32_t)coreNames.count, configured, (int32_t)configuredNames.count,
                                       out, capacity, written);
        } @catch (__unused NSException *exception) {
            memset(out, 0, (size_t)capacity);
            *written = 0;
            return NIAN_WIFI_INVALID_EVIDENCE;
        }
    }
}
