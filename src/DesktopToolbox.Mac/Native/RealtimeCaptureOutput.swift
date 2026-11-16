import Foundation
import Darwin

// Local helper framing, deliberately separate from the network's authenticated envelope.
// NLMC | version:u8 | kind:u8 | reserved:u16 | sequence:i64 | unixMillis:i64 |
// width:i32 | height:i32 | payloadBytes:i32 | reserved:i32. All numbers are little-endian.
final class RealtimeCaptureOutput: @unchecked Sendable {
    private enum Item {
        case image(Data, Int, Int, Int64, Int64)
        case idle
        case failure(String)
    }
    private let lock = NSLock()
    private let queue = DispatchQueue(label: "nian.capture.binary-writer", qos: .userInitiated)
    private var pending: Item?
    private var writing = false
    private var closed = false
    private var terminal = false
    private var sequence: Int64 = 0
    private var wroteImage = false
    private var writtenCaptureRevision: Int64 = 0
    private var idleAt: TimeInterval = 0
    private var completion: CheckedContinuation<Void, Never>?

    init() { signal(SIGPIPE, SIG_IGN) }
    var hasWrittenImage: Bool { lock.withLock { wroteImage } }
    var lastWrittenCaptureRevision: Int64 { lock.withLock { writtenCaptureRevision } }

    func image(_ jpeg: Data, width: Int, height: Int, captured: Int64, captureRevision: Int64) {
        offer(.image(jpeg, width, height, captured, captureRevision))
    }
    func healthyIdle() {
        let now = ProcessInfo.processInfo.systemUptime
        lock.lock()
        guard !closed, !terminal, wroteImage, pending == nil, now - idleAt >= 0.8 else { lock.unlock(); return }
        idleAt = now; pending = .idle
        scheduleLocked(); lock.unlock()
    }
    func fail(_ code: String) {
        lock.lock()
        guard !closed, !terminal else { lock.unlock(); return }
        terminal = true; pending = .failure(code)
        scheduleLocked(); lock.unlock()
    }
    private func offer(_ item: Item) {
        lock.lock()
        guard !closed, !terminal else { lock.unlock(); return }
        pending = item // At most one encoded frame waiting behind the current write.
        scheduleLocked(); lock.unlock()
    }
    private func scheduleLocked() {
        if writing { return }
        writing = true
        queue.async { self.drain() }
    }
    private func drain() {
        while true {
            lock.lock()
            guard !closed, let item = pending else { writing = false; lock.unlock(); return }
            pending = nil
            guard sequence < Int64.max else { closed = true; lock.unlock(); return }
            sequence += 1; let next = sequence; lock.unlock()
            let kind: UInt8, data: Data, width: Int, height: Int, captured: Int64, captureRevision: Int64
            switch item {
            case let .image(bytes, w, h, time, revision): kind = 1; data = bytes; width = w; height = h; captured = time; captureRevision = revision
            case .idle: kind = 2; data = Data(); width = 0; height = 0; captured = 0; captureRevision = 0
            case let .failure(code): kind = 3; data = Data(code.utf8); width = 0; height = 0; captured = 0; captureRevision = 0
            }
            var header = Data(repeating: 0, count: 40)
            header.replaceSubrange(0..<4, with: Data("NLMC".utf8)); header[4] = 1; header[5] = kind
            put(next, at: 8, in: &header); put(captured, at: 16, in: &header)
            put(Int32(width), at: 24, in: &header); put(Int32(height), at: 28, in: &header)
            put(Int32(data.count), at: 32, in: &header)
            do {
                try FileHandle.standardOutput.write(contentsOf: header)
                if !data.isEmpty { try FileHandle.standardOutput.write(contentsOf: data) }
                lock.withLock { if kind == 1 { wroteImage = true; writtenCaptureRevision = captureRevision } }
            } catch { finish(); return }
            if kind == 3 { finish(); return }
        }
    }
    private func put<T: FixedWidthInteger>(_ value: T, at offset: Int, in data: inout Data) {
        var number = value.littleEndian
        withUnsafeBytes(of: &number) { data.replaceSubrange(offset..<(offset + MemoryLayout<T>.size), with: $0) }
    }
    func finish() {
        lock.lock()
        let close = !closed
        closed = true; pending = nil
        let waiting = completion; completion = nil; lock.unlock()
        if close { try? FileHandle.standardOutput.close() }
        waiting?.resume()
    }
    func waitUntilClosed() async {
        await withCheckedContinuation { continuation in
            lock.lock()
            if closed { lock.unlock(); continuation.resume() }
            else { completion = continuation; lock.unlock() }
        }
    }
}
