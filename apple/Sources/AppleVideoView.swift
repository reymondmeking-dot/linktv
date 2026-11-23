import AVFoundation
import QuartzCore
import SwiftUI

#if os(iOS)
import UIKit

struct AppleVideoView: UIViewRepresentable {
    let video: AVSampleBufferDisplayLayer
    func makeUIView(context: Context) -> VideoHostView { VideoHostView(video) }
    func updateUIView(_ view: VideoHostView, context: Context) { view.install(video) }
}

final class VideoHostView: UIView {
    private var video: AVSampleBufferDisplayLayer
    init(_ video: AVSampleBufferDisplayLayer) {
        self.video = video
        super.init(frame: .zero)
        install(video)
    }
    required init?(coder: NSCoder) { return nil }

    func install(_ next: AVSampleBufferDisplayLayer) {
        if video !== next { video.removeFromSuperlayer(); video = next }
        if video.superlayer !== layer { layer.addSublayer(video) }
        setNeedsLayout()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        resizeVideo(video, to: bounds)
    }
}
#else
import AppKit

struct AppleVideoView: NSViewRepresentable {
    let video: AVSampleBufferDisplayLayer
    func makeNSView(context: Context) -> VideoHostView { VideoHostView(video) }
    func updateNSView(_ view: VideoHostView, context: Context) { view.install(video) }
}

final class VideoHostView: NSView {
    private var video: AVSampleBufferDisplayLayer
    init(_ video: AVSampleBufferDisplayLayer) {
        self.video = video
        super.init(frame: .zero)
        wantsLayer = true
        install(video)
    }
    required init?(coder: NSCoder) { return nil }

    func install(_ next: AVSampleBufferDisplayLayer) {
        if video !== next { video.removeFromSuperlayer(); video = next }
        if let layer, video.superlayer !== layer { layer.addSublayer(video) }
        needsLayout = true
    }

    override func layout() {
        super.layout()
        resizeVideo(video, to: bounds)
    }
}
#endif

private func resizeVideo(_ video: AVSampleBufferDisplayLayer, to bounds: CGRect) {
    guard video.frame != bounds else { return }
    CATransaction.begin()
    CATransaction.setDisableActions(true)
    video.frame = bounds
    CATransaction.commit()
}
