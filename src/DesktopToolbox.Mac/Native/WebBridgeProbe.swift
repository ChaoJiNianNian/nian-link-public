import Foundation
import AppKit
import WebKit

/// Hidden, owned WK page fixtures. Never captures a display, reads pasteboard, or opens a real profile.
@main struct WebBridgeProbe {
    @MainActor static var view: UnsafeMutableRawPointer?
    @MainActor static var output = ""
    @MainActor static var mode = "mac-dark-on"
    @MainActor static var window: NSWindow?
    @MainActor static let epoch = "ui-fixture-not-a-real-connection"
    static func callback(_ context: UnsafeMutableRawPointer?, _ json: UnsafePointer<CChar>?) {
        guard let json else { return }; let text = String(cString: json)
        DispatchQueue.main.async { handle(text) }
    }
    @MainActor static func post(_ kind: String, _ value: [String: Any]) {
        guard let view, let data = try? JSONSerialization.data(withJSONObject: value), let json = String(data:data,encoding:.utf8) else { return }
        kind.withCString { type in json.withCString { nianWebPost(view,type,$0) } }
    }
    @MainActor static func reply(_ id: String, _ result: Any) { post("reply",["id":id,"ok":true,"epoch":epoch,"result":result]) }
    @MainActor static func handle(_ text: String) {
        guard let data = text.data(using:.utf8), let event = try? JSONSerialization.jsonObject(with:data) as? [String:Any], let action = event["action"] as? String else { return }
        if action == "ready" {
            reply(event["id"] as? String ?? "",["epoch":epoch]); pushState()
        } else if action == "window.resize", let payload=event["payload"] as? [String:Any], let width=payload["width"] as? Double, let height=payload["height"] as? Double {
            window?.setContentSize(NSSize(width:width,height:height)); print("{\"fixture\":\"\(mode)\",\"width\":\(width),\"height\":\(height)}"); reply(event["id"] as? String ?? "",[:])
        } else if action == "shared.list" {
            reply(event["id"] as? String ?? "",["rootId":"project","relativePath":"","items":[["rootId":"project","relativePath":"设计资料","name":"设计资料","isDirectory":true,"length":0],["rootId":"project","relativePath":"机身结构图.dwg","name":"机身结构图.dwg","isDirectory":false,"length":5360920],["rootId":"project","relativePath":"项目说明.txt","name":"项目说明.txt","isDirectory":false,"length":3200]]])
        } else if action == "native.loaded", let view {
            let web=Unmanaged<WKWebView>.fromOpaque(view).takeUnretainedValue()
            let panel=["transfer":"transfer","shared-files":"shared","clipboard":"clipboard","settings":"settings"][mode]
            DispatchQueue.main.asyncAfter(deadline:.now()+0.4) {
                if let panel { Task { @MainActor in _ = try? await web.callAsyncJavaScript("Array.from(document.querySelectorAll('[data-panel]')).find(x=>x.dataset.panel===panel)?.click();",arguments:["panel":panel],in:nil,contentWorld:.page) } }

            }
            DispatchQueue.main.asyncAfter(deadline:.now()+1.3) {
                Task { @MainActor in
                    if let result = try? await web.callAsyncJavaScript("document.getAnimations().forEach(a=>a.finish()); const r=document.querySelector('#nian-link-mini').getBoundingClientRect(); return {width:Math.ceil(r.width+24),height:Math.ceil(r.height+24)};", arguments: [:], in:nil, contentWorld:.page) as? [String:Double], let width=result["width"],let height=result["height"] { window?.setContentSize(NSSize(width:width,height:height)); print("{\"fixture\":\"\(mode)\",\"width\":\(width),\"height\":\(height)}") }
                    output.withCString { nianWebSnapshot(view,$0) }
                }
            }
        } else if action == "native.snapshot" {
            if let view { nianWebDestroy(view) }; window?.close(); print("{\"fixture\":\"\(mode)\",\"saved\":true}"); exit(0)
        } else if let id=event["id"] as? String { reply(id,[:]) }
        fflush(stdout)
    }
    @MainActor static func pushState() {
        let preview=mode != "preview-off" && mode != "capsule"
        post("state",["epoch":epoch,"revision":1,"platform":"Mac","connected":true,"connectionMessage":"Windows 已连接","activeComputer":"Mac","previewEnabled":preview,"preview":["state":"live","width":960,"height":540,"jpegBytes":desktopFixture()],"clipboardEnabled":true,"clipboardMessage":"两端已开启，新复制的文字和截图会自动互通。","receiveDirectory":"/UI夹具/收件箱","sharedDirectory":"","transfers":[["id":"fixture","name":"设计资料.zip","bytesTransferred":26528972,"totalBytes":38019072,"state":"sending"]],"canPauseSending":true,"canRetrySending":false,"pinned":false,"collapsed":mode == "capsule","theme":mode == "light-on" ? "light":"dark","maxHeight":1000,"wireless":["enabled":true,"canChange":true,"needsRestoreRetry":false,"canRetryRestore":false,"status":"已开启","error":""]])
    }
    @MainActor static func desktopFixture() -> String {
        let image=NSImage(size:NSSize(width:960,height:540)); image.lockFocus()
        NSGradient(starting:NSColor(calibratedRed:0.57,green:0.77,blue:0.94,alpha:1),ending:NSColor(calibratedRed:0.21,green:0.43,blue:0.82,alpha:1))?.draw(in:NSRect(x:0,y:0,width:960,height:540),angle:-20)
        NSColor(calibratedWhite:0.97,alpha:1).setFill(); NSBezierPath(roundedRect:NSRect(x:140,y:150,width:680,height:270),xRadius:20,yRadius:20).fill()
        let grey=NSColor(calibratedRed:0.34,green:0.40,blue:0.49,alpha:1), ink=NSColor(calibratedRed:0.16,green:0.20,blue:0.28,alpha:1)
        func label(_ text:String,_ x:Double,_ y:Double,_ size:Double,_ color:NSColor) { (text as NSString).draw(at:NSPoint(x:x,y:y),withAttributes:[.font:NSFont.systemFont(ofSize:size),.foregroundColor:color]) }
        label("● ● ●   视频导出",174,366,23,grey); label("正在导出",174,294,31,ink); label("72%",694,294,31,ink)
        NSColor(calibratedWhite:0.86,alpha:1).setFill(); NSBezierPath(roundedRect:NSRect(x:174,y:264,width:612,height:13),xRadius:7,yRadius:7).fill()
        NSColor(calibratedRed:0.32,green:0.53,blue:0.90,alpha:1).setFill(); NSBezierPath(roundedRect:NSRect(x:174,y:264,width:441,height:13),xRadius:7,yRadius:7).fill()
        label("项目展示.mp4 · 剩余约 2 分钟",174,204,25,grey)
        NSColor(calibratedWhite:1,alpha:0.45).setFill(); NSRect(x:0,y:0,width:960,height:58).fill()
        label("界面验收夹具 · 未采集用户桌面",302,16,18,ink); image.unlockFocus()
        guard let tiff=image.tiffRepresentation,let bitmap=NSBitmapImageRep(data:tiff),let jpeg=bitmap.representation(using:.jpeg,properties:[.compressionFactor:0.88]) else { return "" };return jpeg.base64EncodedString()
    }
    @MainActor static func main() {
        guard CommandLine.arguments.count == 4 else { exit(2) }
        let app=NSApplication.shared;app.setActivationPolicy(.prohibited)
        output=CommandLine.arguments[2];mode=CommandLine.arguments[3]
        view=CommandLine.arguments[1].withCString { nianWebCreate($0,{ context,json in WebBridgeProbe.callback(context,json) },nil) }
        guard let view else { exit(2) };let native=Unmanaged<NSView>.fromOpaque(view).takeUnretainedValue()
        let window=NSWindow(contentRect:NSRect(x:-5000,y:-5000,width:398,height:548),styleMask:.borderless,backing:.buffered,defer:false)
        window.isOpaque=false;window.backgroundColor = .clear;window.contentView=native;native.frame=NSRect(x:0,y:0,width:398,height:548);self.window=window
        DispatchQueue.main.asyncAfter(deadline:.now()+15) { print("{\"fixture\":\"timeout\"}");exit(4) };app.run()
    }
}

