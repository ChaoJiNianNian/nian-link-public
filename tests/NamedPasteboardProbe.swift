// Synthetic, named pasteboards only. Never touches the user's general clipboard.
import AppKit
import Foundation

_ = NSApplication.shared
let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 2, pixelsHigh: 2,
  bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
  colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
rep.setColor(.red, atX: 0, y: 0)
rep.setColor(.green, atX: 1, y: 0)
rep.setColor(.blue, atX: 0, y: 1)
rep.setColor(.white, atX: 1, y: 1)
let png = rep.representation(using: .png, properties: [:])!
let board = NSPasteboard(name: NSPasteboard.Name("nian-clipboard-test-" + UUID().uuidString))
defer { board.releaseGlobally() }
let beforeClear = board.changeCount
let clearedCount = board.clearContents()
let set = board.setData(png, forType: .png)
let afterSet = board.changeCount
let initialTypes = (board.types ?? []).map { $0.rawValue }
let claimsTiff = board.availableType(from: [.tiff]) != nil
let readable = board.canReadObject(forClasses: [NSImage.self], options: nil)
let images = board.readObjects(forClasses: [NSImage.self], options: nil) as? [NSImage]
let direct = NSImage(pasteboard: board) != nil
let rawTypes = (board.types ?? []).map { $0.rawValue }
let result: [String: Any] = ["beforeClear": beforeClear, "clearedCount": clearedCount, "afterSet": afterSet,
  "writeAccepted": set, "initialTypes": initialTypes,
  "claimsTiffBeforeReading": claimsTiff, "typesAfterRead": rawTypes,
  "canReadNSImage": readable, "readImageCount": images?.count ?? 0, "nsImagePasteboard": direct]
let encoded = try JSONSerialization.data(withJSONObject: result, options: [.sortedKeys])
print(String(data: encoded, encoding: .utf8)!)
