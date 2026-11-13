import Foundation
import ImageIO
import UniformTypeIdentifiers

// Files from the phone, saved in relay/files/<session>/ under their own names and handed to the chat by path. A picture
// is fitted the way Culpmit fits a screenshot: the long side 1,500 px at most and the area two thirds of that square
// (1.5 MP), so the chat reads it whole; one the Claude Code panel takes as it is (JPEG, PNG, GIF, WebP) and that fits
// already stays byte for byte, every other one becomes a JPEG, or a PNG where it carries transparency.

let uploadLimit = 200 * 1_048_576
private let takenAsIs: [String: String] = [
    UTType.jpeg.identifier: "jpg", UTType.png.identifier: "png", UTType.gif.identifier: "gif",
    UTType.webP.identifier: "webp",
]

func mimeType(_ ext: String) -> String {
    ["jpg": "image/jpeg", "png": "image/png", "gif": "image/gif", "webp": "image/webp"][ext] ?? "application/octet-stream"
}

// the bytes to save and their extension for a picture, nil for anything else (a PDF, a text, an SVG); the
// phone's previews take the same rule at 1,000 px
func fitImage(_ data: Data, longSide: Double = 1500) -> (data: Data, ext: String)? {
    guard let source = CGImageSourceCreateWithData(data as CFData, nil),
          let fitted = fit(source, longSide: longSide) else { return nil }
    return (fitted.data ?? data, fitted.ext)
}

// the same rule for a file on disk, which ImageIO reads from the file: `data` nil where the picture goes as it is
func fitImage(at url: URL, longSide: Double = 1500) -> (data: Data?, ext: String)? {
    guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
    return fit(source, longSide: longSide)
}

private func fit(_ source: CGImageSource, longSide: Double) -> (data: Data?, ext: String)? {
    guard let type = CGImageSourceGetType(source) as String?,
          let uti = UTType(type), uti.conforms(to: .image), !uti.conforms(to: .svg),
          let props = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
          let w = (props[kCGImagePropertyPixelWidth] as? NSNumber)?.doubleValue,
          let h = (props[kCGImagePropertyPixelHeight] as? NSNumber)?.doubleValue, w > 0, h > 0 else { return nil }
    // a turned photo is drawn upright, since the model reads the pixels as stored
    let isTurned = ((props[kCGImagePropertyOrientation] as? NSNumber)?.intValue ?? 1) != 1
    let scale = min(1, longSide / max(w, h), (0.667 * longSide * longSide / (w * h)).squareRoot())
    if scale >= 1, !isTurned, let ext = takenAsIs[type] { return (nil, ext) }
    let options: [CFString: Any] = [
        kCGImageSourceCreateThumbnailFromImageAlways: true,
        kCGImageSourceCreateThumbnailWithTransform: true,
        kCGImageSourceThumbnailMaxPixelSize: max(1, Int(max(w, h) * scale)),
    ]
    guard let image = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) else { return nil }
    let isClear = type == UTType.png.identifier || type == UTType.gif.identifier
        || (props[kCGImagePropertyHasAlpha] as? Bool ?? false)
    let format = isClear ? UTType.png : UTType.jpeg
    let out = NSMutableData()
    guard let target = CGImageDestinationCreateWithData(out, format.identifier as CFString, 1, nil) else { return nil }
    CGImageDestinationAddImage(target, image, [kCGImageDestinationLossyCompressionQuality: 0.85] as CFDictionary)
    guard CGImageDestinationFinalize(target) else { return nil }
    return (out as Data, isClear ? "png" : "jpg")
}

// the phone's file name kept to letters, digits, dots, dashes and underscores: a bare name, visible, 80 characters at most
func safeFileName(_ raw: String) -> String {
    let last = (raw as NSString).lastPathComponent
    let kept = String(last.unicodeScalars.map { scalar -> Character in
        CharacterSet.alphanumerics.contains(scalar) || "._-".unicodeScalars.contains(scalar) ? Character(scalar) : "-"
    })
    var name = kept.replacingOccurrences(of: "-{2,}", with: "-", options: .regularExpression)
        .trimmingCharacters(in: CharacterSet(charactersIn: ".-"))
    if name.isEmpty { name = "file" }
    guard name.count > 80 else { return name }
    let ext = (name as NSString).pathExtension
    let stem = (name as NSString).deletingPathExtension
    return ext.isEmpty || ext.count > 10 ? String(name.prefix(80)) : String(stem.prefix(79 - ext.count)) + "." + ext
}

// saves the file under its own name, a number added where the name is taken, and returns the name it got
func saveUpload(_ data: Data, named raw: String, ext: String?, in folder: String) throws -> String {
    let saved = try freeName(raw, ext: ext, in: folder)
    try data.write(to: URL(fileURLWithPath: folder + "/" + saved), options: .atomic)
    return saved
}

// the same for a file the server streamed to the spool, moved into place
func moveUpload(_ file: String, named raw: String, ext: String?, in folder: String) throws -> String {
    let saved = try freeName(raw, ext: ext, in: folder)
    try FileManager.default.moveItem(atPath: file, toPath: folder + "/" + saved)
    return saved
}

// the file's own name in the folder, a number added where the name is taken
private func freeName(_ raw: String, ext: String?, in folder: String) throws -> String {
    var name = safeFileName(raw)
    if let ext { name = (name as NSString).deletingPathExtension + "." + ext }
    try FileManager.default.createDirectory(atPath: folder, withIntermediateDirectories: true)
    let stem = (name as NSString).deletingPathExtension
    let suffix = (name as NSString).pathExtension
    var saved = name
    var count = 2
    while FileManager.default.fileExists(atPath: folder + "/" + saved) {
        saved = suffix.isEmpty ? "\(stem)-\(count)" : "\(stem)-\(count).\(suffix)"
        count += 1
    }
    return saved
}
