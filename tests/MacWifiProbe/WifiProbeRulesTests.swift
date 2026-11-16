import Foundation

@main
enum WifiProbeRulesTests {
    static func main() throws {
        func reject(_ core: [String]?, _ configured: [String]?) throws {
            do {
                _ = try WifiProbeRules.matchingNames(core: core, configured: configured)
                throw NSError(domain: "Expected rejection", code: 1)
            } catch WifiProbeFailure.invalidEvidence { }
        }
        let result = try WifiProbeRules.matchingNames(core: ["en9", "en1"], configured: ["en1", "en9"])
        guard result == ["en1", "en9"] else { throw NSError(domain: "Unexpected order", code: 1) }
        try reject(nil, ["en1"])
        try reject(["en1"], nil)
        try reject([], ["en1"])
        try reject(["en1"], [])
        try reject(["en1", "en1"], ["en1"])
        try reject(["en1"], ["en1", "en1"])
        try reject(["en1"], ["en2"])
        try reject(["en1", "en2"], ["en1"])
        try reject([""], [""])
        try reject(["../en1"], ["../en1"])
        try reject(["é"], ["é"])
        try reject([String(repeating: "x", count: 64)], ["en1"])
        try reject(Array(repeating: "en1", count: 129), ["en1"])
        print("Wi-Fi evidence rule checks passed.")
    }
}
