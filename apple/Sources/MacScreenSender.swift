#if os(macOS)
import CoreGraphics
import CoreMedia
import CoreVideo
import Foundation
import ScreenCaptureKit

final class MacScreenSender: NSObject, SCStreamOutput, SCStreamDelegate {
    enum CaptureFailure: LocalizedError {
        case permission
        case displayUnavailable

        var errorDescription: String? {
            switch self {
            case .permission:
                return "请在「系统设置 → 隐私与安全性 → 屏幕录制」中允许双向投屏，然后重试。"
            case .displayUnavailable:
                return "没有可共享的显示器。请唤醒显示器后重试。"
            }
        }
    }

    private let video: AppleScreenVideo
    private let videoQueue = DispatchQueue(label: "LinkTV.MacCapture")
    private let audioQueue = DispatchQueue(label: "LinkTV.MacAudioCapture")
    private let audio: AppleCapturedPCM?
    private let stopped: @MainActor (MacScreenSender, String) -> Void
    @MainActor private var stream: SCStream?
    @MainActor private var cancelled = false

    init(wire: LinkWire, shareSound: Bool, stopped: @escaping @MainActor (MacScreenSender, String) -> Void) {
        let video = AppleScreenVideo(wire: wire)
        self.video = video
        self.audio = shareSound ? AppleCapturedPCM(send: video.sendCapturedAudio) : nil
        self.stopped = stopped
    }

    @MainActor
    func start() async throws {
        guard CGPreflightScreenCaptureAccess() || CGRequestScreenCaptureAccess() else {
            throw CaptureFailure.permission
        }
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard !cancelled else { throw CancellationError() }
        guard let display = content.displays.first(where: { $0.displayID == CGMainDisplayID() }) ?? content.displays.first else {
            throw CaptureFailure.displayUnavailable
        }
        let filter = SCContentFilter(display: display, excludingWindows: [])
        let configuration = SCStreamConfiguration()
        let mode = CGDisplayCopyDisplayMode(display.displayID)
        let width = Double(mode?.pixelWidth ?? display.width)
        let height = Double(mode?.pixelHeight ?? display.height)
        guard width > 0, height > 0 else { throw CaptureFailure.displayUnavailable }
        let scale = min(1, 1920 / max(width, height), 1080 / min(width, height))
        configuration.width = max(2, Int(width * scale) & ~1)
        configuration.height = max(2, Int(height * scale) & ~1)
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: Int32(video.fps))
        configuration.pixelFormat = kCVPixelFormatType_32BGRA
        configuration.queueDepth = 3
        configuration.showsCursor = true
        if audio != nil {
            configuration.capturesAudio = true
            configuration.sampleRate = 48_000
            configuration.channelCount = 2
            configuration.excludesCurrentProcessAudio = true
        }
        let capture = SCStream(filter: filter, configuration: configuration, delegate: self)
        try capture.addStreamOutput(self, type: .screen, sampleHandlerQueue: videoQueue)
        if audio != nil {
            try capture.addStreamOutput(self, type: .audio, sampleHandlerQueue: audioQueue)
        }
        try await capture.startCapture()
        if cancelled {
            try? await capture.stopCapture()
            throw CancellationError()
        }
        stream = capture
    }

    func enable() { video.enable() }

    @MainActor
    func closeVideo(notifyPeer: Bool) {
        cancelled = true
        video.close(notifyPeer: notifyPeer)
    }

    @MainActor
    func stop() async {
        closeVideo(notifyPeer: false)
        guard let current = stream else { return }
        stream = nil
        try? await current.stopCapture()
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        let reason = "系统已结束屏幕共享。可重新开始投屏。"
        Task { @MainActor [weak self] in
            guard let self, !self.cancelled else { return }
            self.stopped(self, reason)
        }
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of outputType: SCStreamOutputType) {
        if outputType == .audio {
            audio?.offer(sampleBuffer)
            return
        }
        guard outputType == .screen,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
              let rawStatus = attachments.first?[.status] as? Int,
              SCFrameStatus(rawValue: rawStatus) == .complete else { return }
        video.offer(sampleBuffer)
    }
}
#endif
