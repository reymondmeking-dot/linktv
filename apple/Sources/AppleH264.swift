import AVFoundation
import CoreImage
import CoreMedia
import CoreVideo
import Foundation
import VideoToolbox

private let startCode = Data([0, 0, 0, 1])

private func annexBNals(_ data: Data) -> [Data] {
    let bytes = [UInt8](data)
    var units: [Data] = []
    var begin: Int?
    var cursor = 0
    while cursor + 2 < bytes.count {
        if bytes[cursor] == 0 && bytes[cursor + 1] == 0 {
            let length = bytes[cursor + 2] == 1 ? 3
                : cursor + 3 < bytes.count && bytes[cursor + 2] == 0 && bytes[cursor + 3] == 1 ? 4 : 0
            if length > 0 {
                if let begin, begin < cursor { units.append(Data(bytes[begin..<cursor])) }
                cursor += length
                begin = cursor
                continue
            }
        }
        cursor += 1
    }
    if let begin, begin < bytes.count { units.append(Data(bytes[begin...])) }
    return units
}

private func avcFormat(sps: Data, pps: Data) -> CMVideoFormatDescription? {
    sps.withUnsafeBytes { (first: UnsafeRawBufferPointer) in
        pps.withUnsafeBytes { (second: UnsafeRawBufferPointer) in
            guard let a = first.baseAddress, let b = second.baseAddress else { return nil }
            let parameterPointers = [a.assumingMemoryBound(to: UInt8.self), b.assumingMemoryBound(to: UInt8.self)]
            let lengths = [sps.count, pps.count]
            var format: CMFormatDescription?
            let status = CMVideoFormatDescriptionCreateFromH264ParameterSets(allocator: nil,
                parameterSetCount: 2, parameterSetPointers: parameterPointers, parameterSetSizes: lengths,
                nalUnitHeaderLength: 4, formatDescriptionOut: &format)
            return status == noErr ? format : nil
        }
    }
}

final class AppleVideoDisplay {
    let layer = AVSampleBufferDisplayLayer()
    private let lock = NSLock()
    private var active = false
    private var waitingForKey = true
    private var format: CMVideoFormatDescription?

    init() { layer.videoGravity = .resizeAspect }

    func begin() {
        lock.lock()
        defer { lock.unlock() }
        active = true
        waitingForKey = true
        format = nil
        layer.flushAndRemoveImage()
    }

    func stop() {
        lock.lock()
        defer { lock.unlock() }
        active = false
        format = nil
        layer.flushAndRemoveImage()
    }

    func offer(_ packet: Data) {
        guard packet.count > 9 else { return }
        let header = Array(packet.prefix(9))
        let timestamp = header.prefix(8).reduce(UInt64(0)) { ($0 << 8) | UInt64($1) }
        guard timestamp <= UInt64(Int64.max) else { return }
        let key = header[8] == 1
        lock.lock()
        defer { lock.unlock() }
        guard active else { return }
        if layer.status == .failed { waitingForKey = true }
        guard key || !waitingForKey else { return }
        if key && waitingForKey { layer.flush() }
        guard layer.isReadyForMoreMediaData else {
            waitingForKey = true
            return
        }

        let nals = annexBNals(Data(packet.dropFirst(9)))
        if key {
            guard let sps = nals.first(where: { ($0.first ?? 0) & 31 == 7 }),
                  let pps = nals.first(where: { ($0.first ?? 0) & 31 == 8 }),
                  let next = avcFormat(sps: sps, pps: pps) else { return }
            let geometry = CMVideoFormatDescriptionGetDimensions(next)
            guard (2...1920).contains(geometry.width), (2...1920).contains(geometry.height),
                  Int(geometry.width) * Int(geometry.height) <= 1920 * 1088 else { return }
            format = next
            waitingForKey = false
        }
        guard let format else { return }
        var body = Data()
        for unit in nals where ![7, 8, 9].contains((unit.first ?? 0) & 31) {
            var length = UInt32(unit.count).bigEndian
            withUnsafeBytes(of: &length) { body.append(contentsOf: $0) }
            body.append(unit)
        }
        guard !body.isEmpty else { return }
        var block: CMBlockBuffer?
        guard CMBlockBufferCreateWithMemoryBlock(allocator: nil, memoryBlock: nil, blockLength: body.count,
            blockAllocator: kCFAllocatorDefault, customBlockSource: nil, offsetToData: 0,
            dataLength: body.count, flags: 0, blockBufferOut: &block) == noErr, let block else { return }
        guard body.withUnsafeBytes({ CMBlockBufferReplaceDataBytes(with: $0.baseAddress!, blockBuffer: block,
            offsetIntoDestination: 0, dataLength: body.count) }) == noErr else { return }
        var timing = CMSampleTimingInfo(duration: .invalid,
            presentationTimeStamp: CMTime(value: Int64(timestamp), timescale: 1_000_000), decodeTimeStamp: .invalid)
        var size = body.count
        var sample: CMSampleBuffer?
        guard CMSampleBufferCreateReady(allocator: nil, dataBuffer: block, formatDescription: format,
            sampleCount: 1, sampleTimingEntryCount: 1, sampleTimingArray: &timing,
            sampleSizeEntryCount: 1, sampleSizeArray: &size, sampleBufferOut: &sample) == noErr,
            let sample, let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: true),
            let item = CFArrayGetValueAtIndex(attachments, 0) else { return }
        let dictionary = unsafeBitCast(item, to: CFMutableDictionary.self)
        CFDictionarySetValue(dictionary, Unmanaged.passUnretained(kCMSampleAttachmentKey_DisplayImmediately).toOpaque(),
            Unmanaged.passUnretained(kCFBooleanTrue).toOpaque())
        layer.enqueue(sample)
    }
}

