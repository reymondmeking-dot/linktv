import CommonCrypto
import CryptoKit
import Foundation
import Network
import Security

final class LinkWire {
    enum Event {
        case pairing(name: String, code: String)
        case connected
        case message(type: UInt8, payload: Data)
        case disconnected(String?)
    }

    private enum Phase {
        case idle, dialing, handshake, pairing, negotiating, connected, closed
    }

    private enum Packet: UInt8 {
        case accept = 0x01, reject = 0x02
        case start = 0x10, stop = 0x11, jpeg = 0x12, audio = 0x13
        case stopRequest = 0x14, capabilities = 0x15, h264Begin = 0x16, h264Frame = 0x17
    }

    private enum WireError: Error {
        case invalidPacket, cryptography
    }

    private struct Pending {
        var enabled = false
        var casting = false
        var scheduled = false
        var overflow: String?
        var canSendH264 = false
        var canReceiveH264 = false
        var controls: [Packet] = []
        var jpeg: Data?
        var audio: [Data] = []
        var h264 = false
        var lastVideoPts: Int64 = -1
        var videoStart: Data?
        var video: [Data] = []
        var videoBytes = 0

        mutating func clearVideo() {
            h264 = false
            lastVideoPts = -1
            videoStart = nil
            video.removeAll()
            videoBytes = 0
        }
    }

    private struct ClientHello {
        let privateKey: P256.KeyAgreement.PrivateKey
        let nonce: Data
        let wire: Data
    }

    static let h264Send: UInt8 = 0x01
    static let h264Receive: UInt8 = 0x02
    private static let capabilityMask = LinkWire.h264Send | LinkWire.h264Receive
    private static let protocolV2 = "LINKTV2"
    private static let protocolV3 = "LINKTV3"
    private static let maxVideoQueueCount = 12
    private static let maxVideoQueueBytes = 8 * 1024 * 1024
    private static let videoHeaderSize = 9
    static let maxH264AccessUnit = 2_097_093
    private static let maxNameLength = 96
    private static let maxJpegLength = 2_097_087
    private static let maxAudioLength = 16_384
    private static let minBodyLength = 64
    private static let maxBodyLength = 2_097_152

    private let endpoint: NWEndpoint
    private var connection: NWConnection
    private let localCapabilities: UInt8
    private let queue = DispatchQueue(label: "lan.linktv.wire")
    private let name: Data
    private let handler: (Event) -> Void
    private let pendingLock = NSLock()
    private var pending = Pending()

    // Connection state and channel counters are owned by the serial queue.
    private var phase = Phase.idle
    private var deadline: DispatchWorkItem?
    private var incoming: Channel?
    private var outgoing: Channel?
    private var responded = false
    private var localApproved = false
    private var remoteApproved = false
    private var remoteCasting = false
    private var remoteH264 = false
    private var lastRemotePts: Int64 = -1
    private var writeInProgress = false
    private var wireProtocol = LinkWire.protocolV3
    private var awaitingFirstServerBytes = false
    private var connectionGeneration = 0

    var canSendH264: Bool {
        withPending { $0.enabled && $0.canSendH264 }
    }

    var canReceiveH264: Bool {
        withPending { $0.enabled && $0.canReceiveH264 }
    }

    init(endpoint: NWEndpoint, localName: String, localCapabilities: UInt8 = 0,
         handler: @escaping (Event) -> Void) {
        self.endpoint = endpoint
        self.localCapabilities = localCapabilities & Self.capabilityMask
        connection = Self.makeConnection(endpoint: endpoint)
        let cleaned = localName.trimmingCharacters(in: .whitespacesAndNewlines)
        name = Data((Self.isValidName(cleaned) ? cleaned : "Apple").utf8)
        self.handler = handler
    }

