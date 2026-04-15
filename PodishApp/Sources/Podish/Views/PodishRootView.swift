import SwiftUI

struct PodishRootView: View {
    var onSessionReady: ((PodishTerminalSession) -> Void)?

    @StateObject private var store = PodishUiStore()
    @StateObject private var appearance = PodishTerminalAppearance()
    @StateObject private var session: PodishTerminalSession
    @State private var splitVisibility: NavigationSplitViewVisibility = .all
    @State private var iosNavigationPath: [IOSRoute] = []
    @State private var detailsContainer: PodishContainer?
    @State private var showNewContainer = false
    @State private var sidebarSelection: PodishSidebarDestination = .home
    @State private var didBindSession = false

    init(onSessionReady: ((PodishTerminalSession) -> Void)? = nil) {
        self.onSessionReady = onSessionReady
        let appearance = PodishTerminalAppearance()
        _appearance = StateObject(wrappedValue: appearance)
        _session = StateObject(wrappedValue: PodishTerminalSession(appearance: appearance))
    }

    var body: some View {
        platformContent
            .onChange(of: store.selectedContainerID) { newId in
                #if os(macOS)
                guard let newId else { return }
                session.attachContainer(newId)
                #endif
            }
            .onChange(of: session.activeContainerId) { activeId in
                guard let activeId else { return }
                Task { @MainActor in
                    store.markRecentlyUsed(activeId)
                    guard store.selectedContainerID != activeId else { return }
                    store.selectedContainerID = activeId
                    #if os(macOS)
                    if sidebarSelection != .home {
                        sidebarSelection = .container(activeId)
                    }
                    #endif
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
        NavigationStack(path: $iosNavigationPath) {
            IOSHomeView(
                store: store,
                onOpenContainer: { container in
                    openIOSContainer(container)
                },
                onShowDetails: { container in
                    detailsContainer = container
                },
                onShowNewContainer: {
                    showNewContainer = true
                }
            )
            .navigationDestination(for: IOSRoute.self) { route in
                switch route {
                case .terminal(let containerId):
                    IOSTerminalScreen(
                        session: session,
                        containerId: containerId
                    )
                }
            }
        }
        .onAppear {
            bindSessionIfNeeded()
            onSessionReady?(session)
            session.startIfNeeded()
            store.onShowNewContainer = {
                showNewContainer = true
            }
        }
        .onChange(of: store.containers) { containers in
            if let route = iosNavigationPath.last,
               !containers.contains(where: { $0.id == route.containerId }) {
                iosNavigationPath.removeAll()
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
            #if os(macOS)
            ZStack {
                TerminalWorkspaceView(
                    session: session,
                    allowFocus: sidebarSelection != .home
                )
                .opacity(sidebarSelection == .home ? 0 : 1)
                .allowsHitTesting(sidebarSelection != .home)

                if sidebarSelection == .home {
                    HomeDashboardView {
                        store.showNewContainer()
                    }
                }
            }
            #else
            if sidebarSelection == .home {
                HomeDashboardView {
                    store.showNewContainer()
                }
            } else {
                TerminalWorkspaceView(session: session)
            }
            #endif
        }
        .navigationTitle("Podish")
    }

    private func openIOSContainer(_ container: PodishContainer) {
        store.selectedContainerID = container.containerId
        store.markRecentlyUsed(container.containerId)
        let route = IOSRoute.terminal(container.containerId)
        if iosNavigationPath.last != route {
            iosNavigationPath.append(route)
        }
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
        session.onImagePullStatus = { status in
            Task { @MainActor in
                store.updateImagePullStatus(status)
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
        store.onCreateContainer = { imageRef, name, networkMode, dnsServers, portMappings, memoryQuotaBytes in
            session.createContainer(
                from: imageRef,
                name: name,
                networkMode: networkMode,
                dnsServers: dnsServers,
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

private extension PodishRootView {
    enum IOSRoute: Hashable {
        case terminal(String)

        var containerId: String {
            switch self {
            case .terminal(let containerId):
                return containerId
            }
        }
    }
}

#if os(iOS)
private struct IOSHomeView: View {
    @ObservedObject var store: PodishUiStore
    let onOpenContainer: (PodishContainer) -> Void
    let onShowDetails: (PodishContainer) -> Void
    let onShowNewContainer: () -> Void
    private let cardInsets = EdgeInsets(top: 6, leading: 16, bottom: 6, trailing: 16)

    var body: some View {
        List {
            Section {
                IOSDashboardSummaryBar(
                    totalCount: store.containers.count,
                    runningCount: store.runningContainers.count,
                    stoppedCount: store.containers.filter { $0.state != .running }.count
                )
                .listRowInsets(cardInsets)
                .listRowBackground(Color.clear)
                .listRowSeparator(.hidden)
            }

            Section {
                if orderedContainers.isEmpty {
                    IOSDashboardEmptyState(message: "No workspaces")
                        .listRowInsets(cardInsets)
                        .listRowBackground(Color.clear)
                        .listRowSeparator(.hidden)
                } else {
                    ForEach(orderedContainers) { container in
                        IOSContainerListRow(
                            container: container,
                            pendingAction: store.pendingAction(for: container.containerId),
                            primaryActionSymbol: container.state == .running ? "stop.fill" : "play.fill",
                            onOpen: { onOpenContainer(container) },
                            onPrimaryAction: {
                                if container.state == .running {
                                    store.stop(container)
                                } else {
                                    store.start(container)
                                }
                            },
                            onShowDetails: { onShowDetails(container) },
                            onDelete: { store.remove(container) }
                        )
                        .id(container.id)
                        .listRowInsets(cardInsets)
                        .listRowBackground(Color.clear)
                        .listRowSeparator(.hidden)
                    }
                }
            } header: {
                Text("Workspaces")
            }
        }
        .listStyle(.plain)
        .scrollContentBackground(.hidden)
        .background(Color(uiColor: .systemGroupedBackground))
        .navigationTitle("Podish")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button(action: onShowNewContainer) {
                    Image(systemName: "plus")
                }
                .accessibilityLabel("Create Workspace")
            }
        }
    }

    private var orderedContainers: [PodishContainer] {
        let recentRanks = Dictionary(
            uniqueKeysWithValues: store.recentContainerIDs.enumerated().map { ($0.element, $0.offset) }
        )

        return store.containers.sorted { lhs, rhs in
            let lhsRunningRank = lhs.state == .running ? 0 : 1
            let rhsRunningRank = rhs.state == .running ? 0 : 1
            if lhsRunningRank != rhsRunningRank {
                return lhsRunningRank < rhsRunningRank
            }

            let lhsRecentRank = recentRanks[lhs.containerId] ?? Int.max
            let rhsRecentRank = recentRanks[rhs.containerId] ?? Int.max
            if lhsRecentRank != rhsRecentRank {
                return lhsRecentRank < rhsRecentRank
            }

            return lhs.name.localizedCaseInsensitiveCompare(rhs.name) == .orderedAscending
        }
    }
}

private struct IOSDashboardSummaryBar: View {
    let totalCount: Int
    let runningCount: Int
    let stoppedCount: Int

    var body: some View {
        HStack(spacing: 10) {
            IOSDashboardMetricPill(value: "\(runningCount)", title: "Active")
            IOSDashboardMetricPill(value: "\(stoppedCount)", title: "Paused")
            IOSDashboardMetricPill(value: "\(totalCount)", title: "Total")
        }
    }
}

private struct IOSDashboardMetricPill: View {
    let value: String
    let title: String

    var body: some View {
        VStack(spacing: 6) {
            Text(title)
                .font(.caption)
                .foregroundStyle(.secondary)
            Text(value)
                .font(.title2.weight(.bold))
        }
        .frame(maxWidth: .infinity, minHeight: 84)
        .multilineTextAlignment(.center)
        .padding(.horizontal, 12)
        .padding(.vertical, 12)
        .background {
            RoundedRectangle(cornerRadius: 18, style: .continuous)
                .fill(Color(uiColor: .secondarySystemGroupedBackground))
        }
    }
}

private struct IOSDashboardEmptyState: View {
    let message: String

    var body: some View {
        Text(message)
            .foregroundStyle(.secondary)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(16)
            .background {
                RoundedRectangle(cornerRadius: 18, style: .continuous)
                    .fill(Color(uiColor: .secondarySystemGroupedBackground))
            }
    }
}

private struct IOSContainerListRow: View {
    let container: PodishContainer
    let pendingAction: PodishUiStore.PendingContainerAction?
    let primaryActionSymbol: String
    let onOpen: () -> Void
    let onPrimaryAction: () -> Void
    let onShowDetails: () -> Void
    let onDelete: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            ContainerRowView(container: container)
            Spacer(minLength: 4)
            if pendingAction != nil {
                ProgressView()
                    .controlSize(.small)
                    .frame(width: 28, height: 28)
            } else {
                IOSRowCircleButton(systemImage: primaryActionSymbol, action: onPrimaryAction)
            }
            IOSRowCircleButton(systemImage: "info.circle", action: onShowDetails)
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 14)
        .background {
            RoundedRectangle(cornerRadius: 18, style: .continuous)
                .fill(Color(uiColor: .secondarySystemGroupedBackground))
        }
        .contentShape(RoundedRectangle(cornerRadius: 18, style: .continuous))
        .onTapGesture(perform: onOpen)
        .swipeActions(edge: .trailing, allowsFullSwipe: true) {
            Button(role: .destructive, action: onDelete) {
                Image(systemName: "trash")
            }
        }
    }
}

private struct IOSRowCircleButton: View {
    let systemImage: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: systemImage)
                .font(.system(size: 14, weight: .semibold))
                .frame(width: 28, height: 28)
                .background(Color(uiColor: .tertiarySystemFill), in: Circle())
        }
        .buttonStyle(.plain)
    }
}

private struct IOSTerminalScreen: View {
    @ObservedObject var session: PodishTerminalSession
    let containerId: String
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        TerminalWorkspaceView(session: session)
            .background(session.terminalBackgroundColor)
            .toolbar(.hidden, for: .navigationBar)
            .overlay(alignment: .leading) {
                Color.clear
                    .frame(width: 28)
                    .contentShape(Rectangle())
                    .gesture(
                        DragGesture(minimumDistance: 12)
                            .onEnded { value in
                                guard value.startLocation.x < 28 else { return }
                                guard value.translation.width > 80 else { return }
                                guard abs(value.translation.height) < 60 else { return }
                                dismiss()
                            }
                    )
            }
            .onAppear {
                session.attachContainer(containerId)
            }
    }
}
#endif

private struct HomeDashboardView: View {
    let onAddContainer: () -> Void

    var body: some View {
        VStack(spacing: 16) {
            Image(systemName: "cube.transparent")
                .font(.system(size: 46))
                .foregroundStyle(.secondary)

            Text("Podish")
                .font(.largeTitle.weight(.semibold))

            Text("Launch and manage environments")
                .font(.body)
                .foregroundStyle(.secondary)

            Button {
                onAddContainer()
            } label: {
                Label("Add Workspace", systemImage: "plus.rectangle.on.rectangle")
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