private final class AppleAVCEncoder {
    let width: Int
    let height: Int
    private let wire: LinkWire
    private let context = CIContext(options: [.cacheIntermediates: false])
    private let health = NSLock()
    private var session: VTCompressionSession?
    private var failed = false
    private var startedAt: Double?
    private var previousPTS: Int64 = -1

    init?(width: Int, height: Int, wire: LinkWire) {
        self.width = width
        self.height = height
        self.wire = wire
        let attrs = [kCVPixelBufferPixelFormatTypeKey: NSNumber(value: kCVPixelFormatType_32BGRA),
                     kCVPixelBufferWidthKey: NSNumber(value: width),
                     kCVPixelBufferHeightKey: NSNumber(value: height)] as CFDictionary
        var created: VTCompressionSession?
        #if os(macOS)
        let specification = [kVTVideoEncoderSpecification_RequireHardwareAcceleratedVideoEncoder: kCFBooleanTrue] as CFDictionary
        #else
        let specification: CFDictionary? = nil
        #endif
        let creation = VTCompressionSessionCreate(allocator: nil, width: Int32(width), height: Int32(height),
            codecType: kCMVideoCodecType_H264, encoderSpecification: specification, imageBufferAttributes: attrs,
            compressedDataAllocator: nil, outputCallback: { context, _, status, _, sample in
                guard let context else { return }
                let owner = Unmanaged<AppleAVCEncoder>.fromOpaque(context).takeUnretainedValue()
                if status != noErr { owner.markFailure() }
                if status == noErr, let sample { owner.output(sample) }
            }, refcon: Unmanaged.passUnretained(self).toOpaque(), compressionSessionOut: &created)
        guard creation == noErr, let created else { return nil }
        let properties: [(CFString, CFTypeRef)] = [
            (kVTCompressionPropertyKey_RealTime, kCFBooleanTrue),
            (kVTCompressionPropertyKey_AllowFrameReordering, kCFBooleanFalse),
            (kVTCompressionPropertyKey_ExpectedFrameRate, NSNumber(value: 60)),
            (kVTCompressionPropertyKey_MaxKeyFrameInterval, NSNumber(value: 60)),
            (kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration, NSNumber(value: 1)),
            (kVTCompressionPropertyKey_AverageBitRate, NSNumber(value: 12_000_000))
        ]
        for (key, value) in properties {
            if VTSessionSetProperty(created, key: key, value: value) != noErr {
                VTCompressionSessionInvalidate(created)
                return nil
            }
        }
        guard VTCompressionSessionPrepareToEncodeFrames(created) == noErr else {
            VTCompressionSessionInvalidate(created)
            return nil
        }
        session = created
    }

    func encode(_ image: CIImage, now: Double) -> Bool {
        health.lock()
        let healthy = !failed
        health.unlock()
        guard healthy, let session, let pool = VTCompressionSessionGetPixelBufferPool(session) else { return false }
        var target: CVPixelBuffer?
        guard CVPixelBufferPoolCreatePixelBuffer(nil, pool, &target) == kCVReturnSuccess, let target else { return false }
        let origin = startedAt ?? now
        startedAt = origin
        let pts = max(previousPTS + 1, Int64((now - origin) * 1_000_000))
        previousPTS = pts
        let extent = image.extent
        let pixels = image.transformed(by: CGAffineTransform(translationX: -extent.minX, y: -extent.minY))
            .transformed(by: CGAffineTransform(scaleX: CGFloat(width) / extent.width, y: CGFloat(height) / extent.height))
        context.render(pixels, to: target)
        return VTCompressionSessionEncodeFrame(session, imageBuffer: target,
            presentationTimeStamp: CMTime(value: pts, timescale: 1_000_000), duration: CMTime(value: 1, timescale: 60),
            frameProperties: nil, sourceFrameRefcon: nil, infoFlagsOut: nil) == noErr
    }

