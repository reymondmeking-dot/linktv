import CoreMedia
import Foundation
import ReplayKit

final class LinkBroadcast: RPBroadcastSampleHandler {
    private let queue = DispatchQueue(label: "LinkTV.Broadcast")
    private var video: AppleScreenVideo?
    private var audio: AppleCapturedPCM?
    private var request: BroadcastExchange.Request?
    private var connection: LinkWire?
    private var timer: DispatchSourceTimer?
    private var approved = false
    private var rejected = false
    private var sending = false
    private var ending = false

    override func broadcastStarted(withSetupInfo setupInfo: [String: NSObject]?) {
        queue.async { self.start() }
    }

    private func start() {
        guard let request = BroadcastExchange.read(BroadcastExchange.Request.self, "request.json"),
              Date().timeIntervalSince(request.date) >= 0,
              Date().timeIntervalSince(request.date) < 120 else {
            end("请先在双向投屏中选择接收设备。")
            return
        }
        self.request = request
        write("connecting")
        let wire = LinkWire(endpoint: request.endpoint, localName: request.localName, localCapabilities: 1) { [weak self] event in
            self?.queue.async { [weak self] in self?.handle(event) }
        }
        connection = wire
        let poll = DispatchSource.makeTimerSource(queue: queue)
        poll.schedule(deadline: .now(), repeating: .milliseconds(250))
        poll.setEventHandler { [weak self] in self?.pollCommand() }
        timer = poll
        poll.resume()
        wire.start()
    }

    private func handle(_ event: LinkWire.Event) {
        switch event {
        case let .pairing(name, code): write("pairing", name: name, code: code)
        case .connected: startVideo()
        case let .message(type, _): handleRemoteControl(type)
        case let .disconnected(reason):
            if !ending { end(rejected ? "已取消配对。" : (reason ?? "投屏连接已结束。")) }
        }
    }

    private func startVideo() {
        guard let wire = connection else { return }
        let sender = AppleScreenVideo(wire: wire)
        video = sender
        if request?.shareSound == true {
            audio = AppleCapturedPCM(send: sender.sendCapturedAudio)
        }
        sending = true
        sender.enable()
        write("casting")
    }

    private func handleRemoteControl(_ type: UInt8) {
        if type == 0x10 {
            end("对方开始投屏，已停止本机分享。")
        } else if type == 0x14 {
            end("接收设备已停止观看。")
        }
    }

    private func pollCommand() {
        guard let command = BroadcastExchange.read(BroadcastExchange.Command.self, "command.json"),
              command.id == request?.id else { return }
        switch command.action {
        case "approve" where !approved:
            approved = true
            connection?.respond(approved: true)
            write("confirming")
        case "reject" where !approved && !rejected:
            rejected = true
            connection?.respond(approved: false)
        case "stop": end("已停止手机投屏。")
        default: break
        }
    }

    override func processSampleBuffer(_ sampleBuffer: CMSampleBuffer, with sampleBufferType: RPSampleBufferType) {
        // ReplayKit waits for this callback. Processing here avoids retaining queued sample buffers.
        queue.sync {
            guard sending else { return }
            switch sampleBufferType {
            case .video:
                let value = CMGetAttachment(sampleBuffer, key: RPVideoSampleOrientationKey as CFString, attachmentModeOut: nil) as? NSNumber
                video?.offer(sampleBuffer, orientation: value?.int32Value)
            case .audioApp:
                audio?.offer(sampleBuffer)
            case .audioMic:
                break
            @unknown default:
                break
            }
        }
    }

    override func broadcastFinished() {
        queue.async { self.cleanUp() }
    }

    private func write(_ phase: String, name: String? = nil, code: String? = nil, reason: String? = nil) {
        guard let id = request?.id else { return }
        try? BroadcastExchange.write(BroadcastExchange.Status(id: id, phase: phase, name: name, code: code, reason: reason), "status.json")
    }

    private func end(_ text: String) {
        guard !ending else { return }
        cleanUp(reason: text)
        finishBroadcastWithError(NSError(domain: "LinkTV.Broadcast", code: 1, userInfo: [NSLocalizedDescriptionKey: text]))
    }

    private func cleanUp(reason: String? = nil) {
        guard !ending else { return }
        ending = true
        video?.close(notifyPeer: sending)
        video = nil
        audio = nil
        sending = false
        timer?.cancel()
        timer = nil
        connection?.close()
        connection = nil
        write("stopped", reason: reason)
    }
}
