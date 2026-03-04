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

    let id: UUID
    var name: String
    var image: String
    var state: State
    var cpu: Double
    var memoryMB: Int
    var createdAt: Date
}

struct PodishImage: Identifiable, Hashable {
    let id: UUID
    var repoTag: String
    var digest: String
    var size: String
    var createdAt: Date
}

enum SidebarSelection: Hashable {
    case images
    case containers
    case events
    case container(UUID)
}