    private func output(_ sample: CMSampleBuffer) {
        guard let block = CMSampleBufferGetDataBuffer(sample), let format = CMSampleBufferGetFormatDescription(sample) else { return }
        let length = CMBlockBufferGetDataLength(block)
        guard length > 0, length <= 2_097_000 else { return }
        var avcc = [UInt8](repeating: 0, count: length)
        guard avcc.withUnsafeMutableBytes({ CMBlockBufferCopyDataBytes(block, atOffset: 0,
            dataLength: length, destination: $0.baseAddress!) }) == noErr else { return }
        var headerLength: Int32 = 0
        var parameterCount = 0
        guard CMVideoFormatDescriptionGetH264ParameterSetAtIndex(format, parameterSetIndex: 0,
            parameterSetPointerOut: nil, parameterSetSizeOut: nil, parameterSetCountOut: &parameterCount,
            nalUnitHeaderLengthOut: &headerLength) == noErr, (1...4).contains(headerLength) else { return }
        var cursor = 0
        var frame = Data()
        var key = false
        let header = Int(headerLength)
        while cursor + header <= length {
            let size = avcc[cursor..<(cursor + header)].reduce(0) { ($0 << 8) | Int($1) }
            cursor += header
            guard size > 0, size <= length - cursor else { return }
            key = key || avcc[cursor] & 31 == 5
            frame.append(startCode)
            frame.append(contentsOf: avcc[cursor..<(cursor + size)])
            cursor += size
        }
        guard cursor == length else { return }
        if key {
            var prefix = Data()
            for index in 0..<parameterCount {
                var pointer: UnsafePointer<UInt8>?
                var size = 0
                if CMVideoFormatDescriptionGetH264ParameterSetAtIndex(format, parameterSetIndex: index,
                    parameterSetPointerOut: &pointer, parameterSetSizeOut: &size, parameterSetCountOut: nil,
                    nalUnitHeaderLengthOut: nil) == noErr, let pointer, size > 0 {
                    prefix.append(startCode)
                    prefix.append(Data(bytes: pointer, count: size))
                }
            }
            prefix.append(frame)
            frame = prefix
        }
        let pts = CMTimeConvertScale(CMSampleBufferGetPresentationTimeStamp(sample), timescale: 1_000_000, method: .default).value
        if !wire.sendH264Frame(ptsUs: pts, keyFrame: key, accessUnit: frame) { markFailure() }
    }

    private func markFailure() {
        health.lock()
        failed = true
        health.unlock()
    }

    func close() {
        if let session {
            VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: .invalid)
            VTCompressionSessionInvalidate(session)
            self.session = nil
        }
    }

    deinit { close() }
}

final class AppleScreenVideo {
    private let wire: LinkWire
    private let fallback = ScreenJPEG()
    private let lock = NSLock()
    private var encoder: AppleAVCEncoder?
    private var active = false
    private var finished = false
    private var h264: Bool
    private var nextFrameTime: Double?

    init(wire: LinkWire) {
        self.wire = wire
        self.h264 = wire.canSendH264
    }

    var fps: Int { h264 ? 60 : 20 }

    func enable() {
        lock.lock()
        defer { lock.unlock() }
        guard !finished, !active else { return }
        active = true
        wire.send(type: 0x10)
    }

    func offer(_ sample: CMSampleBuffer, orientation: Int32? = nil) {
        lock.lock()
        defer { lock.unlock() }
        guard active else { return }
        if h264, let buffer = CMSampleBufferGetImageBuffer(sample) {
            let now = ProcessInfo.processInfo.systemUptime
            let interval = 1.0 / 60.0
            let deadline = nextFrameTime ?? now
            guard now >= deadline else { return }
            nextFrameTime = deadline + (floor((now - deadline) / interval) + 1) * interval
            let source = CIImage(cvPixelBuffer: buffer)
            let image = orientation.map { source.oriented(forExifOrientation: $0) } ?? source
            let size = image.extent.size
            guard size.width >= 2, size.height >= 2 else { return }
            let scale = min(1, 1920 / max(size.width, size.height), 1080 / min(size.width, size.height))
            let width = Int(size.width * scale) & ~1
            let height = Int(size.height * scale) & ~1
            guard width >= 2, height >= 2 else { return }
            if encoder?.width != width || encoder?.height != height {
                if let encoder {
                    encoder.close()
                    wire.send(type: 0x11)
                    wire.send(type: 0x10)
                }
                encoder = AppleAVCEncoder(width: width, height: height, wire: wire)
                if let encoder, !wire.sendH264Begin(width: width, height: height, fps: 60) {
                    encoder.close()
                    self.encoder = nil
                }
            }
            if let encoder, encoder.encode(image, now: now) { return }
            if let encoder {
                encoder.close()
                self.encoder = nil
                wire.send(type: 0x11)
                wire.send(type: 0x10)
            }
            h264 = false
        }
        if let jpeg = fallback.encode(sample, orientation: orientation) { wire.send(type: 0x12, payload: jpeg) }
    }

    func sendCapturedAudio(_ pcm: Data) {
        lock.lock()
        defer { lock.unlock() }
        if active { wire.send(type: 0x13, payload: pcm) }
    }

    func close(notifyPeer: Bool) {
        lock.lock()
        defer { lock.unlock() }
        finished = true
        encoder?.close()
        encoder = nil
        if active && notifyPeer { wire.send(type: 0x11) }
        active = false
    }
}
