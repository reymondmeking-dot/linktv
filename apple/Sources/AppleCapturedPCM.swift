import AudioToolbox
import AVFoundation
import CoreMedia
import Foundation

// A capture callback queue owns one converter. Reuse it across consecutive buffers
// so its resampling filter continues at the boundary between callbacks.
final class AppleCapturedPCM {
    private static let maxPacketBytes = 16_384
    private static let outputRate = 44_100.0
    private let outputFormat = AVAudioFormat(commonFormat: .pcmFormatInt16,
        sampleRate: AppleCapturedPCM.outputRate, channels: 2, interleaved: true)
    private let send: (Data) -> Void
    private var converter: AVAudioConverter?
    private var input: AVAudioPCMBuffer?
    private var output: AVAudioPCMBuffer?

    init(send: @escaping (Data) -> Void) {
        self.send = send
    }

    func offer(_ sample: CMSampleBuffer) {
        let frames = CMSampleBufferGetNumSamples(sample)
        guard (1...16_384).contains(frames), CMSampleBufferDataIsReady(sample),
              let description = CMSampleBufferGetFormatDescription(sample),
              let stream = CMAudioFormatDescriptionGetStreamBasicDescription(description),
              stream.pointee.mFormatID == kAudioFormatLinearPCM,
              let format = AVAudioFormat(streamDescription: stream),
              (8_000...192_000).contains(format.sampleRate),
              (1...2).contains(format.channelCount),
              let outputFormat else { return }

        if converter?.inputFormat != format {
            converter = AVAudioConverter(from: format, to: outputFormat)
            if format.channelCount == 1 {
                converter?.channelMap = [NSNumber(value: 0), NSNumber(value: 0)]
            }
            input = nil
        }
        guard let converter else { return }
        if input == nil || Int(input?.frameCapacity ?? 0) < frames {
            input = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frames))
        }
        let capacity = ceil(Double(frames) * Self.outputRate / format.sampleRate) + 64
        if output == nil || (output?.frameCapacity ?? 0) < AVAudioFrameCount(capacity) {
            output = AVAudioPCMBuffer(pcmFormat: outputFormat, frameCapacity: AVAudioFrameCount(capacity))
        }
        guard let input, let output else { return }

        var consumed = 0
        var copyFailed = false
        let result = converter.convert(to: output, error: nil) { requested, status in
            let count = min(frames - consumed, Int(requested))
            guard count > 0, !copyFailed else {
                status.pointee = .noDataNow
                return nil
            }
            input.frameLength = AVAudioFrameCount(count)
            guard CMSampleBufferCopyPCMDataIntoAudioBufferList(sample, at: Int32(consumed),
                frameCount: Int32(count), into: input.mutableAudioBufferList) == noErr else {
                copyFailed = true
                status.pointee = .noDataNow
                return nil
            }
            consumed += count
            status.pointee = .haveData
            return input
        }
        if copyFailed || result == .error {
            converter.reset()
            return
        }
        let buffer = output.audioBufferList.pointee.mBuffers
        let size = Int(output.frameLength) * 4
        guard size > 0, size <= Int(buffer.mDataByteSize), let bytes = buffer.mData else { return }
        // All supported iPhone and Mac architectures store Int16 in little-endian order.
        for offset in stride(from: 0, to: size, by: Self.maxPacketBytes) {
            send(Data(bytes: bytes.advanced(by: offset), count: min(Self.maxPacketBytes, size - offset)))
        }
    }
}
