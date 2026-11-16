import Foundation
import CoreFoundation
import ScreenCaptureKit
import CoreGraphics
import CoreMedia
import CoreVideo
import CoreImage
import ImageIO

// A return control is the only app-owned window that may be exempted from
// excludingApplications. Candidate metadata is deliberately non-sensitive.
enum RealtimeReturnWindowPolicy {
    static let title = "NianPreviewReturnOverlay"
    static let ownBundles: Set<String> = ["com.nian.desktoptoolbox.desklink", "com.nian.desktoptoolbox.desklink.native-candidate", "com.nian.desktoptoolbox.desklink.preview-candidate", "com.niannian.nianlink", "com.niannian.nianlink.multidevice-test", "com.niannian.nianlink.discovery-test", "com.niannian.nianlink.fileless-test"]
    struct Candidate {
        let id: UInt32
        let pid: Int32
        let bundle: String?
        let title: String?
    }
    static func selectedWindow(_ candidates: [Candidate], parentPID: Int32, excludedPIDs: Set<Int32>, allowReturn: Bool = true) -> UInt32? {
        guard allowReturn, parentPID > 0, excludedPIDs.contains(parentPID) else { return nil }
        let matches = candidates.filter {
            $0.id != 0 && $0.pid == parentPID && $0.title == title &&
                $0.bundle.map { ownBundles.contains($0) } == true
        }
        return matches.count == 1 ? matches[0].id : nil
    }
    static func selfTest() -> Bool {
        // Each packaged candidate must obey the same parent, uniqueness and
        // explicit-sharing checks as the existing application identities.
        for bundle in ["com.niannian.nianlink.multidevice-test", "com.niannian.nianlink.discovery-test", "com.niannian.nianlink.fileless-test"] {
            let candidate = Candidate(id: 42, pid: 12, bundle: bundle, title: title)
            let duplicate = Candidate(id: 47, pid: 12, bundle: bundle, title: title)
            let otherApp = Candidate(id: 48, pid: 12, bundle: bundle + ".other", title: title)
            guard selectedWindow([candidate], parentPID: 12, excludedPIDs: [12]) == 42,
                  selectedWindow([candidate], parentPID: 13, excludedPIDs: [12,13]) == nil,
                  selectedWindow([candidate], parentPID: 12, excludedPIDs: []) == nil,
                  selectedWindow([candidate, duplicate], parentPID: 12, excludedPIDs: [12]) == nil,
                  selectedWindow([candidate], parentPID: 12, excludedPIDs: [12], allowReturn: false) == nil,
                  selectedWindow([otherApp], parentPID: 12, excludedPIDs: [12]) == nil else { return false }
        }
        let valid = Candidate(id: 42, pid: 12, bundle: "com.nian.desktoptoolbox.desklink.preview-candidate", title: title)
        let other = Candidate(id: 43, pid: 12, bundle: "com.nian.desktoptoolbox.desklink.preview-candidate", title: "年年互联")
        let wrongPID = Candidate(id: 44, pid: 13, bundle: valid.bundle, title: title)
        let wrongBundle = Candidate(id: 45, pid: 12, bundle: "other.app", title: title)
        let similarTitle = Candidate(id: 46, pid: 12, bundle: valid.bundle, title: title + " copy")
        let zeroID = Candidate(id: 0, pid: 12, bundle: valid.bundle, title: title)
        let duplicate = Candidate(id: 47, pid: 12, bundle: valid.bundle, title: title)
        return selectedWindow([valid, other, wrongPID, wrongBundle, similarTitle], parentPID: 12, excludedPIDs: [12]) == 42 &&
            selectedWindow([valid], parentPID: 13, excludedPIDs: [12,13]) == nil &&
            selectedWindow([wrongPID, wrongBundle, similarTitle, zeroID], parentPID: 12, excludedPIDs: [12]) == nil &&
            selectedWindow([valid], parentPID: 12, excludedPIDs: []) == nil &&
            selectedWindow([valid, duplicate], parentPID: 12, excludedPIDs: [12]) == nil &&
            selectedWindow([], parentPID: 12, excludedPIDs: [12]) == nil &&
            selectedWindow([valid], parentPID: 12, excludedPIDs: [12], allowReturn: false) == nil
    }
}

