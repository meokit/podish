import SwiftUI

struct TerminalWorkspaceView: View {
    @ObservedObject var store: PodishUiStore
    @StateObject private var session = PodishTerminalSession()

    var body: some View {
        ZStack(alignment: .topLeading) {
            TerminalViewHost(terminalView: session.terminalView)
                .background(Color.black)
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
                session.startContainer(containerId)
            }
            store.onStopContainer = { containerId in
                session.stopContainer(containerId)
            }
            store.onRemoveContainer = { containerId in
                session.removeContainer(containerId)
            }
            store.onAttachContainer = { containerId in
                session.attachContainer(containerId)
            }
            session.refreshContainerList()
        }
        .onChange(of: store.selectedContainerID) { _, newId in
            guard let newId,
                  let container = store.containers.first(where: { $0.id == newId }),
                  container.state == .running else { return }
            session.attachContainer(container.containerId)
        }
    }
}
