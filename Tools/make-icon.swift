import AppKit

// Draws Uselim's icons in Culpmit's recipe: the Tabler phone-and-message glyph in white on
// graphite. Resources/AppIcon.icns for the Mac (a rounded square on Apple's grid, its shadow) and Resources/Web/icon.png
// for the Home Screen app (the square full bleed, since iOS rounds the corners itself).
// Run from the project root: swift Tools/make-icon.swift

let svgURL = URL(fileURLWithPath: "Resources/Icon/device-mobile-message.svg")
var svg = try! String(contentsOf: svgURL, encoding: .utf8)
svg = svg.replacingOccurrences(of: "currentColor", with: "#FFFFFF").replacingOccurrences(of: "stroke-width=\"2\"", with: "stroke-width=\"1.6\"")
guard let glyph = NSImage(data: Data(svg.utf8)) else { fatalError("the glyph SVG could not be read") }

let graphite = NSGradient(starting: NSColor(calibratedRed: 0.30, green: 0.31, blue: 0.34, alpha: 1),
                          ending: NSColor(calibratedRed: 0.11, green: 0.11, blue: 0.13, alpha: 1))!

func canvas(_ px: Int, _ draw: (CGFloat) -> Void) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8, samplesPerPixel: 4,
                               hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    rep.size = NSSize(width: px, height: px)
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    draw(CGFloat(px))
    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

// the Mac's: a rounded square on 80% of the canvas, the glyph at 62% of the square
func mac(_ px: Int) -> Data {
    canvas(px) { s in
        let body = NSRect(x: s * 0.1, y: s * 0.1, width: s * 0.8, height: s * 0.8)
        let shadow = NSShadow()
        shadow.shadowBlurRadius = s * 0.02
        shadow.shadowOffset = NSSize(width: 0, height: -s * 0.01)
        shadow.shadowColor = NSColor(white: 0, alpha: 0.3)
        NSGraphicsContext.saveGraphicsState()
        shadow.set()
        graphite.draw(in: NSBezierPath(roundedRect: body, xRadius: body.width * 0.225, yRadius: body.width * 0.225), angle: -90)
        NSGraphicsContext.restoreGraphicsState()
        let g = body.width * 0.62
        glyph.draw(in: NSRect(x: body.midX - g / 2, y: body.midY - g / 2, width: g, height: g))
    }
}

// the phone's: the whole square, the glyph at the same share of it
func phone(_ px: Int) -> Data {
    canvas(px) { s in
        graphite.draw(in: NSRect(x: 0, y: 0, width: s, height: s), angle: -90)
        let g = s * 0.62
        glyph.draw(in: NSRect(x: (s - g) / 2, y: (s - g) / 2, width: g, height: g))
    }
}

let set = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("UselimIcon.iconset")
try? FileManager.default.removeItem(at: set)
try! FileManager.default.createDirectory(at: set, withIntermediateDirectories: true)
for size in [16, 32, 128, 256, 512] {
    try! mac(size).write(to: set.appendingPathComponent("icon_\(size)x\(size).png"))
    try! mac(size * 2).write(to: set.appendingPathComponent("icon_\(size)x\(size)@2x.png"))
}
let p = Process()
p.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
p.arguments = ["-c", "icns", set.path, "-o", "Resources/AppIcon.icns"]
try! p.run()
p.waitUntilExit()
print(p.terminationStatus == 0 ? "Resources/AppIcon.icns written" : "iconutil failed")
try! phone(512).write(to: URL(fileURLWithPath: "Resources/Web/icon.png"))
print("Resources/Web/icon.png written")