// Enumeration cannot alter an existing filter. An attempted update may have
// altered it, so a failed update must first install the closed filter.
enum RealtimeFilterRecoveryPolicy {
    enum Failure { case enumeration, update, closedUpdate }
    enum Action { case retainAndRetry, applyClosed, stop }
    static func action(_ failure: Failure) -> Action {
        switch failure {
        case .enumeration: return .retainAndRetry
        case .update: return .applyClosed
        case .closedUpdate: return .stop
        }
    }
    static func selfTest() -> Bool {
        action(.enumeration) == .retainAndRetry && action(.update) == .applyClosed && action(.closedUpdate) == .stop
    }
}

// A health signal must eventually be based on recent capture callbacks rather
// than a timer merely observing that startCapture succeeded.
enum RealtimeSourceHealth {
    enum State { case waiting, healthy, stalled }
    static func evaluate(started: TimeInterval, lastCallback: TimeInterval?, now: TimeInterval) -> State {
        guard started.isFinite, now.isFinite, started >= 0, now >= started else { return .stalled }
        guard let last = lastCallback else { return now - started <= 4 ? .waiting : .stalled }
        guard last.isFinite, last >= 0, last <= now else { return .stalled }
        return now - last <= 4 ? .healthy : .stalled
    }
    static func shouldSendIdle(_ state: State, lastCallbackWasIdle: Bool, latestCapture: Int64 = 0, writtenCapture: Int64 = 0) -> Bool {
        state == .healthy && lastCallbackWasIdle && latestCapture > 0 && writtenCapture >= latestCapture
    }
    static func selfTest() -> Bool {
        evaluate(started: 100, lastCallback: nil, now: 101) == .waiting &&
        evaluate(started: 100, lastCallback: 101, now: 102) == .healthy &&
        evaluate(started: 100, lastCallback: 101, now: 105) == .healthy &&
        evaluate(started: 100, lastCallback: 110, now: 113) == .healthy &&
        evaluate(started: 100, lastCallback: 101, now: 105.1) == .stalled &&
        evaluate(started: 100, lastCallback: nil, now: 104.1) == .stalled &&
        evaluate(started: 100, lastCallback: 101, now: 100) == .stalled &&
        shouldSendIdle(.healthy, lastCallbackWasIdle: true, latestCapture: 1, writtenCapture: 1) &&
        !shouldSendIdle(.healthy, lastCallbackWasIdle: false, latestCapture: 1, writtenCapture: 1) &&
        !shouldSendIdle(.healthy, lastCallbackWasIdle: true, latestCapture: 2, writtenCapture: 1) &&
        shouldSendIdle(.healthy, lastCallbackWasIdle: true, latestCapture: 2, writtenCapture: 2) &&
        !shouldSendIdle(.healthy, lastCallbackWasIdle: true, latestCapture: 1, writtenCapture: 0)
    }
}

// This stricter policy belongs to the new continuous capture path. The legacy
// screenshot helper intentionally keeps its existing session interpretation.
enum RealtimeCaptureSessionPolicy {
    private static let consoleKey = kCGSessionOnConsoleKey as String
    private static let loginKey = kCGSessionLoginDoneKey as String
    private static let lockedKey = "CGSSessionScreenIsLocked"

    static func currentFailure() -> String? {
        failure(CGSessionCopyCurrentDictionary() as? [String: Any])
    }
    static func failure(_ session: [String: Any]?) -> String? {
        guard let session = session else { return "source-unavailable" }
        if let raw = session[lockedKey] {
            guard let locked = boolean(raw) else { return "source-unavailable" }
            if locked { return "locked" }
        }
        guard let console = session[consoleKey], boolean(console) == true,
              let loggedIn = session[loginKey], boolean(loggedIn) == true else { return "source-unavailable" }
        return nil
    }
    private static func boolean(_ raw: Any) -> Bool? {
        let value = raw as AnyObject
        guard CFGetTypeID(value) == CFBooleanGetTypeID() else { return nil }
        return (raw as? NSNumber)?.boolValue
    }
    static func selfTest() -> Bool {
        let ready: [String: Any] = [consoleKey: true, loginKey: true]
        var locked = ready; locked[lockedKey] = true
        var offConsole = ready; offConsole[consoleKey] = false
        var unfinished = ready; unfinished[loginKey] = false
        var numeric = ready; numeric[consoleKey] = NSNumber(value: 1)
        var unknownLock = ready; unknownLock[lockedKey] = "false"
        return failure(nil) == "source-unavailable" && failure([:]) == "source-unavailable" &&
            failure(ready) == nil && failure(locked) == "locked" &&
            failure(offConsole) == "source-unavailable" && failure(unfinished) == "source-unavailable" &&
            failure(numeric) == "source-unavailable" && failure(unknownLock) == "source-unavailable"
    }
}

