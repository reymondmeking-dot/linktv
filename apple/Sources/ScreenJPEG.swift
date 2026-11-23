import CoreImage
import CoreMedia
import Foundation
import ImageIO

final class ScreenJPEG {
    private let context = CIContext(options: [.cacheIntermediates: false])
    private let colourSpace = CGColorSpace(name: CGColorSpace.sRGB)!
    private var previous = -Double.infinity

    func encode(_ buffer: CMSampleBuffer, orientation: Int32? = nil) -> Data? {
        guard let pixels = CMSampleBufferGetImageBuffer(buffer) else { return nil }
        let now = ProcessInfo.processInfo.systemUptime
        guard now - previous >= 0.05 else { return nil }
        previous = now
        let input = CIImage(cvPixelBuffer: pixels)
        let image = orientation.map { input.oriented(forExifOrientation: $0) } ?? input
        let extent = image.extent
        guard extent.width > 0, extent.height > 0 else { return nil }
        let scale = min(1, 1920 / max(extent.width, extent.height), 1080 / min(extent.width, extent.height))
        let resized = image.transformed(by: CGAffineTransform(scaleX: scale, y: scale))
        return jpeg(resized, quality: 0.90) ?? jpeg(resized, quality: 0.75)
    }

    private func jpeg(_ image: CIImage, quality: CGFloat) -> Data? {
        let key = CIImageRepresentationOption(rawValue: kCGImageDestinationLossyCompressionQuality as String)
        guard let data = context.jpegRepresentation(of: image, colorSpace: colourSpace, options: [key: quality]),
              data.count <= 2_097_088 else { return nil }
        return data
    }
}
