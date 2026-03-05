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
    let onShowDetails: (PodishContainer) -> Void
    @State private var localSelection: String?

    var body: some View {
        List(selection: $localSelection) {
            Section("Actions") {
                Button {
                    store.showNewContainer()
                } label: {
                    Label("New Container", systemImage: "plus.rectangle.on.rectangle")
                }
                .buttonStyle(.plain)
            }

            Section("Running") {
                ForEach(store.runningContainers) { container in
                    HStack(spacing: 8) {
                        ContainerRowView(container: container)
                        Spacer(minLength: 4)
                        Button {
                            store.stop(container)
                        } label: {
                            Image(systemName: "stop.fill")
                        }
                        .buttonStyle(.borderless)
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
                    .tag(container.id)
                }
            }

            Section("Stopped") {
                ForEach(store.containers.filter { $0.state != .running }) { container in
                    HStack(spacing: 8) {
                        ContainerRowView(container: container)
                        Spacer(minLength: 4)
                        Button {
                            store.start(container)
                        } label: {
                            Image(systemName: "play.fill")
                        }
                        .buttonStyle(.borderless)
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
                    .tag(container.id)
                }
            }

            if store.runningContainers.isEmpty {
                Section {
                    Text("No running containers")
                        .foregroundStyle(.secondary)
                }
            }
        }
        .listStyle(.sidebar)
        .navigationTitle("Containers")
        .onAppear {
            localSelection = store.selectedContainerID
        }
        .onChange(of: localSelection) { newValue in
            guard store.selectedContainerID != newValue else { return }
            DispatchQueue.main.async {
                if store.selectedContainerID != newValue {
                    store.selectedContainerID = newValue
                }
            }
        }
        .onChange(of: store.selectedContainerID) { newValue in
            guard localSelection != newValue else { return }
            DispatchQueue.main.async {
                if localSelection != newValue {
                    localSelection = newValue
                }
            }
        }
    }
}
