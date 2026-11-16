import CoreImage
import Foundation
// reads every QR code in the PNG given, prints its text
let url = URL(fileURLWithPath: CommandLine.arguments[1])
guard let image = CIImage(contentsOf: url),
      let detector = CIDetector(ofType: CIDetectorTypeQRCode, context: nil, options: [CIDetectorAccuracy: CIDetectorAccuracyHigh]) else { print("unreadable"); exit(1) }
let found = detector.features(in: image).compactMap { ($0 as? CIQRCodeFeature)?.messageString }
print(found.first ?? "none")
