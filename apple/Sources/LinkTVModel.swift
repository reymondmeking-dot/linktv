import CoreGraphics
import AVFoundation
import Foundation
import ImageIO
import Network
import SwiftUI
#if os(iOS)
import UIKit
#endif

struct NearbyComputer: Identifiable, Equatable {
    let id: String
    let name: String
    let endpoint: NWEndpoint
}

struct PairingPrompt: Identifiable {
    let id = UUID()
    let name: String
    let number: String
}

@MainActor
final class LinkTVModel: ObservableObject {
    enum Phase: Equatable {
        case idle, connecting, pairing, confirming, cancelling, connected, receiving
        case startingCapture, sending
        case broadcastReady, broadcastConnecting, broadcasting, broadcastStopping
    }

    @Published private(set) var computers: [NearbyComputer] = []
    @Published private(set) var phase: Phase = .idle
    @Published private(set) var peerName = ""
    @Published private(set) var prompt: PairingPrompt?
    @Published private(set) var frame: CGImage?
    @Published private(set) var h264Layer: AVSampleBufferDisplayLayer?
    @Published private(set) var message: String?
    @Published private(set) var discoveryMessage = "正在搜索附近的设备…"

    private var browser: NWBrowser?
    private var browserID = UUID()
    private var wire: LinkWire?
    private var sessionID: UUID?
    private var media: IncomingMedia?
    private var avcDisplay: AppleVideoDisplay?
    private var audioVisible = true
    #if os(macOS)
    private var capture: MacScreenSender?
    #endif
    #if os(iOS)
    private var broadcast: BroadcastExchange.Request?
    private var broadcastPoll: Task<Void, Never>?
    private var broadcastStopRequestedAt: Date?
    #endif

    var connectionIsBusy: Bool { phase != .idle }

    private var localName: String {
        #if os(macOS)
        return Host.current().localizedName ?? "Mac"
        #else
        return UIDevice.current.name
        #endif
    }

    func startDiscovery() {
        guard browser == nil else { return }
        let id = UUID()
        browserID = id
        let next = NWBrowser(for: .bonjour(type: "_linktv._tcp", domain: nil), using: .tcp)
        next.stateUpdateHandler = { [weak self] state in
            Task { @MainActor [weak self] in self?.discoveryChanged(state, id: id) }
        }
        next.browseResultsChangedHandler = { [weak self] results, _ in
            let nearby = Self.nearbyComputers(results)
            Task { @MainActor [weak self] in
                guard let self, self.browserID == id else { return }
                self.computers = nearby
            }
        }
        browser = next
        next.start(queue: DispatchQueue(label: "LinkTV.Bonjour"))
    }

    func rescan() {
        browserID = UUID()
        browser?.cancel()
        browser = nil
        computers = []
        discoveryMessage = "正在搜索附近的设备…"
        message = nil
        startDiscovery()
    }

    private nonisolated static func nearbyComputers(_ results: Set<NWBrowser.Result>) -> [NearbyComputer] {
        var discovered: [String: NearbyComputer] = [:]
        for result in results {
            guard case let .service(name, type, domain, _) = result.endpoint else { continue }
            let id = [name, type, domain].joined(separator: "|")
            discovered[id] = NearbyComputer(id: id, name: name,
                endpoint: .service(name: name, type: type, domain: domain, interface: nil))
        }
        return discovered.values.sorted { first, second in
            first.name.localizedStandardCompare(second.name) == .orderedAscending
        }
    }

    private func discoveryChanged(_ state: NWBrowser.State, id: UUID) {
        guard browserID == id else { return }
        switch state {
        case .ready:
            discoveryMessage = "在同一网络的 Windows 电脑上打开「双向投屏」。"
        case .waiting:
            discoveryMessage = "等待局域网。如果系统询问，请允许「本地网络」访问。"
        case .failed:
            computers = []
            discoveryMessage = "搜索暂不可用。请检查本地网络权限，再点「重新搜索」。"
        default:
            break
        }
    }

