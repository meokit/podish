import SwiftUI

@MainActor
final class PodishUiStore: ObservableObject {
    @Published var containers: [PodishContainer] = []
    @Published var images: [PodishImage] = []

    @Published var events: [String] = [
        "container-start alpine-shell",
        "image-pull docker.io/i386/alpine:latest",
        "container-exit openssl-test (0)"
    ]

    @Published var selectedContainerID: String?
    var onStartContainer: ((String) -> Void)?
    var onStopContainer: ((String) -> Void)?
    var onRemoveContainer: ((String) -> Void)?
    var onAttachContainer: ((String) -> Void)?
    var onCreateContainer: ((String) -> Void)?
    var onPullImage: ((String) -> Void)?
    var onRemoveImage: ((String) -> Void)?
    var onShowNewContainer: (() -> Void)?

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

    func applyImageList(_ items: [NativeImageListItem]) {
        let mapped = items.map { item in
            PodishImage(
                id: item.imageReference,
                repoTag: item.imageReference,
                digest: item.manifestDigest,
                size: "\(item.layerCount) layers",
                createdAt: .now
            )
        }

        if images != mapped {
            images = mapped
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

    func remove(_ container: PodishContainer) {
        onRemoveContainer?(container.containerId)
    }

    func createContainer(fromImage imageRef: String) {
        onCreateContainer?(imageRef)
    }

    func pullImage(_ imageRef: String) {
        onPullImage?(imageRef)
    }

    func removeImage(_ imageRef: String) {
        onRemoveImage?(imageRef)
    }

    func showNewContainer() {
        onShowNewContainer?()
    }
}
