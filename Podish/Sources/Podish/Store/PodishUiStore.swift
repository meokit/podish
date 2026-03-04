import SwiftUI

@MainActor
final class PodishUiStore: ObservableObject {
    @Published var containers: [PodishContainer] = []

    @Published var images: [PodishImage] = [
        .init(id: UUID(), repoTag: "docker.io/i386/alpine:latest", digest: "sha256:a76a...cb9e", size: "7.1 MB", createdAt: .now.addingTimeInterval(-86000)),
        .init(id: UUID(), repoTag: "docker.io/library/python:3.12-alpine", digest: "sha256:45c2...8ad1", size: "57.3 MB", createdAt: .now.addingTimeInterval(-43000))
    ]

    @Published var events: [String] = [
        "container-start alpine-shell",
        "image-pull docker.io/i386/alpine:latest",
        "container-exit openssl-test (0)"
    ]

    @Published var selectedContainerID: String?
    var onStartContainer: ((String) -> Void)?
    var onStopContainer: ((String) -> Void)?
    var onAttachContainer: ((String) -> Void)?

    var runningContainers: [PodishContainer] { containers.filter { $0.state == .running } }

    var selectedContainer: PodishContainer? {
        if let selectedContainerID {
            return containers.first { $0.id == selectedContainerID }
        }
        return runningContainers.first ?? containers.first
    }

    func applyContainerList(_ items: [NativeContainerListItem]) {
        let previous = selectedContainerID
        let mapped = items.map { item in
            let state: PodishContainer.State
            if item.running {
                state = .running
            } else {
                switch item.state.lowercased() {
                case "exited":
                    state = .exited
                default:
                    state = .stopped
                }
            }

            let shortId = String(item.containerId.prefix(12))
            return PodishContainer(
                id: item.containerId,
                name: shortId,
                containerId: item.containerId,
                image: item.image,
                state: state,
                cpu: 0,
                memoryMB: 0,
                createdAt: .now
            )
        }

        if containers != mapped {
            containers = mapped
        }

        let nextSelected: String?
        if let previous, containers.contains(where: { $0.id == previous }) {
            nextSelected = previous
        } else {
            nextSelected = runningContainers.first?.id ?? containers.first?.id
        }

        if selectedContainerID != nextSelected {
            DispatchQueue.main.async {
                if self.selectedContainerID != nextSelected {
                    self.selectedContainerID = nextSelected
                }
            }
        }
    }

    func start(_ container: PodishContainer) {
        onStartContainer?(container.containerId)
    }

    func stop(_ container: PodishContainer) {
        onStopContainer?(container.containerId)
    }

    func attach(_ container: PodishContainer) {
        onAttachContainer?(container.containerId)
    }
}
