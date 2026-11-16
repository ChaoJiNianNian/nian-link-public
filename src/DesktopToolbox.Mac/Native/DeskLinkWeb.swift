import Foundation
import AppKit
import WebKit

public typealias NianWebCallback = @convention(c) (UnsafeMutableRawPointer?, UnsafePointer<CChar>?) -> Void

@MainActor private var instances: [UInt: NianWebController] = [:]

@MainActor private final class NianWebView: WKWebView {
    weak var bridge: NianWebController?
    override func draggingEntered(_ sender: NSDraggingInfo) -> NSDragOperation {
        guard fileURLs(sender).count > 0 else { return [] }
        bridge?.dragChanged(true); return .copy
    }
    override func draggingUpdated(_ sender: NSDraggingInfo) -> NSDragOperation { fileURLs(sender).isEmpty ? [] : .copy }
    override func draggingExited(_ sender: NSDraggingInfo?) { bridge?.dragChanged(false) }
    override func performDragOperation(_ sender: NSDraggingInfo) -> Bool {
        let files = fileURLs(sender)
        bridge?.dragChanged(false)
        guard !files.isEmpty, files.count <= 128 else { return false }
        bridge?.prepareTransfer(files.count)
        bridge?.emit(["action": "files.nativeDrop", "payload": ["paths": files.map(\.path)], "native": true]); return true
    }
    private func fileURLs(_ sender: NSDraggingInfo) -> [URL] {
        (sender.draggingPasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL] ?? []).filter { $0.isFileURL && $0.path.utf8.count < 4096 }
    }
}

@MainActor private final class NianWebController: NSObject, WKNavigationDelegate, WKUIDelegate, WKScriptMessageHandler {
    let web: NianWebView
    let page: URL
    var callback: NianWebCallback?
    let context: UnsafeMutableRawPointer?
    private var mouseMonitor: Any?
    private var lastMouseDown: NSEvent?
    init(page: URL, callback: NianWebCallback?, context: UnsafeMutableRawPointer?) {
        self.page = page.standardizedFileURL; self.callback = callback; self.context = context
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .nonPersistent()
        configuration.preferences.javaScriptCanOpenWindowsAutomatically = false
        web = NianWebView(frame: NSRect(x: 0, y: 0, width: 398, height: 548), configuration: configuration)
        super.init()
        configuration.userContentController.add(self, name: "nian")
        web.navigationDelegate = self; web.uiDelegate = self; web.bridge = self
        web.wantsLayer = true; web.underPageBackgroundColor = .clear
        // Avalonia changes the attachment container after ArrangeOverride has sized this child.
        // AppKit width/height springs would apply that same size delta a second time.
        web.autoresizingMask = []
        // This setter is implemented by WKWebView on macOS; check it before applying KVC.
        if web.responds(to: NSSelectorFromString("setDrawsBackground:")) || web.responds(to: NSSelectorFromString("_setDrawsBackground:")) { web.setValue(false, forKey: "drawsBackground") }
        mouseMonitor = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown, .leftMouseUp]) { [weak self] event in
            guard let self, let window = self.web.window, event.window === window else { return event }
            self.lastMouseDown = event.type == .leftMouseDown ? event : nil; return event
        }
        web.registerForDraggedTypes([.fileURL])
        web.loadFileURL(self.page, allowingReadAccessTo: self.page.deletingLastPathComponent())
    }
    func isApproved(_ url: URL?) -> Bool { url?.isFileURL == true && url?.standardizedFileURL.path == page.path }
    func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        decisionHandler(isApproved(action.request.url) && action.targetFrame?.isMainFrame != false ? .allow : .cancel)
    }
    func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration, for action: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? { nil }
    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        guard message.name == "nian", message.frameInfo.isMainFrame, isApproved(message.frameInfo.request.url), var body = message.body as? [String: Any], JSONSerialization.isValidJSONObject(body) else { return }
        body["native"] = false; emit(body)
    }
    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) { emit(["action": "native.loaded", "native": true]) }
    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) { emit(["action": "native.error", "payload": ["message": "本机界面加载失败"], "native": true]) }
    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) { emit(["action": "native.reload", "native": true]); webView.reload() }
    func emit(_ object: [String: Any]) {
        guard let data = try? JSONSerialization.data(withJSONObject: object), data.count <= 1024 * 1024, let json = String(data: data, encoding: .utf8) else { return }
        json.withCString { callback?(context, $0) }
    }
    func post(_ kind: String, _ data: Any) {
        Task { @MainActor in _ = try? await web.callAsyncJavaScript("if (kind === 'state') { window.nianState?.(payload); } else if (kind === 'transfer-result') { window.nianNativeTransferResult?.(payload); } else { window.nianReceive?.(payload); }", arguments: ["kind": kind, "payload": data], in: nil, contentWorld: .page) }
    }
    func prepareTransfer(_ count: Int) {
        Task { @MainActor in _ = try? await web.callAsyncJavaScript("window.nianPrepareTransfer?.(count);", arguments: ["count": count], in: nil, contentWorld: .page) }
    }
    func dragChanged(_ over: Bool) {
        Task { @MainActor in _ = try? await web.callAsyncJavaScript("window.nianNativeDrag?.(over); window.dispatchEvent(new CustomEvent('nian-native-drag', {detail:{over}}));", arguments: ["over": over], in: nil, contentWorld: .page) }
    }
    func beginDrag() -> Int32 {
        guard let window = web.window, NSEvent.pressedMouseButtons & 1 == 1 else { return 0 }
        let current = NSApp.currentEvent
        let event = current?.window === window && (current?.type == .leftMouseDown || current?.type == .leftMouseDragged) ? current : lastMouseDown
        guard let event, event.window === window else { return 0 }
        window.performDrag(with: event); lastMouseDown = nil; return 1
    }
    func destroy() {
        if let mouseMonitor { NSEvent.removeMonitor(mouseMonitor) }; mouseMonitor = nil; lastMouseDown = nil
        callback = nil; web.stopLoading(); web.configuration.userContentController.removeScriptMessageHandler(forName: "nian"); web.navigationDelegate = nil; web.uiDelegate = nil; web.bridge = nil; web.removeFromSuperview()
    }
}

