import SwiftUI

@MainActor
final class PodishUiStore: ObservableObject {
    enum PendingContainerAction {
        case starting
        case stopping
    }

    @Published var containers: [PodishContainer] = []
    @Published var images: [PodishImage] = []
    @Published private(set) var pendingContainerActions: [String: PendingContainerAction] = [:]

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
    var onCreateContainer: ((String, String?, PodishNetworkMode, [PodishPortMapping], Int64?) -> Void)?
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
            let displayName = item.name.isEmpty ? shortId : item.name
            return PodishContainer(
                id: item.containerId,
                name: displayName,
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

        // Resolve pending actions based on observed runtime state.
        var nextPending = pendingContainerActions
        let knownIds = Set(containers.map(\.containerId))
        for container in containers {
            guard let action = nextPending[container.containerId] else { continue }
            switch action {
            case .starting:
                if container.state == .running {
                    nextPending.removeValue(forKey: container.containerId)
                }
            case .stopping:
                if container.state != .running {
                    nextPending.removeValue(forKey: container.containerId)
                }
            }
        }
        for id in nextPending.keys where !knownIds.contains(id) {
            nextPending.removeValue(forKey: id)
        }
        if nextPending != pendingContainerActions {
            pendingContainerActions = nextPending
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
        pendingContainerActions[container.containerId] = .starting
        onStartContainer?(container.containerId)
    }

    func stop(_ container: PodishContainer) {
        pendingContainerActions[container.containerId] = .stopping
        onStopContainer?(container.containerId)
    }

    func pendingAction(for containerId: String) -> PendingContainerAction? {
        pendingContainerActions[containerId]
    }

    func clearPendingAction(for containerId: String) {
        pendingContainerActions.removeValue(forKey: containerId)
    }

    func attach(_ container: PodishContainer) {
        onAttachContainer?(container.containerId)
    }

    func remove(_ container: PodishContainer) {
        onRemoveContainer?(container.containerId)
    }

    func createContainer(
        fromImage imageRef: String,
        name: String?,
        networkMode: PodishNetworkMode,
        portMappings: [PodishPortMapping],
        memoryQuotaBytes: Int64?
    ) {
        onCreateContainer?(imageRef, name, networkMode, portMappings, memoryQuotaBytes)
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
