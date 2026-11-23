#if os(iOS)
import Foundation
import Network

enum BroadcastExchange {
    static let group = "group.io.github.reymondmeking.linktv"
    static let extensionIdentifier = "io.github.reymondmeking.linktv.broadcast"

    struct Request: Codable {
        let id: UUID
        let name: String
        let type: String
        let domain: String
        let localName: String
        let shareSound: Bool?
        let date: Date

        init?(endpoint: NWEndpoint, localName: String, shareSound: Bool) {
            guard case let .service(name, type, domain, _) = endpoint else { return nil }
            self.id = UUID()
            self.name = name
            self.type = type
            self.domain = domain
            self.localName = localName
            self.shareSound = shareSound
            self.date = Date()
        }

        var endpoint: NWEndpoint { .service(name: name, type: type, domain: domain, interface: nil) }
    }

    struct Status: Codable {
        let id: UUID
        let phase: String
        var name: String? = nil
        var code: String? = nil
        var reason: String? = nil
    }

    struct Command: Codable {
        let id: UUID
        let action: String
    }

    static func write<T: Encodable>(_ object: T, _ file: String) throws {
        guard let folder = FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: group) else {
            throw CocoaError(.fileWriteNoPermission)
        }
        try JSONEncoder().encode(object).write(to: folder.appendingPathComponent(file), options: .atomic)
    }

    static func read<T: Decodable>(_ type: T.Type, _ file: String) -> T? {
        guard let folder = FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: group),
              let data = try? Data(contentsOf: folder.appendingPathComponent(file)) else { return nil }
        return try? JSONDecoder().decode(type, from: data)
    }
}
#endif
