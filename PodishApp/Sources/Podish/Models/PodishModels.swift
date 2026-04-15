import SwiftUI

struct PodishContainer: Identifiable, Hashable {
    enum State: String {
        case running = "Active"
        case stopped = "Paused"
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

enum PodishImagePullPhase: String, Decodable, Hashable {
    case resolving
    case downloading
    case extracting
    case completed
    case failed

    var isTerminal: Bool {
        switch self {
        case .completed, .failed:
            return true
        case .resolving, .downloading, .extracting:
            return false
        }
    }
}

struct PodishImagePullStatus: Hashable {
    let imageReference: String
    let phase: PodishImagePullPhase
    let message: String
    let overallBytes: Int64?
    let overallTotalBytes: Int64?
    let layerBytes: Int64?
    let layerTotalBytes: Int64?
    let layerIndex: Int?
    let layerCount: Int?

    var progressFraction: Double? {
        guard let overallBytes, let overallTotalBytes, overallTotalBytes > 0 else { return nil }
        let clamped = min(max(overallBytes, 0), overallTotalBytes)
        return Double(clamped) / Double(overallTotalBytes)
    }

    var isActive: Bool {
        !phase.isTerminal
    }
}

struct NativeImageListItem: Decodable, Hashable, Sendable {
    let imageReference: String
    let manifestDigest: String
    let layerCount: Int
    let storeDirectory: String
    let tag: String?
    let repository: String?
}

struct NativeContainerListItem: Decodable, Hashable, Sendable {
    let handle: String
    let containerId: String
    let name: String
    let image: String
    let state: String
    let hasTerminal: Bool
    let running: Bool
    let exitCode: Int?
}

struct NativePublishedPortSpec: Decodable, Hashable, Sendable {
    let hostPort: Int
    let containerPort: Int
    let protocolValue: Int
    let bindAddress: String

    enum CodingKeys: String, CodingKey {
        case hostPort
        case containerPort
        case protocolValue = "protocol"
        case bindAddress
    }
}

struct NativeRunSpec: Decodable, Hashable, Sendable {
    let name: String?
    let networkMode: Int
    let image: String?
    let rootfs: String?
    let exe: String?
    let exeArgs: [String]
    let volumes: [String]
    let env: [String]
    let dns: [String]
    let interactive: Bool
    let tty: Bool
    let strace: Bool
    let logDriver: String
    let publishedPorts: [NativePublishedPortSpec]
    let memoryQuotaBytes: Int64?
}

struct NativeContainerInspect: Decodable, Hashable, Sendable {
    let handle: String
    let containerId: String
    let name: String
    let image: String
    let state: String
    let hasTerminal: Bool
    let running: Bool
    let exitCode: Int?
    let spec: NativeRunSpec
}

struct NativeContainerLogEntry: Decodable, Hashable, Sendable {
    let time: String
    let stream: String
    let log: String
}

struct NativeLogsChunk: Decodable, Hashable, Sendable {
    let cursor: String
    let entries: [NativeContainerLogEntry]
}

enum PodishSidebarDestination: Hashable {
    case home
    case container(String)
}

enum PodishNetworkMode: String, CaseIterable, Identifiable, Sendable {
    case host
    case privateNet = "private"

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .host:
            return "Automatic"
        case .privateNet:
            return "Managed"
        }
    }

    var nativeValue: Int {
        switch self {
        case .host:
            return 0
        case .privateNet:
            return 1
        }
    }
}

enum PodishMemoryLimits {
    static let minimumMemoryQuotaMB = 32
    static let defaultMemoryQuotaMB = 2048
    static let bytesPerMiB: Int64 = 1024 * 1024
}

struct PodishPortMapping: Hashable, Sendable {
    let hostPort: Int
    let containerPort: Int
}