    private static func makeConnection(endpoint: NWEndpoint) -> NWConnection {
        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        tcp.enableKeepalive = true
        tcp.keepaliveIdle = 5
        tcp.keepaliveInterval = 1
        tcp.keepaliveCount = 6
        return NWConnection(to: endpoint, using: NWParameters(tls: nil, tcp: tcp))
    }

    deinit {
        deadline?.cancel()
        connection.cancel()
    }

    func start() {
        queue.async { [weak self] in
            guard let self, self.phase == .idle else { return }
            self.openConnection()
        }
    }

    private func openConnection() {
        phase = .dialing
        let generation = connectionGeneration
        connection.stateUpdateHandler = { [weak self] state in
            guard let self, self.connectionGeneration == generation else { return }
            self.connectionChanged(state)
        }
        armDeadline(seconds: 8, reason: "连接对方设备超时，请检查网络")
        connection.start(queue: queue)
    }

    private func retryLegacyIfUnrecognized() -> Bool {
        guard phase == .handshake, wireProtocol == Self.protocolV3, awaitingFirstServerBytes else { return false }
        connectionGeneration += 1
        connection.stateUpdateHandler = nil
        connection.cancel()
        connection = Self.makeConnection(endpoint: endpoint)
        wireProtocol = Self.protocolV2
        openConnection()
        return true
    }

    func respond(approved: Bool) {
        queue.async { [weak self] in
            guard let self, self.phase == .pairing, !self.responded else { return }
            self.responded = true
            self.writeFrame(approved ? .accept : .reject, payload: Data()) { [weak self] in
                guard let self, self.phase == .pairing else { return }
                if approved {
                    self.localApproved = true
                    self.finishPairingIfReady()
                } else {
                    self.finish("已拒绝配对")
                }
            }
        }
    }

    func send(type: UInt8, payload: Data = Data()) {
        guard let packet = Packet(rawValue: type) else { return }

        // Coalesce captured frames before scheduling a queue hop so a fast capture
        // thread cannot build up a backlog of retained images.
        enqueue { pending in
            switch packet {
            case .start, .stop, .stopRequest:
                guard payload.isEmpty else { return false }
                if packet != .stopRequest {
                    guard pending.casting != (packet == .start) else { return false }
                    pending.casting = packet == .start
                    if packet == .stop {
                        pending.jpeg = nil
                        pending.audio.removeAll()
                        pending.clearVideo()
                    }
                }
                if pending.controls.count == 16 {
                    pending.overflow = "投屏指令发送超时"
                } else {
                    pending.controls.append(packet)
                }
            case .jpeg:
                guard pending.casting, !pending.h264, !payload.isEmpty, payload.count <= Self.maxJpegLength else { return false }
                pending.jpeg = payload
            case .audio:
                guard pending.casting, Self.validAudioLength(payload.count) else { return false }
                if pending.audio.count == 6 {
                    pending.audio.removeFirst()
                }
                pending.audio.append(payload)
            case .accept, .reject, .capabilities, .h264Begin, .h264Frame:
                return false
            }
            return true
        }
    }

    @discardableResult
    func sendH264Begin(width: Int, height: Int, fps: Int) -> Bool {
        guard Self.validH264Format(width: width, height: height, fps: fps) else { return false }
        return enqueue { pending in
            guard pending.casting, pending.canSendH264, !pending.h264 else { return false }
            pending.h264 = true
            pending.jpeg = nil
            pending.videoStart = Self.join(Self.bigEndian(UInt16(width)), Self.bigEndian(UInt16(height)),
                                           Self.bigEndian(UInt16(fps)))
            return true
        }
    }