private struct RealtimeRequest: Decodable {
    var action: String
    var fps: Int?
    var width: Int?
    var height: Int?
    var quality: Int?
    var valid: Bool {
        action == "start" && (15...60).contains(fps ?? 0) && (1...1920).contains(width ?? 0) &&
        (1...1080).contains(height ?? 0) && (1...100).contains(quality ?? 0)
    }
}

enum RealtimeCaptureRunner {
    static func run() async {
        let output = RealtimeCaptureOutput()
        guard let line = readLine(), line.utf8.count <= 4096,
              let request = try? JSONDecoder().decode(RealtimeRequest.self, from: Data(line.utf8)), request.valid else {
            output.fail("capture-failed"); await output.waitUntilClosed(); return
        }
        let capture = RealtimeCapture(request, output: output)
        let input = Task.detached(priority: .userInitiated) {
            // There is one finite generation per process. Any EOF/invalid control ends it.
            _ = readLine()
            await capture.stop()
        }
        await capture.start()
        await input.value
        await capture.stop()
        output.finish()
    }
}

private struct RealtimePixelFrame {
    let buffer: CVPixelBuffer
    let captured: Int64
    let captureRevision: Int64
}

private struct RealtimeFilterSelection: Equatable {
    let displayID: CGDirectDisplayID
    let excludedPIDs: [Int32]
    let returnWindowID: UInt32?
}

