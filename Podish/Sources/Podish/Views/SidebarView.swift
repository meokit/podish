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

    var body: some View {
        List(selection: $store.selectedContainerID) {
            Section("Actions") {
                Button {
                } label: {
                    Label("Pull Image", systemImage: "arrow.down.circle")
                }
                .buttonStyle(.plain)

                Button {
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
                            onShowDetails(container)
                        } label: {
                            Image(systemName: "info.circle")
                        }
                        .buttonStyle(.borderless)
                    }
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
    }
}