    @discardableResult
    func sendH264Frame(ptsUs: Int64, keyFrame: Bool, accessUnit: Data) -> Bool {
        guard ptsUs >= 0, accessUnit.count <= Self.maxH264AccessUnit,
              Self.hasAnnexBStartCode(accessUnit) else { return false }
        var exceeded = false
        let enqueued = enqueue { pending in
            guard pending.casting, pending.h264, pending.canSendH264,
                  ptsUs > pending.lastVideoPts, pending.lastVideoPts >= 0 || keyFrame else { return false }
            let length = Self.videoHeaderSize + accessUnit.count
            if pending.video.count >= Self.maxVideoQueueCount || pending.videoBytes + length > Self.maxVideoQueueBytes {
                pending.overflow = "网络过慢，H.264 投屏已断开"
                exceeded = true
                return true
            }
            let payload = Self.join(Self.bigEndian(ptsUs), Data([keyFrame ? 1 : 0]), accessUnit)
            pending.video.append(payload)
            pending.videoBytes += length
            pending.lastVideoPts = ptsUs
            return true
        }
        return enqueued && !exceeded
    }

    @discardableResult
    private func enqueue(_ append: (inout Pending) -> Bool) -> Bool {
        let result = withPending { pending -> (accepted: Bool, wake: Bool) in
            guard pending.enabled, pending.overflow == nil, append(&pending) else { return (false, false) }
            let wake = !pending.scheduled
            pending.scheduled = true
            return (true, wake)
        }
        if result.wake {
            queue.async { [weak self] in self?.drain() }
        }
        return result.accepted
    }

    func close() {
        queue.async { [weak self] in self?.finish(nil) }
    }

    private func connectionChanged(_ state: NWConnection.State) {
        switch state {
        case .ready:
            if phase == .dialing {
                beginHandshake()
            }
        case .failed:
            if !retryLegacyIfUnrecognized() {
                finish(phase == .connected ? "与对方设备的连接已中断" : "无法连接对方设备，请检查网络和程序")
            }
        case .cancelled:
            finish(nil)
        default:
            break
        }
    }

    private func beginHandshake() {
        phase = .handshake
        awaitingFirstServerBytes = true
        armDeadline(seconds: 8, reason: "连接对方设备超时，请检查网络")

        do {
            let privateKey = P256.KeyAgreement.PrivateKey(compactRepresentable: false)
            let coordinates = Data(privateKey.publicKey.x963Representation.dropFirst())
            let nonce = try Self.randomBytes(32)
            let identity = Self.join(coordinates, Data([UInt8(name.count)]), name)
            let commitment = Self.commitment(protocolName: wireProtocol, role: "C", identity: identity, nonce: nonce)
            let hello = ClientHello(privateKey: privateKey, nonce: nonce,
                                    wire: Self.join(Data(wireProtocol.utf8), identity, commitment))
            writeRaw(hello.wire) { [weak self] in
                self?.readServerHello(hello)
            }
        } catch {
            finish("连接校验失败")
        }
    }

    private func readServerHello(_ hello: ClientHello) {
        readExactly(65) { [weak self] header in
            guard let self else { return }
            guard let length = header.last, length > 0, Int(length) <= Self.maxNameLength else {
                self.finish("对方返回了无效的配对信息")
                return
            }
            self.readExactly(Int(length) + 32) { [weak self] tail in
                guard let self else { return }
                let nameBytes = Data(tail.prefix(Int(length)))
                guard let peerName = String(data: nameBytes, encoding: .utf8), Self.isValidName(peerName) else {
                    self.finish("对方返回了无效的配对信息")
                    return
                }
                let peerIdentity = Self.join(header, nameBytes)
                let peerCommitment = Data(tail.suffix(32))
                let coordinates = Data(header.prefix(64))
                self.writeRaw(hello.nonce) { [weak self] in
                    self?.readExactly(32) { [weak self] peerNonce in
                        self?.preparePairing(hello: hello, coordinates: coordinates, peerName: peerName,
                                             identity: peerIdentity, commitment: peerCommitment, nonce: peerNonce)
                    }
                }
            }
        }
    }

