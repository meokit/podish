import SwiftUI

@MainActor
final class PodishUiStore: ObservableObject {
    @Published var containers: [PodishContainer] = [
        .init(id: UUID(), name: "alpine-shell", image: "docker.io/i386/alpine:latest", state: .running, cpu: 0.7, memoryMB: 24, createdAt: .now.addingTimeInterval(-3600)),
        .init(id: UUID(), name: "python-dev", image: "docker.io/library/python:3.12-alpine", state: .running, cpu: 2.4, memoryMB: 96, createdAt: .now.addingTimeInterval(-1800)),
        .init(id: UUID(), name: "openssl-test", image: "docker.io/i386/alpine:latest", state: .stopped, cpu: 0.0, memoryMB: 0, createdAt: .now.addingTimeInterval(-7200))
    ]

    @Published var images: [PodishImage] = [
        .init(id: UUID(), repoTag: "docker.io/i386/alpine:latest", digest: "sha256:a76a...cb9e", size: "7.1 MB", createdAt: .now.addingTimeInterval(-86000)),
        .init(id: UUID(), repoTag: "docker.io/library/python:3.12-alpine", digest: "sha256:45c2...8ad1", size: "57.3 MB", createdAt: .now.addingTimeInterval(-43000))
    ]

    @Published var events: [String] = [
        "container-start alpine-shell",
        "image-pull docker.io/i386/alpine:latest",
        "container-exit openssl-test (0)"
    ]

    @Published var selectedContainerID: UUID?

    var runningContainers: [PodishContainer] { containers.filter { $0.state == .running } }

    var selectedContainer: PodishContainer? {
        if let selectedContainerID {
            return containers.first { $0.id == selectedContainerID }
        }
        return runningContainers.first
    }
}