    func connect(_ computer: NearbyComputer) {
        guard !connectionIsBusy else { return }
        let id = UUID()
        sessionID = id
        peerName = computer.name
        phase = .connecting
        message = nil
        let receiver = IncomingMedia { [weak self] picture in
            guard let self, self.sessionID == id, self.phase == .receiving else { return }
            self.frame = picture
        }
        let video = AppleVideoDisplay()
        receiver.setAudible(audioVisible)
        let next = LinkWire(endpoint: computer.endpoint, localName: localName, localCapabilities: 3) { [weak self] event in
            if case let .message(type, payload) = event {
                switch type {
                case 0x10: receiver.start()
                case 0x11: receiver.stop(); video.stop()
                case 0x12: receiver.offerJPEG(payload); return
                case 0x13: receiver.offerPCM(payload); return
                case 0x14: break
                case 0x16: video.begin()
                case 0x17: video.offer(payload); return
                default: return
                }
            }
            DispatchQueue.main.async { [weak self] in self?.handle(event, id: id) }
        }
        media = receiver
        avcDisplay = video
        wire = next
        next.start()
    }

    func setAudioVisible(_ visible: Bool) {
        audioVisible = visible
        media?.setAudible(visible)
    }

    func respond(approved: Bool) {
        guard phase == .pairing else { return }
        #if os(iOS)
        if let request = broadcast {
            do {
                try BroadcastExchange.write(BroadcastExchange.Command(id: request.id,
                    action: approved ? "approve" : "reject"), "command.json")
                prompt = nil
                phase = approved ? .confirming : .broadcastStopping
                if !approved { broadcastStopRequestedAt = Date() }
            } catch {
                message = "无法向屏幕广播发送确认，请停止后重试。"
            }
            return
        }
        #endif
        prompt = nil
        phase = approved ? .confirming : .cancelling
        wire?.respond(approved: approved)
    }

    func disconnect() {
        #if os(iOS)
        if broadcast != nil {
            stopBroadcast()
            return
        }
        #endif
        if phase == .pairing {
            respond(approved: false)
            return
        }
        endConnection("已断开连接")
    }

    private func handle(_ event: LinkWire.Event, id: UUID) {
        guard sessionID == id else { return }
        switch event {
        case let .pairing(name, code):
            peerName = name
            prompt = PairingPrompt(name: name, number: code)
            phase = .pairing
        case .connected:
            prompt = nil
            phase = .connected
        case let .message(type, _):
            if type == 0x10 {
                #if os(macOS)
                stopMacCapture(notifyRemote: true)
                #endif
                frame = nil
                h264Layer = nil
                phase = .receiving
            } else if type == 0x11, phase == .receiving {
                frame = nil
                h264Layer = nil
                phase = .connected
            } else if type == 0x16, phase == .receiving {
                h264Layer = avcDisplay?.layer
            } else if type == 0x14, phase == .sending {
                #if os(macOS)
                stopMacSharing()
                #endif
            }
        case let .disconnected(reason):
            endConnection(reason ?? "设备已断开连接")
        }
    }

    private func endConnection(_ reason: String) {
        #if os(macOS)
        stopMacCapture(notifyRemote: false)
        #endif
        sessionID = nil
        let previous = wire
        wire = nil
        previous?.close()
        media?.stop()
        media = nil
        avcDisplay?.stop()
        avcDisplay = nil
        h264Layer = nil
        prompt = nil
        frame = nil
        peerName = ""
        phase = .idle
        message = reason
    }

