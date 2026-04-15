import SwiftUI

struct ContainerRowView: View {
    let container: PodishContainer

    var body: some View {
        HStack(spacing: 10) {
            Circle()
                .fill(container.state.color)
                .frame(width: 8, height: 8)
            VStack(alignment: .leading, spacing: 2) {
                Text(container.name)
                    .font(.body.weight(.medium))
                Text(container.image)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
        }
    }
}

struct SidebarView: View {
    @ObservedObject var store: PodishUiStore
    @Binding var selection: PodishSidebarDestination
    let onShowDetails: (PodishContainer) -> Void
    var onSelected: (() -> Void)? = nil

    var body: some View {
        #if os(macOS)
        VStack(spacing: 10) {
            Button {
                store.showNewContainer()
            } label: {
                Label("New Workspace", systemImage: "plus.rectangle.on.rectangle")
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.regular)

            containerList
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(.horizontal, 8)
        .padding(.top, 8)
        .background(.regularMaterial)
        #else
        containerList
        #endif
    }

    private var containerList: some View {
        List(selection: listSelectionBinding) {
            Section("Overview") {
                Label("Dashboard", systemImage: "house")
                    .tag(PodishSidebarDestination.home)
            }

            #if !os(macOS)
            Section("Quick Actions") {
                Button {
                    store.showNewContainer()
                } label: {
                    Label("New Workspace", systemImage: "plus.rectangle.on.rectangle")
                }
                .buttonStyle(.plain)
            }
            #endif

            Section("Active") {
                ForEach(store.runningContainers) { container in
                    HStack(spacing: 8) {
                        ContainerRowView(container: container)
                        Spacer(minLength: 4)
                        if store.pendingAction(for: container.containerId) == .stopping {
                            ProgressView()
                                .controlSize(.small)
                                .frame(width: 18, height: 18)
                        } else {
                            Button {
                                store.stop(container)
                            } label: {
                                Image(systemName: "stop.fill")
                            }
                            .buttonStyle(.borderless)
                            .disabled(store.pendingAction(for: container.containerId) != nil)
                        }
                        Button {
                            onShowDetails(container)
                        } label: {
                            Image(systemName: "info.circle")
                        }
                        .buttonStyle(.borderless)
                    }
                    .contextMenu {
                        Button(role: .destructive) {
                            store.remove(container)
                        } label: {
                            Label("Delete", systemImage: "trash")
                        }
                    }
                    #if os(iOS)
                    .swipeActions(edge: .trailing, allowsFullSwipe: true) {
                        Button(role: .destructive) {
                            store.remove(container)
                        } label: {
                            Label("Delete", systemImage: "trash")
                        }
                    }
                    #endif
                    .tag(PodishSidebarDestination.container(container.id))
                }
            }

            Section("Paused") {
                ForEach(store.containers.filter { $0.state != .running }) { container in
                    HStack(spacing: 8) {
                        ContainerRowView(container: container)
                        Spacer(minLength: 4)
                        if store.pendingAction(for: container.containerId) == .starting {
                            ProgressView()
                                .controlSize(.small)
                                .frame(width: 18, height: 18)
                        } else {
                            Button {
                                store.start(container)
                            } label: {
                                Image(systemName: "play.fill")
                            }
                            .buttonStyle(.borderless)
                            .disabled(store.pendingAction(for: container.containerId) != nil)
                        }
                        Button {
                            onShowDetails(container)
                        } label: {
                            Image(systemName: "info.circle")
                        }
                        .buttonStyle(.borderless)
                    }
                    .contextMenu {
                        Button(role: .destructive) {
                            store.remove(container)
                        } label: {
                            Label("Delete", systemImage: "trash")
                        }
                    }
                    #if os(iOS)
                    .swipeActions(edge: .trailing, allowsFullSwipe: true) {
                        Button(role: .destructive) {
                            store.remove(container)
                        } label: {
                            Label("Delete", systemImage: "trash")
                        }
                    }
                    #endif
                    .tag(PodishSidebarDestination.container(container.id))
                }
            }

            if store.runningContainers.isEmpty {
                Section {
                    Text("No running workspaces")
                        .foregroundStyle(.secondary)
                }
            }
        }
        .listStyle(.sidebar)
        .scrollContentBackground(.hidden)
        .navigationTitle("Workspaces")
        .background(Color.clear)
        .onChange(of: store.selectedContainerID) { newValue in
            guard case .container = selection else { return }
            if let newValue {
                selection = .container(newValue)
            } else {
                selection = .home
            }
        }
    }

    private var listSelectionBinding: Binding<PodishSidebarDestination?> {
        Binding<PodishSidebarDestination?>(
            get: { selection },
            set: { newValue in
                guard let newValue else { return }
                selection = newValue
                switch newValue {
                case .home:
                    onSelected?()
                case .container(let containerID):
                    if store.selectedContainerID != containerID {
                        store.selectedContainerID = containerID
                    }
                    onSelected?()
                }
            }
        )
    }
}