    private func preparePairing(hello: ClientHello, coordinates: Data, peerName: String,
                                identity: Data, commitment: Data, nonce: Data) {
        do {
            guard Self.commitment(protocolName: wireProtocol, role: "S", identity: identity, nonce: nonce) == commitment else {
                throw WireError.cryptography
            }
            let publicKey = try P256.KeyAgreement.PublicKey(x963Representation: Self.join(Data([0x04]), coordinates))
            let sharedSecret = try hello.privateKey.sharedSecretFromKeyAgreement(with: publicKey)
            let sharedMaterial = sharedSecret.withUnsafeBytes { Self.hash(Data($0)) }
            let transcript = Self.hash(Self.join(hello.wire, identity, commitment, hello.nonce, nonce))
            let sessionKey = Self.hmac(sharedMaterial, key: transcript)
            let comparison = Self.readUInt32(Self.derive("compare", from: sessionKey, protocolName: wireProtocol).prefix(4)) % 1_000_000
            let code = String(format: "%06u", comparison)

            incoming = Channel(key: Self.derive("se", from: sessionKey, protocolName: wireProtocol),
                               macKey: Self.derive("sm", from: sessionKey, protocolName: wireProtocol))
            outgoing = Channel(key: Self.derive("ce", from: sessionKey, protocolName: wireProtocol),
                               macKey: Self.derive("cm", from: sessionKey, protocolName: wireProtocol))
            phase = .pairing
            armDeadline(seconds: 75, reason: "确认配对超时，请重新选择设备")
            handler(.pairing(name: peerName, code: code))
            readFrame(pairing: true) { [weak self] packet, payload in
                guard let self, self.phase == .pairing else { return }
                if packet == .accept, payload.isEmpty {
                    self.remoteApproved = true
                    self.finishPairingIfReady()
                } else if packet == .reject, payload.isEmpty {
                    self.finish("对方拒绝了配对")
                } else {
                    self.finish("对方返回了无效的配对信息")
                }
            }
        } catch {
            finish("连接校验失败")
        }
    }

    private func finishPairingIfReady() {
        guard phase == .pairing, localApproved, remoteApproved else { return }
        if wireProtocol == Self.protocolV2 {
            becomeConnected(remoteCapabilities: 0)
            return
        }

        phase = .negotiating
        armDeadline(seconds: 8, reason: "协商投屏格式超时")
        writeFrame(.capabilities, payload: Data([localCapabilities])) { [weak self] in
            self?.readFrame(pairing: true) { [weak self] packet, payload in
                guard let self, self.phase == .negotiating else { return }
                guard packet == .capabilities, payload.count == 1, let caps = payload.first,
                      (caps & ~Self.capabilityMask) == 0 else {
                    self.finish("对方返回了无效的投屏能力")
                    return
                }
                self.becomeConnected(remoteCapabilities: caps)
            }
        }
    }

    private func becomeConnected(remoteCapabilities: UInt8) {
        deadline?.cancel()
        deadline = nil
        phase = .connected
        withPending { pending in
            pending.enabled = true
            pending.canSendH264 = (localCapabilities & Self.h264Send) != 0 && (remoteCapabilities & Self.h264Receive) != 0
            pending.canReceiveH264 = (localCapabilities & Self.h264Receive) != 0 && (remoteCapabilities & Self.h264Send) != 0
        }
        handler(.connected)
        readMedia()
    }

    private func readMedia() {
        readFrame(pairing: false) { [weak self] packet, payload in
            guard let self, self.phase == .connected else { return }
            switch packet {
            case .start, .stop, .stopRequest:
                guard payload.isEmpty else {
                    self.finish("收到无效的投屏指令")
                    return
                }
                // A stop request is for our sender. Only START and STOP change the peer's receive state.
                if packet != .stopRequest {
                    let starting = packet == .start
                    if starting != self.remoteCasting {
                        self.remoteH264 = false
                        self.lastRemotePts = -1
                    }
                    self.remoteCasting = starting
                }
                self.handler(.message(type: packet.rawValue, payload: payload))
            case .audio, .jpeg:
                let valid = packet == .audio ? Self.validAudioLength(payload.count) : !payload.isEmpty && !self.remoteH264
                guard valid else {
                    self.finish("收到无效的投屏数据")
                    return
                }
                if self.remoteCasting {
                    self.handler(.message(type: packet.rawValue, payload: payload))
                }
            case .h264Begin, .h264Frame:
                guard self.handleVideo(packet, payload: payload) else {
                    self.finish("收到无效的 H.264 投屏数据")
                    return
                }
            case .accept, .reject, .capabilities:
                self.finish("收到无效的投屏指令")
                return
            }
            self.readMedia()
        }
    }

