#if os(macOS)
import CoreGraphics
import CoreMedia
import CoreVideo
import Foundation
import ScreenCaptureKit

final class MacScreenSender: NSObject, SCStreamOutput {
    private let video: AppleScreenVideo
    private let queue = DispatchQueue(label: "LinkTV.MacCapture")
    private var stream: SCStream?

    init(wire: LinkWire) { self.video = AppleScreenVideo(wire: wire) }

    func start() async throws {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first(where: { $0.displayID == CGMainDisplayID() }) ?? content.displays.first else {
            throw CocoaError(.featureUnsupported)
        }
        let filter = SCContentFilter(display: display, excludingWindows: [])
        let configuration = SCStreamConfiguration()
        let mode = CGDisplayCopyDisplayMode(display.displayID)
        let width = Double(mode?.pixelWidth ?? display.width)
        let height = Double(mode?.pixelHeight ?? display.height)
        let scale = min(1, 1920 / max(width, height), 1080 / min(width, height))
        configuration.width = max(2, Int(width * scale) & ~1)
        configuration.height = max(2, Int(height * scale) & ~1)
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: Int32(video.fps))
        configuration.pixelFormat = kCVPixelFormatType_32BGRA
        configuration.queueDepth = 3
        configuration.showsCursor = true
        let capture = SCStream(filter: filter, configuration: configuration, delegate: nil)
        try capture.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
        try await capture.startCapture()
        stream = capture
    }

    func enable() { video.enable() }

    func closeVideo(notifyPeer: Bool) { video.close(notifyPeer: notifyPeer) }

    func stop() async {
        video.close(notifyPeer: false)
        guard let current = stream else { return }
        stream = nil
        try? await current.stopCapture()
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of outputType: SCStreamOutputType) {
        guard outputType == .screen,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let rawStatus = attachments.first?[.status] as? Int,
              SCFrameStatus(rawValue: rawStatus) == .complete else { return }
        video.offer(sampleBuffer)
    }
}
#endif
