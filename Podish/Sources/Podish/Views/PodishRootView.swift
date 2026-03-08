import SwiftUI

struct PodishRootView: View {
    @StateObject private var store = PodishUiStore()
    @State private var splitVisibility: NavigationSplitViewVisibility = .all
    @State private var detailsContainer: PodishContainer?
    @State private var showNewContainer = false
    @State private var showSidebar = false
    @State private var sidebarSelection: PodishSidebarDestination = .home

    var body: some View {
        #if os(macOS)
        NavigationSplitView(columnVisibility: $splitVisibility) {
            SidebarView(store: store, selection: $sidebarSelection) { container in
                detailsContainer = container
            }
            .navigationSplitViewColumnWidth(min: 300, ideal: 340, max: 420)
        } detail: {
            detailContent
        }
        .navigationSplitViewStyle(.automatic)
        .onAppear {
            store.onShowNewContainer = {
                DispatchQueue.main.async {
                    showNewContainer = true
                }
            }
        }
        .sheet(item: $detailsContainer) { container in
            ContainerDetailsSheetView(container: container)
        }
        .sheet(isPresented: $showNewContainer) {
            NewContainerSheetView(store: store)
        }
        #else
        NavigationStack {
            detailContent
                .navigationTitle("Podish")
                .navigationBarTitleDisplayMode(.inline)
                .toolbar {
                    ToolbarItem(placement: .topBarLeading) {
                        Button {
                            showSidebar = true
                        } label: {
                            Label("Containers", systemImage: "line.3.horizontal")
                        }
                    }
                }
        }
        .onAppear {
            store.onShowNewContainer = {
                DispatchQueue.main.async {
                    if showSidebar {
                        showSidebar = false
                        DispatchQueue.main.async {
                            showNewContainer = true
                        }
                    } else {
                        showNewContainer = true
                    }
                }
            }
        }
        .sheet(isPresented: $showSidebar) {
            NavigationStack {
                SidebarView(
                    store: store,
                    selection: $sidebarSelection,
                    onShowDetails: { container in
                        if showSidebar {
                            showSidebar = false
                            DispatchQueue.main.async {
                                detailsContainer = container
                            }
                        } else {
                            detailsContainer = container
                        }
                    },
                    onSelected: {
                        showSidebar = false
                    }
                )
                .toolbar {
                    ToolbarItem(placement: .cancellationAction) {
                        Button("Done") {
                            showSidebar = false
                        }
                    }
                }
            }
        }
        .sheet(item: $detailsContainer) { container in
            ContainerDetailsSheetView(container: container)
        }
        .sheet(isPresented: $showNewContainer) {
            NewContainerSheetView(store: store)
        }
        #endif
    }

    private var detailContent: some View {
        Group {
            if sidebarSelection == .home {
                HomeDashboardView {
                    store.showNewContainer()
                }
            } else {
                TerminalWorkspaceView(store: store)
            }
        }
        .navigationTitle("Podish")
    }
}

private struct HomeDashboardView: View {
    let onAddContainer: () -> Void

    var body: some View {
        VStack(spacing: 16) {
            Image(systemName: "cube.transparent")
                .font(.system(size: 46))
                .foregroundStyle(.secondary)

            Text("Podish")
                .font(.largeTitle.weight(.semibold))

            Text("Create and manage containers")
                .font(.body)
                .foregroundStyle(.secondary)

            Button {
                onAddContainer()
            } label: {
                Label("Add Container", systemImage: "plus.rectangle.on.rectangle")
                    .frame(minWidth: 200)
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.large)
            .keyboardShortcut("n", modifiers: [.command])
        }
        .padding(24)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
