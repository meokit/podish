import SwiftUI

struct PodishRootView: View {
    var onSessionReady: ((PodishTerminalSession) -> Void)?

    @StateObject private var store = PodishUiStore()
    @StateObject private var session = PodishTerminalSession()
    @State private var splitVisibility: NavigationSplitViewVisibility = .all
    @State private var detailsContainer: PodishContainer?
    @State private var showNewContainer = false
    @State private var showSidebar = false
    @State private var sidebarSelection: PodishSidebarDestination = .home
    @State private var didBindSession = false

    var body: some View {
        platformContent
            .onChange(of: store.selectedContainerID) { newId in
                guard let newId else { return }
                session.attachContainer(newId)
            }
            .onChange(of: session.activeContainerId) { activeId in
                guard let activeId, store.selectedContainerID != activeId else { return }
                if store.selectedContainerID != activeId {
                    store.selectedContainerID = activeId
                }
                if sidebarSelection != .home {
                    sidebarSelection = .container(activeId)
                }
            }
    }

    @ViewBuilder
    private var platformContent: some View {
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
            bindSessionIfNeeded()
            onSessionReady?(session)
            session.startIfNeeded()
            store.onShowNewContainer = {
                showNewContainer = true
            }
        }
        .sheet(item: $detailsContainer) { container in
            ContainerDetailsSheetView(container: container, session: session)
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
            bindSessionIfNeeded()
            onSessionReady?(session)
            session.startIfNeeded()
            store.onShowNewContainer = {
                if showSidebar {
                    showSidebar = false
                    Task { @MainActor in
                        showNewContainer = true
                    }
                } else {
                    showNewContainer = true
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
                            Task { @MainActor in
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
            ContainerDetailsSheetView(container: container, session: session)
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
                TerminalWorkspaceView(session: session)
            }
        }
        .navigationTitle("Podish")
    }

    private func bindSessionIfNeeded() {
        guard !didBindSession else { return }
        didBindSession = true

        session.onContainerList = { items in
            Task { @MainActor in
                store.applyContainerList(items)
            }
        }
        session.onImageList = { items in
            Task { @MainActor in
                store.applyImageList(items)
            }
        }
        session.onContainerStateChanged = { items in
            Task { @MainActor in
                store.applyContainerList(items)
                if let selectedId = store.selectedContainerID {
                    session.attachContainer(selectedId)
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
        store.onCreateContainer = { imageRef, name, networkMode, portMappings, memoryQuotaBytes in
            session.createContainer(
                from: imageRef,
                name: name,
                networkMode: networkMode,
                portMappings: portMappings,
                memoryQuotaBytes: memoryQuotaBytes
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
