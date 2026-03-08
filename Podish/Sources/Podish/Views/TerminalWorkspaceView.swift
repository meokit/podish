import SwiftUI

struct TerminalWorkspaceView: View {
    @ObservedObject var store: PodishUiStore
    @StateObject private var session = PodishTerminalSession()

    var body: some View {
        ZStack(alignment: .topLeading) {
            TerminalViewHost(terminalView: session.currentTerminalView)
                .id(session.activeTerminalIdentity)
                .background(Color.black)
                .clipped()
                .onAppear {
                    session.startIfNeeded()
                }
                .onDisappear {
                    session.stop()
                }

            if let startupError = session.startupError {
                Text(startupError)
                    .font(.caption)
                    .foregroundStyle(.white)
                    .padding(8)
                    .background(.red.opacity(0.75), in: RoundedRectangle(cornerRadius: 8))
                    .padding(12)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .onAppear {
            session.onContainerList = { items in
                DispatchQueue.main.async {
                    store.applyContainerList(items)
                }
            }
            session.onImageList = { items in
                DispatchQueue.main.async {
                    store.applyImageList(items)
                }
            }
            session.onContainerStateChanged = { items in
                DispatchQueue.main.async {
                    store.applyContainerList(items)
                    if let selectedId = store.selectedContainerID,
                       let selected = store.containers.first(where: { $0.id == selectedId }),
                       selected.state == .running {
                        session.attachContainer(selected.containerId)
                    }
                }
            }
            store.onStartContainer = { containerId in
                session.startContainer(containerId) { _ in
                    store.clearPendingAction(for: containerId)
                }
            }
            store.onStopContainer = { containerId in
                session.stopContainer(containerId) { _ in
                    store.clearPendingAction(for: containerId)
                }
            }
            store.onRemoveContainer = { containerId in
                session.removeContainer(containerId)
            }
            store.onCreateContainer = { imageRef, name, networkMode, portMappings in
                session.createContainer(
                    from: imageRef,
                    name: name,
                    networkMode: networkMode,
                    portMappings: portMappings
                )
            }
            store.onPullImage = { imageRef in
                session.pullImage(imageRef)
            }
            store.onRemoveImage = { imageRef in
                session.removeImage(imageRef)
            }
            store.onAttachContainer = { containerId in
                session.attachContainer(containerId)
            }
            session.refreshContainerList()
            session.refreshImageList()
        }
        .onChange(of: store.selectedContainerID) { newId in
            guard let newId,
                  let container = store.containers.first(where: { $0.id == newId }),
                  container.state == .running else { return }
            session.attachContainer(container.containerId)
        }
    }
}
