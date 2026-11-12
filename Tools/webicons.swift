import AppKit
// Exports the web app's pictures: SF Symbols as alpha masks at 3x
let dir = CommandLine.arguments[1]

func bitmap(_ w: Int, _ h: Int) -> NSBitmapImageRep {
    NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: w, pixelsHigh: h, bitsPerSample: 8, samplesPerPixel: 4,
                     hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
}

func save(_ rep: NSBitmapImageRep, _ name: String) {
    try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: "\(dir)/\(name)"))
}

func symbol(_ name: String, _ file: String, size: CGFloat, weight: NSFont.Weight) {
    let img = NSImage(systemSymbolName: name, accessibilityDescription: nil)!
        .withSymbolConfiguration(.init(pointSize: size, weight: weight))!
    let scale: CGFloat = 3
    let w = Int(ceil(img.size.width * scale)), h = Int(ceil(img.size.height * scale))
    let rep = bitmap(w, h)
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    img.draw(in: NSRect(x: 0, y: 0, width: w, height: h))
    NSColor.black.set()
    NSRect(x: 0, y: 0, width: w, height: h).fill(using: .sourceAtop)
    NSGraphicsContext.restoreGraphicsState()
    save(rep, file)
    print(file, img.size.width, img.size.height)
}

symbol("chevron.backward", "back.png", size: 20, weight: .semibold)
symbol("arrow.up", "send.png", size: 16, weight: .bold)
symbol("plus", "attach.png", size: 16, weight: .semibold)
symbol("xmark", "remove.png", size: 9, weight: .bold)
symbol("doc", "file.png", size: 12, weight: .regular)
// the turn's Stop as an icon alone
symbol("stop.fill", "stop.png", size: 11, weight: .semibold)
// a code block's copy button and its done mark
symbol("doc.on.doc", "copy.png", size: 12, weight: .regular)
// the scan icon in the pairing field, which opens the camera
symbol("qrcode.viewfinder", "scan.png", size: 18, weight: .regular)
symbol("checkmark", "copied.png", size: 12, weight: .semibold)
// the chat's model pill, which opens the switch for its model and effort
symbol("chevron.up.chevron.down", "updown.png", size: 9, weight: .semibold)
// the question and permission card's fold
symbol("chevron.down", "fold.png", size: 12, weight: .semibold)
// the home's bell, the Notifications screen's gear, and a mark per kind of notice
symbol("bell.fill", "bell.png", size: 17, weight: .semibold)
symbol("gearshape", "gear.png", size: 17, weight: .semibold)
// the phone's New chat, a pen beside the bell
symbol("square.and.pencil", "compose.png", size: 17, weight: .semibold)
symbol("bubble.left.fill", "reply.png", size: 17, weight: .semibold)
symbol("questionmark.bubble.fill", "question.png", size: 17, weight: .semibold)
symbol("lock.fill", "permission.png", size: 17, weight: .semibold)
symbol("speedometer", "limit.png", size: 17, weight: .semibold)
// the Home Screen icon comes from Tools/make-icon.swift, with the Mac's