private final class RealtimeCapture: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    private let options: RealtimeRequest
    private let output: RealtimeCaptureOutput
    private let lock = NSLock()
    private let sampleQueue = DispatchQueue(label: "nian.capture.samples", qos: .userInteractive)
    private let encodingQueue = DispatchQueue(label: "nian.capture.jpeg", qos: .userInitiated)
    private let healthQueue = DispatchQueue(label: "nian.capture.health", qos: .userInitiated)
    private let context = CIContext(options: [.cacheIntermediates: false])
    private let colorSpace = CGColorSpace(name: CGColorSpace.sRGB)!
    private let parentPID = getppid()
    private var stream: SCStream?
    private var active = true
    private var capturing = false
    private var pending: RealtimePixelFrame?
    private var encoding = false
    private var source: PreviewSourceSnapshot?
    private var healthTimer: DispatchSourceTimer?
    private var stoppingTask: Task<Void, Never>?
    private var filterRefreshTask: Task<Void, Never>?
    private var filterSelection: RealtimeFilterSelection?
    private var captureStartedAt: TimeInterval = 0
    private var lastCaptureCallbackAt: TimeInterval?
    private var lastCaptureCallbackWasIdle = false
    private var latestCaptureRevision: Int64 = 0

    init(_ options: RealtimeRequest, output: RealtimeCaptureOutput) { self.options = options; self.output = output }
    private var isActive: Bool { lock.withLock { active } }

    func start() async {
        guard isActive else { return }
        if let reason = RealtimeCaptureSessionPolicy.currentFailure() { fail(reason); return }
        guard CGPreflightScreenCaptureAccess() else { fail("permission"); return }
        do {
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
            guard isActive else { return }
            guard let display = content.displays.first(where: { $0.displayID == CGMainDisplayID() }),
                  let snapshot = DeskLinkMedia.previewSource(display.displayID), display.width > 0, display.height > 0 else {
                fail("source-unavailable"); return
            }
            let (filter, selection) = makeFilter(content, display: display)
            let scale = min(Double(options.width!) / Double(display.width), Double(options.height!) / Double(display.height))
            let configuration = SCStreamConfiguration()
            configuration.width = max(1, Int(Double(display.width) * scale))
            configuration.height = max(1, Int(Double(display.height) * scale))
            configuration.pixelFormat = kCVPixelFormatType_32BGRA
            configuration.minimumFrameInterval = CMTime(value: 1, timescale: Int32(options.fps!))
            configuration.queueDepth = 3; configuration.showsCursor = true
            configuration.capturesAudio = false; configuration.scalesToFit = true
            let next = SCStream(filter: filter, configuration: configuration, delegate: self)
            try next.addStreamOutput(self, type: .screen, sampleHandlerQueue: sampleQueue)
            let accepted = lock.withLock { () -> Bool in
                guard active else { return false }; stream = next; source = snapshot; filterSelection = selection; return true
            }
            guard accepted else { return }
            try await next.startCapture()
            guard isActive else { try? await next.stopCapture(); return }
            lock.withLock { capturing = true; captureStartedAt = ProcessInfo.processInfo.systemUptime }
            startHealthCheck()
            startFilterRefresh()
        } catch { fail(CGPreflightScreenCaptureAccess() ? "capture-failed" : "permission") }
    }

    // ScreenCaptureKit's exceptions invert the application exclusion. Only the
    // unique return window of this helper's own parent may be made visible;
    // all other windows, including other running versions, remain excluded.
    private func makeFilter(_ content: SCShareableContent, display: SCDisplay, allowReturn: Bool = true) -> (SCContentFilter, RealtimeFilterSelection) {
        let ownApps = content.applications.filter {
            RealtimeReturnWindowPolicy.ownBundles.contains($0.bundleIdentifier) || $0.processID == parentPID || $0.processID == getpid()
        }
        let excluded = Set(ownApps.map { $0.processID })
        let candidates = content.windows.map { window in
            RealtimeReturnWindowPolicy.Candidate(id: window.windowID,
                pid: window.owningApplication?.processID ?? 0,
                bundle: window.owningApplication?.bundleIdentifier, title: window.title)
        }
        let selected = RealtimeReturnWindowPolicy.selectedWindow(candidates, parentPID: parentPID, excludedPIDs: excluded, allowReturn: allowReturn)
        let exceptions = content.windows.filter { $0.windowID == selected && $0.owningApplication?.processID == parentPID }
        let filter = SCContentFilter(display: display, excludingApplications: ownApps, exceptingWindows: exceptions)
        return (filter, RealtimeFilterSelection(displayID: display.displayID, excludedPIDs: excluded.sorted(), returnWindowID: selected))
    }

    private func startFilterRefresh() {
        let task = Task { [weak self] in
            guard let self = self else { return }
            await self.refreshFilterLoop()
        }
        let accepted = lock.withLock { () -> Bool in
            guard active, capturing else { return false }; filterRefreshTask = task; return true
        }
        if !accepted { task.cancel() }
    }

    // Return windows are created only during control. Refresh independently of
    // frame encoding and health checks, and update only when the selection changes.
    private func refreshFilterLoop() async {
        while !Task.isCancelled {
            do { try await Task.sleep(nanoseconds: 1_000_000_000) } catch { return }
            guard isActive, !Task.isCancelled else { return }
            let content: SCShareableContent
            do {
                content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
            } catch {
                if Task.isCancelled || !isActive { return }
                if RealtimeFilterRecoveryPolicy.action(.enumeration) == .retainAndRetry { continue }
                fail("source-unavailable"); return
            }
            guard !Task.isCancelled,
                  let current = lock.withLock({ active && capturing ? stream : nil }) else { return }
            guard let display = content.displays.first(where: { $0.displayID == CGMainDisplayID() }) else {
                fail("source-unavailable"); return
            }
            let (filter, selection) = makeFilter(content, display: display)
            let changed = lock.withLock { active && stream === current && filterSelection != selection }
            if changed {
                do {
                    try await current.updateContentFilter(filter)
                    lock.withLock { if active && stream === current { filterSelection = selection } }
                } catch {
                    guard isActive, !Task.isCancelled else { return }
                    guard RealtimeFilterRecoveryPolicy.action(.update) == .applyClosed else { fail("source-unavailable"); return }
                    let (closedFilter, closedSelection) = makeFilter(content, display: display, allowReturn: false)
                    do {
                        try await current.updateContentFilter(closedFilter)
                        lock.withLock { if active && stream === current { filterSelection = closedSelection } }
                    } catch {
                        if isActive && !Task.isCancelled && RealtimeFilterRecoveryPolicy.action(.closedUpdate) == .stop {
                            fail("source-unavailable")
                        }
                        return
                    }
                }
            }
        }
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of outputType: SCStreamOutputType) {
        guard outputType == .screen, isActive, sampleBuffer.isValid,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let raw = attachments.first?[.status] as? Int, let status = SCFrameStatus(rawValue: raw) else { return }
        if status == .idle {
            lock.withLock { if active { lastCaptureCallbackAt = ProcessInfo.processInfo.systemUptime; lastCaptureCallbackWasIdle = true } }
            checkHealth(); return
        }
        if status == .blank || status == .suspended || status == .stopped { fail("source-unavailable"); return }
        guard status == .complete, let buffer = sampleBuffer.imageBuffer else { return }
        let width = CVPixelBufferGetWidth(buffer), height = CVPixelBufferGetHeight(buffer)
        guard width > 0, height > 0, width <= options.width!, height <= options.height! else { fail("capture-failed"); return }
        let now = Date()
        let age = CMTimeGetSeconds(CMTimeSubtract(CMClockGetTime(CMClockGetHostTimeClock()), sampleBuffer.presentationTimeStamp))
        guard age.isFinite, age >= -0.5, age <= 5 else { fail("capture-failed"); return }
        let captured = Int64(now.addingTimeInterval(-max(0, age)).timeIntervalSince1970 * 1000)
        lock.lock()
        guard active else { lock.unlock(); return }
        guard latestCaptureRevision < Int64.max else { lock.unlock(); fail("capture-failed"); return }
        latestCaptureRevision += 1
        lastCaptureCallbackAt = ProcessInfo.processInfo.systemUptime
        lastCaptureCallbackWasIdle = false
        pending = RealtimePixelFrame(buffer: buffer, captured: captured, captureRevision: latestCaptureRevision)
        let schedule = !encoding; encoding = true; lock.unlock()
        if schedule { encodingQueue.async { self.encodeLatest() } }
    }
    func stream(_ stream: SCStream, didStopWithError error: Error) { fail("capture-failed") }

    private func encodeLatest() {
        while true {
            lock.lock()
            guard active, let frame = pending else { pending = nil; encoding = false; lock.unlock(); return }
            pending = nil; lock.unlock()
            let data: Data? = autoreleasepool {
                let image = CIImage(cvPixelBuffer: frame.buffer)
                let key = CIImageRepresentationOption(rawValue: kCGImageDestinationLossyCompressionQuality as String)
                return context.jpegRepresentation(of: image, colorSpace: colorSpace, options: [key: Double(options.quality!) / 100])
            }
            guard let jpeg = data, jpeg.count >= 4, jpeg.count <= 1024 * 1024 else { fail("capture-failed"); return }
            guard isActive else { return }
            output.image(jpeg, width: CVPixelBufferGetWidth(frame.buffer), height: CVPixelBufferGetHeight(frame.buffer), captured: frame.captured, captureRevision: frame.captureRevision)
        }
    }
    private func startHealthCheck() {
        let timer = DispatchSource.makeTimerSource(queue: healthQueue)
        timer.schedule(deadline: .now() + .seconds(1), repeating: .seconds(1))
        timer.setEventHandler { [weak self] in self?.checkHealth() }
        let accepted = lock.withLock { () -> Bool in
            guard active else { return false }; healthTimer = timer; return true
        }
        if accepted { timer.resume() } else { timer.setEventHandler {}; timer.cancel(); timer.resume() }
    }
    private func checkHealth() {
        guard isActive else { return }
        if let reason = RealtimeCaptureSessionPolicy.currentFailure() { fail(reason); return }
        if !CGPreflightScreenCaptureAccess() { fail("permission"); return }
        let current = lock.withLock { (capturing, source, captureStartedAt, lastCaptureCallbackAt, lastCaptureCallbackWasIdle) }
        guard current.0, let source = current.1 else { return }
        guard DeskLinkMedia.previewSource(source.displayID) == source else { fail("source-changed"); return }
        // Capture callbacks can race the health timer. Re-evaluate under the capture
        // lock and enqueue idle while the newest complete revision cannot change.
        let stalled = lock.withLock { () -> Bool in
            guard active, capturing else { return false }
            let state = RealtimeSourceHealth.evaluate(started: captureStartedAt, lastCallback: lastCaptureCallbackAt,
                                                      now: ProcessInfo.processInfo.systemUptime)
            if RealtimeSourceHealth.shouldSendIdle(state, lastCallbackWasIdle: lastCaptureCallbackWasIdle,
                                                   latestCapture: latestCaptureRevision, writtenCapture: output.lastWrittenCaptureRevision) {
                output.healthyIdle()
            }
            return state == .stalled
        }
        if stalled { fail("source-unavailable") }
    }
    private func fail(_ code: String) {
        guard retire() else { return }
        output.fail(code)
    }
    private func retire() -> Bool {
        lock.lock()
        guard active else { lock.unlock(); return false }
        active = false; capturing = false; pending = nil
        let value = stream; stream = nil
        if let value = value { stoppingTask = Task { try? await value.stopCapture() } }
        let timer = healthTimer; healthTimer = nil
        let refresh = filterRefreshTask; filterRefreshTask = nil; filterSelection = nil; lock.unlock()
        refresh?.cancel()
        timer?.setEventHandler {}; timer?.cancel()
        return true
    }
    func stop() async {
        _ = retire()
        let stopping = lock.withLock { stoppingTask }
        await stopping?.value
    }
}
