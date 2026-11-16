import Foundation
import CoreFoundation
import Darwin
import CoreWLAN
import SystemConfiguration

@main
enum WifiProbeMain {
    static func main() {
        guard CommandLine.arguments == [CommandLine.arguments[0], "--run"] else {
            FileHandle.standardError.write(Data("Explicit --run required.\n".utf8))
            exit(2)
        }
        do {
            guard let interfaces = CWWiFiClient.shared().interfaces(), interfaces.count <= 128,
                  let systemInterfaces = SCNetworkInterfaceCopyAll() as? [SCNetworkInterface],
                  systemInterfaces.count <= 1024 else { throw WifiProbeFailure.invalidEvidence }
            var wifiNames = [String]()
            for interface in interfaces {
                guard let name = interface.interfaceName else { throw WifiProbeFailure.invalidEvidence }
                wifiNames.append(name)
            }
            var configuredNames = [String]()
            for interface in systemInterfaces {
                guard let type = SCNetworkInterfaceGetInterfaceType(interface) else { throw WifiProbeFailure.invalidEvidence }
                guard CFEqual(type, kSCNetworkInterfaceTypeIEEE80211) else { continue }
                guard let name = SCNetworkInterfaceGetBSDName(interface) else { throw WifiProbeFailure.invalidEvidence }
                configuredNames.append(name as String)
            }
            let names = try WifiProbeRules.matchingNames(core: wifiNames, configured: configuredNames)
            let data = try JSONSerialization.data(withJSONObject: ["version": 1, "wifi_candidates": names], options: [.sortedKeys])
            FileHandle.standardOutput.write(data)
            FileHandle.standardOutput.write(Data([10]))
        } catch {
            FileHandle.standardError.write(Data("System Wi-Fi interface query failed.\n".utf8))
            exit(4)
        }
    }
}
