import Foundation

enum WifiProbeFailure: Error { case invalidEvidence }

/// These are untrusted system observations, not proof of physical identity.
enum WifiProbeRules {
    static func matchingNames(core: [String]?, configured: [String]?) throws -> [String] {
        guard let core, let configured,
              !core.isEmpty, !configured.isEmpty,
              core.count <= 128, configured.count <= 128 else {
            throw WifiProbeFailure.invalidEvidence
        }
        func validated(_ names: [String]) throws -> Set<String> {
            var result = Set<String>()
            for name in names {
                let bytes = Array(name.utf8)
                guard !bytes.isEmpty, bytes.count <= 63,
                      bytes.allSatisfy({ (48...57).contains($0) || (65...90).contains($0) ||
                                         (97...122).contains($0) || $0 == 45 || $0 == 46 || $0 == 95 }),
                      result.insert(name).inserted else {
                    throw WifiProbeFailure.invalidEvidence
                }
            }
            return result
        }
        let coreNames = try validated(core)
        let configuredNames = try validated(configured)
        guard coreNames == configuredNames else { throw WifiProbeFailure.invalidEvidence }
        return coreNames.sorted()
    }
}
