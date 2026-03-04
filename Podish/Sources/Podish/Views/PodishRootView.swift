import SwiftUI

struct PodishRootView: View {
    @StateObject private var store = PodishUiStore()
    @State private var splitVisibility: NavigationSplitViewVisibility = .all
    @State private var detailsContainer: PodishContainer?
    @State private var showNewContainer = false

    var body: some View {
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
    }
}