    private func handleVideo(_ type: Packet, payload: Data) -> Bool {
        guard canReceiveH264, remoteCasting else { return false }
        if type == .h264Begin {
            guard !remoteH264, payload.count == 6 else { return false }
            let format = Array(payload)
            let width = Int(format[0]) << 8 | Int(format[1])
            let height = Int(format[2]) << 8 | Int(format[3])
            let fps = Int(format[4]) << 8 | Int(format[5])
            guard Self.validH264Format(width: width, height: height, fps: fps) else { return false }
            remoteH264 = true
        } else {
            let count = payload.count - Self.videoHeaderSize
            guard remoteH264, count >= 4, count <= Self.maxH264AccessUnit else { return false }
            let header = Array(payload.prefix(Self.videoHeaderSize))
            let timestamp = header.prefix(8).reduce(UInt64(0)) { ($0 << 8) | UInt64($1) }
            guard timestamp <= UInt64(Int64.max), header[8] <= 1, Int64(timestamp) > lastRemotePts,
                  lastRemotePts >= 0 || header[8] == 1,
                  Self.hasAnnexBStartCode(Data(payload.dropFirst(Self.videoHeaderSize).prefix(5))) else { return false }
            lastRemotePts = Int64(timestamp)
        }
        handler(.message(type: type.rawValue, payload: payload))
        return true
    }

    private func readFrame(pairing: Bool, completion: @escaping (Packet, Data) -> Void) {
        readExactly(4) { [weak self] header in
            guard let self else { return }
            let length = Int(Self.readUInt32(header))
            let maxLength = pairing ? Self.minBodyLength : Self.maxBodyLength
            guard length >= Self.minBodyLength, length <= maxLength, length.isMultiple(of: 16) else {
                self.finish("收到无效的投屏数据")
                return
            }
            self.readExactly(length) { [weak self] body in
                guard let self, let incoming = self.incoming else { return }
                do {
                    let cleartext = try incoming.decode(header: header, body: body)
                    guard let type = cleartext.first, let packet = Packet(rawValue: type) else {
                        throw WireError.invalidPacket
                    }
                    completion(packet, Data(cleartext.dropFirst()))
                } catch {
                    self.finish("连接校验失败")
                }
            }
        }
    }

    private func drain() {
        let overflow = withPending { pending -> String? in
            pending.scheduled = false
            return pending.overflow
        }
        if let overflow {
            finish(overflow)
            return
        }

        guard phase == .connected, !writeInProgress else { return }
        let next = withPending { pending -> (Packet, Data)? in
            if !pending.controls.isEmpty {
                return (pending.controls.removeFirst(), Data())
            }
            if let beginning = pending.videoStart {
                pending.videoStart = nil
                return (.h264Begin, beginning)
            }
            if let jpeg = pending.jpeg {
                pending.jpeg = nil
                return (.jpeg, jpeg)
            }
            if !pending.audio.isEmpty {
                return (.audio, pending.audio.removeFirst())
            }
            if !pending.video.isEmpty {
                let accessUnit = pending.video.removeFirst()
                pending.videoBytes -= accessUnit.count
                return (.h264Frame, accessUnit)
            }
            return nil
        }
        guard let (type, payload) = next else { return }
        writeInProgress = true
        armDeadline(seconds: 3, reason: "发送投屏数据超时")
        writeFrame(type, payload: payload) { [weak self] in
            guard let self else { return }
            self.deadline?.cancel()
            self.deadline = nil
            self.writeInProgress = false
            self.drain()
        }
    }