    #if os(macOS)
    func startMacSharing() {
        guard phase == .connected, let wire else { return }
        let sender = MacScreenSender(wire: wire)
        capture = sender
        phase = .startingCapture
        message = nil
        Task { [weak self] in
            do {
                try await sender.start()
                guard let self, self.capture === sender, self.phase == .startingCapture else {
                    await sender.stop()
                    return
                }
                sender.enable()
                self.phase = .sending
            } catch {
                guard let self, self.capture === sender else { return }
                self.capture = nil
                self.phase = .connected
                self.message = "无法打开屏幕共享。请在系统设置里允许「屏幕录制」后重试。"
            }
        }
    }

    func stopMacSharing() {
        stopMacCapture(notifyRemote: true)
        phase = .connected
    }

    private func stopMacCapture(notifyRemote: Bool) {
        guard let sender = capture else { return }
        capture = nil
        sender.closeVideo(notifyPeer: notifyRemote)
        Task { await sender.stop() }
    }
    #endif

    #if os(iOS)
    func prepareBroadcast(_ computer: NearbyComputer) {
        guard !connectionIsBusy,
              let request = BroadcastExchange.Request(endpoint: computer.endpoint, localName: localName) else { return }
        do {
            try BroadcastExchange.write(request, "request.json")
        } catch {
            message = "无法准备屏幕广播。请检查应用和扩展的 App Group 签名设置。"
            return
        }
        broadcast = request
        broadcastStopRequestedAt = nil
        peerName = computer.name
        phase = .broadcastReady
        message = nil
        broadcastPoll?.cancel()
        broadcastPoll = Task { [weak self] in
            while !Task.isCancelled {
                self?.pollBroadcast(id: request.id)
                try? await Task.sleep(nanoseconds: 250_000_000)
            }
        }
    }

    private func pollBroadcast(id: UUID) {
        guard let current = broadcast, current.id == id else { return }
        guard let status = BroadcastExchange.read(BroadcastExchange.Status.self, "status.json"),
              status.id == id else {
            if Date().timeIntervalSince(current.date) >= 120 {
                clearBroadcast("广播准备已超时，请重新选择接收设备。")
            }
            return
        }
        if status.phase == "stopped" {
            clearBroadcast("本机投屏已结束")
            return
        }
        if let stopRequestedAt = broadcastStopRequestedAt {
            if Date().timeIntervalSince(stopRequestedAt) >= 8 {
                clearBroadcast("请通过系统录屏指示器确认广播已停止。")
            }
            return
        }
        switch status.phase {
        case "connecting":
            phase = .broadcastConnecting
        case "pairing":
            guard phase != .confirming, let name = status.name, let number = status.code else { return }
            peerName = name
            if prompt == nil {
                prompt = PairingPrompt(name: name, number: number)
            }
            phase = .pairing
        case "confirming":
            prompt = nil
            phase = .confirming
        case "casting":
            prompt = nil
            phase = .broadcasting
        default:
            break
        }
    }

    private func stopBroadcast() {
        guard let request = broadcast else { return }
        let pickerIsOpen = phase == .broadcastReady
        do {
            try BroadcastExchange.write(BroadcastExchange.Command(id: request.id, action: "stop"), "command.json")
            if pickerIsOpen,
               BroadcastExchange.read(BroadcastExchange.Status.self, "status.json")?.id != request.id {
                clearBroadcast("已取消屏幕广播")
                return
            }
            broadcastStopRequestedAt = Date()
            prompt = nil
            phase = .broadcastStopping
        } catch {
            message = "请通过系统录屏指示器停止屏幕广播。"
        }
    }

    private func clearBroadcast(_ reason: String) {
        if let request = broadcast,
           BroadcastExchange.read(BroadcastExchange.Request.self, "request.json")?.id == request.id,
           let group = FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: BroadcastExchange.group) {
            try? FileManager.default.removeItem(at: group.appendingPathComponent("request.json"))
        }
        broadcastPoll?.cancel()
        broadcastPoll = nil
        broadcast = nil
        broadcastStopRequestedAt = nil
        prompt = nil
        peerName = ""
        phase = .idle
        message = reason
    }
    #endif
}
