import Foundation
import AppKit
import ScreenCaptureKit
import CoreGraphics
import ImageIO
import UniformTypeIdentifiers
import CryptoKit

// Must match Nian.Input.Mac.MacDesktopSource exactly. Revision uses native
// logical coordinates and rotation, never requested JPEG size or local DPI.
struct PreviewSourceSnapshot: Equatable {
    let displayID: CGDirectDisplayID
    let revision: String
    let bounds: CGRect
    func geometry(width: Int, height: Int) -> [String: Any] {
        return ["sourceId": "mac-display-\(displayID)", "revision": revision,
                "left": Double(bounds.minX), "top": Double(bounds.minY),
                "width": Double(bounds.width), "height": Double(bounds.height),
                "imageWidth": width, "imageHeight": height]
    }
}

struct Request: Decodable {
    var action: String
    var width: Int?
    var height: Int?
    var windowID: UInt32?
    var text: String?
    var png: String?
    var expectedChangeCount: Int?
}

@main
struct DeskLinkMedia {
    static func main() async {
        if CommandLine.arguments.contains("--realtime-stream") {
            await RealtimeCaptureRunner.run(); return
        }
        if CommandLine.arguments.contains("--self-test") {
            guard RealtimeCaptureSessionPolicy.selfTest() else {
                emit(["status": "error", "message": "实时会话策略自检未通过"]); exit(1)
            }
            guard RealtimeSourceHealth.selfTest() else {
                emit(["status": "error", "message": "实时采集源活性自检未通过"]); exit(1)
            }
            guard RealtimeReturnWindowPolicy.selfTest() else {
                emit(["status": "error", "message": "归还浮窗筛选自检未通过"]); exit(1)
            }
            guard RealtimeFilterRecoveryPolicy.selfTest() else {
                emit(["status": "error", "message": "采集筛选恢复自检未通过"]); exit(1)
            }
            guard previewStatusSelfTest() else {
                emit(["status": "error", "message": "被动画面来源状态自检未通过"]); exit(1)
            }
            guard clipboardWriteSelfTest() else {
                emit(["status": "error", "message": "隔离剪贴板写入自检未通过"]); exit(1)
            }
            emit(["status": "ok", "protocol": 1, "screenCaptureKit": true, "realtimeStream": 1, "realtimeSessionPolicyTests": 8, "realtimeSourceHealthTests": 12, "previewStatusTests": 4, "returnWindowPolicyTests": 25, "filterRecoveryTests": 3]); return
        }
        if CommandLine.arguments.contains("--status") {
            emit(["status": currentPreviewStatus()]); return
        }
        while let line = readLine() {
            if line.utf8.count > 6 * 1024 * 1024 { emit(["status": "error", "message": "请求超过限制"]); continue }
            do {
                let request = try JSONDecoder().decode(Request.self, from: Data(line.utf8))
                emit(await handle(request))
            } catch { emit(["status": "error", "message": "本机媒体请求无效"]); }
        }
    }
    static func emit(_ object: [String: Any]) {
        if let bytes = try? JSONSerialization.data(withJSONObject: object), let text = String(data: bytes, encoding: .utf8) { print(text); fflush(stdout) }
    }
    static func locked() -> Bool {
        let session = CGSessionCopyCurrentDictionary() as? [String: Any]
        return session?["CGSSessionScreenIsLocked"] as? Bool == true || session?["kCGSessionOnConsoleKey"] as? Bool == false
    }
    static func previewStatus(sessionFailure: String?, captureAllowed: Bool) -> String {
        if sessionFailure == "locked" { return "locked" }
        if sessionFailure != nil { return "sourceUnavailable" }
        return captureAllowed ? "ready" : "permissionRequired"
    }
    static func currentPreviewStatus() -> String {
        previewStatus(sessionFailure: RealtimeCaptureSessionPolicy.currentFailure(), captureAllowed: CGPreflightScreenCaptureAccess())
    }
    static func previewStatusSelfTest() -> Bool {
        previewStatus(sessionFailure: nil, captureAllowed: true) == "ready" &&
        previewStatus(sessionFailure: nil, captureAllowed: false) == "permissionRequired" &&
        previewStatus(sessionFailure: "locked", captureAllowed: true) == "locked" &&
        previewStatus(sessionFailure: "source-unavailable", captureAllowed: true) == "sourceUnavailable"
    }
    @MainActor static func handle(_ request: Request) async -> [String: Any] {
        switch request.action {
        case "permission":
            let allowed = CGPreflightScreenCaptureAccess() || CGRequestScreenCaptureAccess()
            return ["status": allowed ? "ready" : "permissionRequired"]
        case "status": return ["status": currentPreviewStatus()]
        case "clipboardRead": return readClipboard()
        case "clipboardWrite": return writeClipboard(request)
        case "capture", "sources", "sourceGeometry": return await screen(request)
        default: return ["status": "error", "message": "未知媒体动作"]
        }
    }
    static func previewSource(_ displayID: CGDirectDisplayID) -> PreviewSourceSnapshot? {
        let mainID = CGMainDisplayID()
        // Controlled captures are of the actual primary source only. A fallback
        // display may remain viewable, but cannot acquire another source's proof.
        guard displayID != 0, displayID == mainID else { return nil }
        var ids = [CGDirectDisplayID](repeating: 0, count: 64)
        var count: UInt32 = 0
        guard CGGetActiveDisplayList(UInt32(ids.count), &ids, &count) == .success,
              count > 0, count <= UInt32(ids.count) else { return nil }
        let active = Array(ids.prefix(Int(count))).sorted()
        guard !active.contains(0), Set(active).count == active.count, active.contains(mainID) else { return nil }
        var layout: [String] = []
        var sourceBounds: CGRect?
        func number(_ value: Double) -> String? {
            guard value.isFinite, abs(value) <= 1_000_000 else { return nil }
            return String(Int64((value * 1_000_000).rounded(.toNearestOrAwayFromZero)))
        }
        for id in active {
            let bounds = CGDisplayBounds(id)
            guard !bounds.isNull, !bounds.isInfinite, bounds.width >= 1, bounds.height >= 1,
                  bounds.width <= 100_000, bounds.height <= 100_000,
                  let x = number(Double(bounds.minX)), let y = number(Double(bounds.minY)),
                  let width = number(Double(bounds.width)), let height = number(Double(bounds.height)),
                  let rotation = number(CGDisplayRotation(id)) else { return nil }
            layout.append("\(id)|\(x),\(y),\(width),\(height),\(rotation)")
            if id == displayID { sourceBounds = bounds }
        }
        guard let bounds = sourceBounds else { return nil }
        var after = [CGDirectDisplayID](repeating: 0, count: 64)
        var afterCount: UInt32 = 0
        guard mainID == CGMainDisplayID(),
              CGGetActiveDisplayList(UInt32(after.count), &after, &afterCount) == .success,
              afterCount == count, active == Array(after.prefix(Int(afterCount))).sorted() else { return nil }
        let canonical = "primary:\(mainID);" + layout.joined(separator: ";")
        let revision = SHA256.hash(data: Data(canonical.utf8)).map { String(format: "%02X", $0) }.joined()
        return PreviewSourceSnapshot(displayID: displayID, revision: revision, bounds: bounds)
    }
    @MainActor static func screen(_ request: Request) async -> [String: Any] {
        guard !locked() else { return ["status": "locked", "message": "Mac已锁屏"] }
        guard CGPreflightScreenCaptureAccess() else { return ["status": "permissionRequired", "message": "请在Mac允许屏幕录制"] }
        do {
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
            let ownBundles: Set<String> = ["com.nian.desktoptoolbox.desklink", "com.nian.desktoptoolbox.desklink.native-candidate", "com.nian.desktoptoolbox.desklink.preview-candidate", "com.niannian.nianlink"]
            let ownApps = content.applications.filter { ownBundles.contains($0.bundleIdentifier) || $0.processID == getppid() || $0.processID == getpid() }
            let windows = content.windows.filter { window in
                window.frame.width >= 100 && window.frame.height >= 80 && window.windowLayer == 0 && window.owningApplication != nil && !ownApps.contains(where: { $0.processID == window.owningApplication?.processID })
            }
            if request.action == "sources" {
                return ["status": "ready", "windows": windows.map { ["id": $0.windowID, "name": ($0.title?.isEmpty == false ? $0.title! : $0.owningApplication!.applicationName)] }]
            }
            if request.action == "sourceGeometry" {
                let width = request.width ?? 1, height = request.height ?? 1
                guard (1...1920).contains(width), (1...1080).contains(height),
                      let display = content.displays.first(where: { $0.displayID == CGMainDisplayID() }),
                      let source = previewSource(display.displayID) else {
                    return ["status": "sourceUnavailable", "message": "无法确认当前主显示器"]
                }
                return ["status": "sourceGeometry", "geometry": source.geometry(width: width, height: height)]
            }
            let filter: SCContentFilter
            let size: CGSize
            var sourceBefore: PreviewSourceSnapshot? = nil
            if let id = request.windowID {
                guard let window = windows.first(where: { $0.windowID == id }) else { return ["status": "sourceUnavailable", "message": "选择的窗口已关闭或不可见"] }
                filter = SCContentFilter(desktopIndependentWindow: window); size = window.frame.size
            } else {
                guard let display = content.displays.first(where: { $0.displayID == CGMainDisplayID() }) ?? content.displays.first else { return ["status": "noDisplay", "message": "Mac未检测到可采集的显示器"] }
                filter = SCContentFilter(display: display, excludingApplications: ownApps, exceptingWindows: [])
                size = CGSize(width: display.width, height: display.height)
                sourceBefore = previewSource(display.displayID)
            }
            let maxWidth = min(1920, max(160, request.width ?? 960))
            let maxHeight = min(1080, max(90, request.height ?? 540))
            let scale = min(Double(maxWidth) / max(1, size.width), Double(maxHeight) / max(1, size.height))
            let width = max(1, Int(size.width * scale))
            let height = max(1, Int(size.height * scale))
            let configuration = SCStreamConfiguration()
            configuration.width = width; configuration.height = height
            configuration.showsCursor = true; configuration.capturesAudio = false
            configuration.scalesToFit = true; configuration.queueDepth = 1
            let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: configuration)
            let data = NSMutableData()
            guard let destination = CGImageDestinationCreateWithData(data, UTType.jpeg.identifier as CFString, 1, nil) else { return ["status": "captureFailed", "message": "无法编码屏幕画面"] }
            CGImageDestinationAddImage(destination, image, [kCGImageDestinationLossyCompressionQuality: 0.65] as CFDictionary)
            guard CGImageDestinationFinalize(destination), data.length <= 1024 * 1024 else { return ["status": "captureFailed", "message": "屏幕画面超过限制"] }
            guard !locked() else { return ["status": "locked", "message": "Mac已锁屏"] }
            var reply: [String: Any] = ["status": "frame", "width": image.width, "height": image.height, "jpeg": (data as Data).base64EncodedString()]
            if let before = sourceBefore {
                guard previewSource(before.displayID) == before else {
                    return ["status": "sourceUnavailable", "message": "显示器布局在采集期间发生变化，请等待新画面"]
                }
                reply["geometry"] = before.geometry(width: image.width, height: image.height)
            }
            return reply
        } catch { return ["status": "captureFailed", "message": "Mac屏幕采集暂不可用，请检查屏幕录制权限与显示器连接"] }
    }
    @MainActor static func readClipboard() -> [String: Any] {
        readClipboard(board: .general)
    }
    @MainActor static func readClipboard(board: NSPasteboard) -> [String: Any] {
        let count = board.changeCount
        if board.types?.contains(.fileURL) == true { return ["status": "clipboard", "changeCount": count] }
        if board.types?.contains(NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType")) == true { return ["status": "clipboard", "changeCount": count, "suppressed": true] }
        // An image may advertise incidental text as well; inspect only format
        // names and never read its pixels or accidental text metadata.
        if board.types?.contains(where: { UTType($0.rawValue)?.conforms(to: .image) == true }) == true {
            return ["status": "clipboard", "changeCount": count]
        }
        if let text = board.string(forType: .string) {
            guard text.utf8.count <= 256 * 1024 else {
                return ["status": "clipboard", "changeCount": count, "oversized": true]
            }
            return ["status": "clipboard", "changeCount": count, "text": text]
        }
        return ["status": "clipboard", "changeCount": count]
    }
    @MainActor static func writeClipboard(_ request: Request) -> [String: Any] {
        writeClipboard(request, board: .general)
    }
    @MainActor static func writeClipboard(_ request: Request, board: NSPasteboard) -> [String: Any] {
        guard request.png == nil else { return ["status": "error", "message": "仅支持同步文字"] }
        if let expected = request.expectedChangeCount, expected != board.changeCount { return ["status": "changed", "changeCount": board.changeCount] }
        if let text = request.text, text.utf8.count <= 256 * 1024 {
            let generation = board.clearContents()
            if let expected = request.expectedChangeCount, expected == Int.max || generation != expected + 1 { return ["status": "changed", "changeCount": board.changeCount] }
            guard board.changeCount == generation else { return ["status": "changed", "changeCount": board.changeCount] }
            guard board.setString(text, forType: .string) else { return ["status": "error", "changeCount": board.changeCount, "message": "剪贴板写入失败"] }
            guard board.changeCount == generation else { return ["status": "changed", "changeCount": board.changeCount] }
            return ["status": "written", "changeCount": generation]
        } else { return ["status": "error", "message": "仅支持同步文字"] }
    }
    @MainActor static func clipboardWriteSelfTest() -> Bool {
        // A distinct named pasteboard: never reads or modifies the user's general clipboard.
        let board = NSPasteboard(name: NSPasteboard.Name("nian-media-selftest-" + UUID().uuidString))
        defer { board.releaseGlobally() }
        let before = board.changeCount
        let good = Request(action: "clipboardWrite", width: nil, height: nil, windowID: nil, text: "synthetic", png: nil, expectedChangeCount: before)
        let written = writeClipboard(good, board: board)
        guard written["status"] as? String == "written", let writtenCount = written["changeCount"] as? Int, writtenCount == board.changeCount else { return false }
        let stale = writeClipboard(good, board: board)
        guard stale["status"] as? String == "changed", board.changeCount == writtenCount else { return false }
        let invalid = Request(action: "clipboardWrite", width: nil, height: nil, windowID: nil, text: nil, png: "invalid", expectedChangeCount: writtenCount)
        let rejected = writeClipboard(invalid, board: board)
        guard rejected["status"] as? String == "error" && board.changeCount == writtenCount else { return false }
        let actualText = readClipboard(board: board)
        guard actualText["text"] as? String == "synthetic" else { return false }
        board.clearContents()
        guard board.setString(String(repeating: "界", count: 87382), forType: .string) else { return false }
        let oversized = readClipboard(board: board)
        guard oversized["oversized"] as? Bool == true && oversized["text"] == nil else { return false }
        board.clearContents()
        let maximum = String(repeating: "界", count: 87381) + "a"
        guard board.setString(maximum, forType: .string), readClipboard(board: board)["text"] as? String == maximum else { return false }
        // Exercise mixed image/text formats on this private test board only.
        board.clearContents()
        guard board.setString("must-not-share", forType: .string),
              board.setData(Data([0x00]), forType: .png) else { return false }
        let image = readClipboard(board: board)
        guard image["text"] == nil && image["png"] == nil else { return false }
        board.clearContents()
        guard board.setString("must-not-share", forType: .string),
              board.setString("file:///synthetic.txt", forType: .fileURL) else { return false }
        let file = readClipboard(board: board)
        return file["text"] == nil && file["png"] == nil
    }
}
