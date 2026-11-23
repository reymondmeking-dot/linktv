import AVFoundation
import CoreGraphics
import Foundation
import ImageIO

final class IncomingMedia {
    private let lock = NSLock()
    private let decoder = DispatchQueue(label: "LinkTV.image-decode")
    private let audio = ApplePCMPlayback()
    private let present: @MainActor (CGImage) -> Void
    private var receiving = false
    private var audible = true
    private var generation = 0
    private var queuedJPEG: (generation: Int, data: Data)?
    private var queuedPicture: (generation: Int, image: CGImage)?
    private var decoding = false
    private var displaying = false

    init(present: @escaping @MainActor (CGImage) -> Void) {
        self.present = present
    }

    func start() { setReceiving(true) }
    func stop() { setReceiving(false) }

    private func setReceiving(_ on: Bool) {
        lock.lock()
        receiving = on
        generation += 1
        queuedJPEG = nil
        queuedPicture = nil
        audio.stop()
        lock.unlock()
    }

    func setAudible(_ on: Bool) {
        lock.lock()
        audible = on
        if !on { audio.stop() }
        lock.unlock()
    }

    func offerJPEG(_ jpeg: Data) {
        lock.lock()
        defer { lock.unlock() }
        guard receiving else { return }
        queuedJPEG = (generation, jpeg)
        if !decoding {
            decoding = true
            decoder.async { [weak self] in self?.decodeLatest() }
        }
    }

    func offerPCM(_ pcm: Data) {
        lock.lock()
        defer { lock.unlock() }
        if receiving && audible { audio.offer(pcm) }
    }

    private func nextJPEG() -> (generation: Int, data: Data)? {
        lock.lock()
        defer { lock.unlock() }
        guard let frame = queuedJPEG else {
            decoding = false
            return nil
        }
        queuedJPEG = nil
        return frame
    }

    private func decodeLatest() {
        while let frame = nextJPEG() {
            guard let picture = Self.decodeJPEG(frame.data) else { continue }
            lock.lock()
            if frame.generation == generation && receiving {
                queuedPicture = (frame.generation, picture)
                if !displaying {
                    displaying = true
                    DispatchQueue.main.async { [weak self] in self?.showLatest() }
                }
            }
            lock.unlock()
        }
    }

    @MainActor
    private func showLatest() {
        lock.lock()
        let picture = queuedPicture
        queuedPicture = nil
        displaying = false
        let current = generation
        lock.unlock()
        if let picture, picture.generation == current { present(picture.image) }
    }

    private static func decodeJPEG(_ data: Data) -> CGImage? {
        guard data.starts(with: [0xff, 0xd8]),
              let source = CGImageSourceCreateWithData(data as CFData, nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [String: Any],
              let width = properties[kCGImagePropertyPixelWidth as String] as? Int,
              let height = properties[kCGImagePropertyPixelHeight as String] as? Int,
              (1...4096).contains(width), (1...4096).contains(height),
              width * height <= 4096 * 2160 else { return nil }
        let options = [kCGImageSourceShouldCacheImmediately as String: true] as CFDictionary
        return CGImageSourceCreateImageAtIndex(source, 0, options)
    }
}

private final class ApplePCMPlayback {
    private static let maxQueuedBytes = 32_768
    private static let maxPacketBytes = 16_384
    private let lock = NSLock()
    private let worker = DispatchQueue(label: "LinkTV.PCM-playback")
    private var blocks: [Data] = []
    private var queuedBytes = 0
    private var scheduled = false
    private var generation = 0

    // The worker alone owns the audio engine and its scheduled buffer count.
    private var engine: AVAudioEngine?
    private var player: AVAudioPlayerNode?
    private var playingGeneration = 0
    private var buffersInFlight = 0
    private var failed = false
    private let format = AVAudioFormat(standardFormatWithSampleRate: 44_100, channels: 2)
    #if os(iOS)
    private var ownsAudioSession = false
    #endif

    func offer(_ data: Data) {
        guard data.count >= 4, data.count <= Self.maxPacketBytes, data.count % 4 == 0 else { return }
        lock.lock()
        defer { lock.unlock() }
        while queuedBytes + data.count > Self.maxQueuedBytes {
            queuedBytes -= blocks.removeFirst().count
        }
        blocks.append(data)
        queuedBytes += data.count
        scheduleDrain()
    }

    func stop() {
        lock.lock()
        generation += 1
        blocks.removeAll()
        queuedBytes = 0
        scheduleDrain()
        lock.unlock()
    }

    // Called only while holding the queue lock.
    private func scheduleDrain() {
        guard !scheduled else { return }
        scheduled = true
        worker.async { [self] in drain() }
    }

    private func pop(generation expected: Int) -> Data? {
        lock.lock()
        defer { lock.unlock() }
        guard expected == generation, !blocks.isEmpty else { return nil }
        let data = blocks.removeFirst()
        queuedBytes -= data.count
        return data
    }

    private func drain() {
        lock.lock()
        scheduled = false
        let current = generation
        lock.unlock()
        if playingGeneration != current {
            stopEngine()
            playingGeneration = current
            buffersInFlight = 0
            failed = false
        }

        while !failed, buffersInFlight < 2, let data = pop(generation: current) {
            do {
                let player = try prepareEngine()
                guard let buffer = buffer(for: data) else { continue }
                buffersInFlight += 1
                player.scheduleBuffer(buffer, at: nil, options: [], completionCallbackType: .dataPlayedBack) { [weak self] _ in
                    self?.worker.async { [weak self] in
                        guard let self, self.playingGeneration == current else { return }
                        self.buffersInFlight -= 1
                        self.drain()
                    }
                }
            } catch {
                stopEngine()
                failed = true
            }
        }
    }

    private func prepareEngine() throws -> AVAudioPlayerNode {
        if let player { return player }
        guard let format else { throw CocoaError(.featureUnsupported) }
        #if os(iOS)
        let session = AVAudioSession.sharedInstance()
        try session.setCategory(.playback, mode: .moviePlayback, options: [])
        try session.setActive(true)
        ownsAudioSession = true
        #endif
        let engine = AVAudioEngine()
        let player = AVAudioPlayerNode()
        engine.attach(player)
        engine.connect(player, to: engine.mainMixerNode, format: format)
        try engine.start()
        player.play()
        self.engine = engine
        self.player = player
        return player
    }

    private func stopEngine() {
        player?.stop()
        engine?.stop()
        player = nil
        engine = nil
        #if os(iOS)
        if ownsAudioSession {
            try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
            ownsAudioSession = false
        }
        #endif
    }

    private func buffer(for data: Data) -> AVAudioPCMBuffer? {
        let frames = data.count / 4
        guard let format,
              let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frames)),
              let channels = buffer.floatChannelData else { return nil }
        buffer.frameLength = AVAudioFrameCount(frames)
        data.withUnsafeBytes { (bytes: UnsafeRawBufferPointer) in
            for frame in 0..<frames {
                let offset = frame * 4
                let left = UInt16(bytes[offset]) | (UInt16(bytes[offset + 1]) << 8)
                let right = UInt16(bytes[offset + 2]) | (UInt16(bytes[offset + 3]) << 8)
                channels[0][frame] = Float(Int16(bitPattern: left)) / 32_768
                channels[1][frame] = Float(Int16(bitPattern: right)) / 32_768
            }
        }
        return buffer
    }
}