@_cdecl("nian_web_create") @MainActor public func nianWebCreate(_ page: UnsafePointer<CChar>?, _ callback: NianWebCallback?, _ context: UnsafeMutableRawPointer?) -> UnsafeMutableRawPointer? {
    guard let page else { return nil }
    let path = String(cString: page)
    guard path.utf8.count < 4096, path.hasPrefix("/"), FileManager.default.fileExists(atPath: path) else { return nil }
    let controller = NianWebController(page: URL(fileURLWithPath: path), callback: callback, context: context)
    let pointer = Unmanaged.passUnretained(controller.web).toOpaque(); instances[UInt(bitPattern: pointer)] = controller; return pointer
}
@_cdecl("nian_web_post") @MainActor public func nianWebPost(_ view: UnsafeMutableRawPointer?, _ kind: UnsafePointer<CChar>?, _ json: UnsafePointer<CChar>?) {
    guard let view, let kind, let json, let controller = instances[UInt(bitPattern: view)] else { return }
    let string = String(cString: json); guard string.utf8.count <= 6 * 1024 * 1024, let data = string.data(using: .utf8), let object = try? JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed]) else { return }
    let type = String(cString: kind); guard type == "state" || type == "reply" || type == "transfer-result" else { return }; controller.post(type, object)
}
@_cdecl("nian_web_destroy") @MainActor public func nianWebDestroy(_ view: UnsafeMutableRawPointer?) {
    guard let view, let controller = instances.removeValue(forKey: UInt(bitPattern: view)) else { return }; controller.destroy()
}
@_cdecl("nian_web_drag") @MainActor public func nianWebDrag(_ view: UnsafeMutableRawPointer?) -> Int32 {
    guard let view, let controller = instances[UInt(bitPattern: view)] else { return 0 }; return controller.beginDrag()
}
@_cdecl("nian_web_snapshot") @MainActor public func nianWebSnapshot(_ view: UnsafeMutableRawPointer?, _ path: UnsafePointer<CChar>?) {
    guard let view, let path, let controller = instances[UInt(bitPattern: view)] else { return }
    let target = String(cString: path); let config = WKSnapshotConfiguration(); config.snapshotWidth = NSNumber(value: controller.web.bounds.width)
    controller.web.takeSnapshot(with: config) { image, error in
        var saved = false
        if let image, let tiff = image.tiffRepresentation, let bitmap = NSBitmapImageRep(data: tiff), let png = bitmap.representation(using: .png, properties: [:]) { do { try png.write(to: URL(fileURLWithPath: target), options: .atomic); saved = true } catch {} }
        controller.emit(["action": "native.snapshot", "payload": ["ok": saved], "native": true])
    }
}




