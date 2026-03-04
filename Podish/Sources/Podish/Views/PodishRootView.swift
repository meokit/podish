import SwiftUI

struct PodishRootView: View {
    @StateObject private var store = PodishUiStore()
    @State private var splitVisibility: NavigationSplitViewVisibility = .all
    @State private var detailsContainer: PodishContainer?

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
        .sheet(item: $detailsContainer) { container in
            ContainerDetailsSheetView(container: container)
        }
    }
}
