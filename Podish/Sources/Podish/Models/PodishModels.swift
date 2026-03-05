import SwiftUI

struct PodishContainer: Identifiable, Hashable {
    enum State: String {
        case running = "Running"
        case stopped = "Stopped"
        case exited = "Exited"

        var color: Color {
            switch self {
            case .running: return .green
            case .stopped: return .gray
            case .exited: return .red
            }
        }
    }

    let id: String
    var name: String
    var containerId: String
    var image: String
    var state: State
    var cpu: Double
    var memoryMB: Int
    var createdAt: Date
}

struct PodishImage: Identifiable, Hashable {
    let id: String
    var repoTag: String
    var digest: String
    var size: String
    var createdAt: Date
}

struct NativeImageListItem: Decodable, Hashable {
    let imageReference: String
    let manifestDigest: String
    let layerCount: Int
    let storeDirectory: String
    let tag: String?
    let repository: String?
}

struct NativeContainerListItem: Decodable, Hashable {
    let handle: String
    let containerId: String
    let name: String
    let image: String
    let state: String
    let hasTerminal: Bool
    let running: Bool
    let exitCode: Int?
}

enum SidebarSelection: Hashable {
    case images
    case containers
    case events
    case container(UUID)
}
