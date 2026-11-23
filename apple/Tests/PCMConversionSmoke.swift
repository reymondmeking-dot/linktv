import AudioToolbox
import AVFoundation
import CoreMedia
import Foundation

@main
struct PCMConversionSmoke {
    static func main() {
        check(format: AVAudioFormat(commonFormat: .pcmFormatFloat32,
            sampleRate: 48_000, channels: 2, interleaved: false)!, right: -0.4)
        check(format: AVAudioFormat(commonFormat: .pcmFormatFloat32,
            sampleRate: 48_000, channels: 1, interleaved: true)!, right: 0.25)
        print("48 kHz stereo and mono → 44.1 kHz interleaved stereo PCM passed")
    }

    private static func check(format: AVAudioFormat, right: Float) {
        let frames = 960
        let pcm = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frames))!
        pcm.frameLength = AVAudioFrameCount(frames)
        for channel in 0..<Int(format.channelCount) {
            let value: Float = channel == 0 ? 0.25 : right
            for i in 0..<frames { pcm.floatChannelData![channel][i] = value }
        }

        var output = Data()
        let converter = AppleCapturedPCM { packet in
            precondition(!packet.isEmpty && packet.count <= 16_384 && packet.count % 4 == 0)
            output.append(packet)
        }
        for block in 0..<8 {
            var timing = CMSampleTimingInfo(duration: CMTime(value: 1, timescale: 48_000),
                presentationTimeStamp: CMTime(value: Int64(block * frames), timescale: 48_000),
                decodeTimeStamp: .invalid)
            var sample: CMSampleBuffer?
            precondition(CMSampleBufferCreate(allocator: nil, dataBuffer: nil, dataReady: false,
                makeDataReadyCallback: nil, refcon: nil, formatDescription: format.formatDescription,
                sampleCount: frames, sampleTimingEntryCount: 1, sampleTimingArray: &timing,
                sampleSizeEntryCount: 0, sampleSizeArray: nil, sampleBufferOut: &sample) == noErr)
            let buffer = sample!
            precondition(CMSampleBufferSetDataBufferFromAudioBufferList(buffer,
                blockBufferAllocator: nil, blockBufferMemoryAllocator: nil,
                flags: 0, bufferList: pcm.audioBufferList) == noErr)
            precondition(CMSampleBufferSetDataReady(buffer) == noErr)
            converter.offer(buffer)
        }

        let outputFrames = output.count / 4
        precondition(abs(outputFrames - 7_056) < 150, "Incorrect resampled frame count \(outputFrames)")
        let center = (outputFrames / 2) * 4
        let leftSample = Int(Int16(bitPattern: UInt16(output[center]) | UInt16(output[center + 1]) << 8))
        let rightSample = Int(Int16(bitPattern: UInt16(output[center + 2]) | UInt16(output[center + 3]) << 8))
        precondition(abs(leftSample - 8_192) < 256 && abs(rightSample - Int(right * 32_768)) < 256,
            "Incorrect stereo samples \(leftSample), \(rightSample)")
    }
}