@_cdecl("nian_web_attach") @MainActor public func nianWebAttach(_ view: UnsafeMutableRawPointer?, _ parent: UnsafeMutableRawPointer?) {
    guard let view,let parent,let controller=instances[UInt(bitPattern:view)] else { return }
    let container=Unmanaged<NSView>.fromOpaque(parent).takeUnretainedValue()
    controller.web.frame=container.bounds;container.addSubview(controller.web)
}
@_cdecl("nian_web_is_attached") @MainActor public func nianWebIsAttached(_ view: UnsafeMutableRawPointer?) -> Int32 {
    guard let view,let controller=instances[UInt(bitPattern:view)] else { return 0 };return controller.web.window == nil ? 0:1
}
@_cdecl("nian_web_resize") @MainActor public func nianWebResize(_ view: UnsafeMutableRawPointer?, _ width: Double, _ height: Double) {
    guard let view,let controller=instances[UInt(bitPattern:view)],width.isFinite,height.isFinite,width>0,height>0,width<=2000,height<=2000 else { return }
    controller.web.frame=NSRect(x:0,y:0,width:width,height:height)
}

@_cdecl("nian_web_is_visible") @MainActor public func nianWebIsVisible(_ view: UnsafeMutableRawPointer?) -> Int32 {
    guard let view,let controller=instances[UInt(bitPattern:view)] else { return 0 }
    return controller.web.window?.isVisible == true && !controller.web.isHiddenOrHasHiddenAncestor ? 1:0
}
#if NIAN_WEB_DIAGNOSTICS
// Test-only local diagnostic entry point. Not reachable from page messages.
@_cdecl("nian_web_probe_geometry") @MainActor public func nianWebProbeGeometry(_ view: UnsafeMutableRawPointer?, _ command: UnsafePointer<CChar>?, _ target: UnsafePointer<CChar>?) {
    guard let view,let command,let target,let controller=instances[UInt(bitPattern:view)] else { return }
    let mode=String(cString:command),path=String(cString:target)
    let selectors=["inspect":"", "transfer":"[data-panel=transfer]", "shared":"[data-panel=shared]", "settings":"[data-panel=settings]", "close-panel":"[data-close-panel]:not([hidden])"]
    guard let selector=selectors[mode],path.hasPrefix("/"),path.utf8.count<4096 else { return }
    Task { @MainActor in
        let script="if (selector) { const candidates=[...document.querySelectorAll(selector)]; candidates.find(x=>x.getClientRects().length)?.click(); } const r=x=>x?{x:x.x,y:x.y,width:x.width,height:x.height}:null; return {viewport:{width:innerWidth,height:innerHeight},card:r(document.querySelector('.nl-window')?.getBoundingClientRect()),capsule:r(document.querySelector('.nl-capsule')?.getBoundingClientRect()),body:{width:document.body.scrollWidth,height:document.body.scrollHeight},previewVisible:!document.querySelector('[data-preview-wrap]').hidden};"
        let dom=(try? await controller.web.callAsyncJavaScript(script,arguments:["selector":selector],in:nil,contentWorld:.page)) ?? NSNull()
        func rect(_ value:NSRect)->[String:Double] {["x":value.origin.x,"y":value.origin.y,"width":value.width,"height":value.height]}
        var chain:[[String:Any]]=[]
        var current:NSView?=controller.web
        while let item=current,chain.count<12 {
            chain.append(["class":NSStringFromClass(type(of:item)),"frame":rect(item.frame),"bounds":rect(item.bounds),"visibleRect":rect(item.visibleRect),"flipped":item.isFlipped,"hidden":item.isHidden,"autoresizingMask":item.autoresizingMask.rawValue]);current=item.superview
        }
        var result:[String:Any]=["dom":dom,"chain":chain,"webInWindow":rect(controller.web.convert(controller.web.bounds,to:nil)),"webVisibleInWindow":rect(controller.web.convert(controller.web.visibleRect,to:nil))]
        if let window=controller.web.window {result["windowFrame"]=rect(window.frame);result["windowContentRect"]=rect(window.contentRect(forFrameRect:window.frame));result["windowVisible"]=window.isVisible;if let content=window.contentView {result["contentViewBounds"]=rect(content.bounds);result["webInContentView"]=rect(controller.web.convert(controller.web.bounds,to:content))}}
        if let data=try? JSONSerialization.data(withJSONObject:result,options:[.prettyPrinted,.sortedKeys]) {try? data.write(to:URL(fileURLWithPath:path),options:.atomic)}
    }
}

#endif