    private func writeFrame(_ type: Packet, payload: Data, completion: @escaping () -> Void) {
        guard let outgoing else { return }
        do {
            writeRaw(try outgoing.encode(type: type, payload: payload), completion: completion)
        } catch {
            finish("连接校验失败")
        }
    }

    private func readExactly(_ length: Int, collected: Data = Data(), completion: @escaping (Data) -> Void) {
        let remaining = length - collected.count
        let generation = connectionGeneration
        connection.receive(minimumIncompleteLength: remaining, maximumLength: remaining) { [weak self] data, _, end, error in
            guard let self, self.phase != .closed, self.connectionGeneration == generation else { return }
            if let data, !data.isEmpty {
                self.awaitingFirstServerBytes = false
            }
            if error != nil {
                if !self.retryLegacyIfUnrecognized() {
                    self.finish("与对方设备的连接已中断")
                }
                return
            }

            let received = Self.join(collected, data ?? Data())
            if received.count == length {
                completion(received)
            } else if end || data?.isEmpty != false {
                if !self.retryLegacyIfUnrecognized() {
                    self.finish("对方设备已断开连接")
                }
            } else {
                self.readExactly(length, collected: received, completion: completion)
            }
        }
    }

    private func writeRaw(_ data: Data, completion: @escaping () -> Void) {
        let generation = connectionGeneration
        connection.send(content: data, completion: .contentProcessed { [weak self] error in
            guard let self, self.phase != .closed, self.connectionGeneration == generation else { return }
            if error == nil {
                completion()
            } else if !self.retryLegacyIfUnrecognized() {
                self.finish("与对方设备的连接已中断")
            }
        })
    }

