import SwiftUI

struct PodishRootView: View {
    @StateObject private var store = PodishUiStore()
    @State private var splitVisibility: NavigationSplitViewVisibility = .all
    @State private var detailsContainer: PodishContainer?
    @State private var showNewContainer = false
    @State private var showSidebar = false

    var body: some View {
        #if os(macOS)
        NavigationSplitView(columnVisibility: $splitVisibility) {
            SidebarView(store: store) { container in
                detailsContainer = container
            }
            .navigationSplitViewColumnWidth(min: 300, ideal: 340, max: 420)
        } detail: {
            TerminalWorkspaceView(store: store)
                .navigationTitle("Podish")
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
            TerminalWorkspaceView(store: store)
                .navigationTitle("Podish")
                .navigationBarTitleDisplayMode(.inline)
                .toolbar {
                    ToolbarItem(placement: .topBarLeading) {
                        Button {
                            showSidebar = true
                        } label: {
                            Label("Containers", systemImage: "sidebar.left")
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
                SidebarView(store: store, onShowDetails: { container in
                    detailsContainer = container
                }, onSelected: {
                    showSidebar = false
                })
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
}