    private func armDeadline(seconds: Int, reason: String) {
        deadline?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.finish(reason) }
        deadline = work
        queue.asyncAfter(deadline: .now() + .seconds(seconds), execute: work)
    }

    private func finish(_ reason: String?) {
        guard phase != .closed else { return }
        phase = .closed
        deadline?.cancel()
        deadline = nil
        incoming = nil
        outgoing = nil
        withPending { $0 = Pending() }
        connection.stateUpdateHandler = nil
        connection.cancel()
        handler(.disconnected(reason))
    }

    private func withPending<T>(_ body: (inout Pending) -> T) -> T {
        pendingLock.lock()
        defer { pendingLock.unlock() }
        return body(&pending)
    }

    private static func isValidName(_ name: String) -> Bool {
        !name.isEmpty && name.utf8.count <= maxNameLength &&
            !name.unicodeScalars.contains { $0.value <= 0x1f || (0x7f...0x9f).contains($0.value) }
    }

    private static func validH264Format(width: Int, height: Int, fps: Int) -> Bool {
        width >= 2 && height >= 2 && width.isMultiple(of: 2) && height.isMultiple(of: 2)
            && max(width, height) <= 1920 && min(width, height) <= 1080 && (1...60).contains(fps)
    }

    private static func hasAnnexBStartCode(_ data: Data) -> Bool {
        let leading = Array(data.prefix(5))
        return leading.count >= 4 && leading[0] == 0 && leading[1] == 0 &&
            (leading[2] == 1 || (leading.count >= 5 && leading[2] == 0 && leading[3] == 1))
    }

    private static func validAudioLength(_ length: Int) -> Bool {
        length > 0 && length <= maxAudioLength && length.isMultiple(of: 4)
    }

    private static func randomBytes(_ count: Int) throws -> Data {
        var data = Data(count: count)
        let status = data.withUnsafeMutableBytes { (buffer: UnsafeMutableRawBufferPointer) in
            guard let destination = buffer.baseAddress else { return errSecParam }
            return SecRandomCopyBytes(kSecRandomDefault, count, destination)
        }
        guard status == errSecSuccess else { throw WireError.cryptography }
        return data
    }

    private static func join(_ parts: Data...) -> Data {
        var result = Data()
        for data in parts {
            result.append(data)
        }
        return result
    }

    private static func hash(_ input: Data) -> Data {
        Data(SHA256.hash(data: input))
    }

    private static func hmac(_ input: Data, key: Data) -> Data {
        Data(HMAC<SHA256>.authenticationCode(for: input, using: SymmetricKey(data: key)))
    }

    private static func commitment(protocolName: String, role: String, identity: Data, nonce: Data) -> Data {
        hash(join(Data("\(protocolName) commit \(role)".utf8), identity, nonce))
    }

    private static func derive(_ label: String, from sessionKey: Data, protocolName: String) -> Data {
        hmac(join(Data("\(protocolName) \(label)".utf8), Data([0x01])), key: sessionKey)
    }

    private static func readUInt32(_ data: Data) -> UInt32 {
        data.reduce(0) { ($0 << 8) | UInt32($1) }
    }

    private static func bigEndian<T: FixedWidthInteger>(_ value: T) -> Data {
        var encoded = value.bigEndian
        return withUnsafeBytes(of: &encoded) { Data($0) }
    }

    private final class Channel {
        private let key: Data
        private let macKey: SymmetricKey
        private var sequence: UInt64 = 0

        init(key: Data, macKey: Data) {
            self.key = key
            self.macKey = SymmetricKey(data: macKey)
        }

        func encode(type: Packet, payload: Data) throws -> Data {
            let iv = try LinkWire.randomBytes(16)
            let plaintext = LinkWire.join(Data([type.rawValue]), payload)
            let ciphertext = try aes(CCOperation(kCCEncrypt), iv: iv, data: plaintext)
            let bodyLength = UInt32(iv.count + ciphertext.count + 32)
            let header = LinkWire.bigEndian(bodyLength)
            let macInput = LinkWire.join(LinkWire.bigEndian(sequence), header, iv, ciphertext)
            let tag = Data(HMAC<SHA256>.authenticationCode(for: macInput, using: macKey))
            sequence += 1
            return LinkWire.join(header, iv, ciphertext, tag)
        }

        func decode(header: Data, body: Data) throws -> Data {
            let tag = Data(body.suffix(32))
            let signed = Data(body.dropLast(32))
            let macInput = LinkWire.join(LinkWire.bigEndian(sequence), header, signed)
            guard HMAC<SHA256>.isValidAuthenticationCode(tag, authenticating: macInput, using: macKey) else {
                throw WireError.cryptography
            }

            let iv = Data(signed.prefix(16))
            let ciphertext = Data(signed.dropFirst(16))
            let plaintext = try aes(CCOperation(kCCDecrypt), iv: iv, data: ciphertext)
            sequence += 1
            return plaintext
        }

        private func aes(_ operation: CCOperation, iv: Data, data: Data) throws -> Data {
            let capacity = data.count + kCCBlockSizeAES128
            var result = Data(count: capacity)
            var actualLength = 0
            let status = result.withUnsafeMutableBytes { (output: UnsafeMutableRawBufferPointer) in
                key.withUnsafeBytes { (keyBytes: UnsafeRawBufferPointer) in
                    iv.withUnsafeBytes { (ivBytes: UnsafeRawBufferPointer) in
                        data.withUnsafeBytes { (input: UnsafeRawBufferPointer) in
                            CCCrypt(operation, CCAlgorithm(kCCAlgorithmAES), CCOptions(kCCOptionPKCS7Padding),
                                    keyBytes.baseAddress, key.count, ivBytes.baseAddress,
                                    input.baseAddress, data.count, output.baseAddress, capacity, &actualLength)
                        }
                    }
                }
            }
            guard status == kCCSuccess else { throw WireError.cryptography }
            return Data(result.prefix(actualLength))
        }
    }
}
