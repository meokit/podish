import SwiftUI

struct ContainerDetailsSheetView: View {
    let container: PodishContainer
    @ObservedObject var session: PodishTerminalSession
    @Environment(\.dismiss) private var dismiss
    @State private var selectedTab = 1
    @State private var inspect: NativeContainerInspect?
    @State private var logs: [NativeContainerLogEntry] = []
    @State private var isLoadingInspect = false
    @State private var isLoadingLogs = false
    @State private var inspectError: String?
    @State private var logsError: String?

    var body: some View {
        NavigationStack {
            VStack(alignment: .leading, spacing: 12) {
                Picker("Tab", selection: $selectedTab) {
                    Text("Logs").tag(1)
                    Text("Inspect").tag(2)
                    Text("Mounts").tag(3)
                }
                .pickerStyle(.segmented)

                Group {
                    switch selectedTab {
                    case 1:
                        logsView
                    case 2:
                        inspectView
                    default:
                        mountsView
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
            .padding(16)
            .navigationTitle(container.name)
            .task(id: container.containerId) {
                reloadInspect()
                reloadLogs()
            }
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Close") { dismiss() }
                }
                ToolbarItem(placement: .primaryAction) {
                    Button {
                        reloadInspect()
                        reloadLogs()
                    } label: {
                        Image(systemName: "arrow.clockwise")
                    }
                }
            }
        }
        #if os(macOS)
        .frame(minWidth: 880, minHeight: 560)
        #endif
    }

    private var logsView: some View {
        ScrollView {
            if isLoadingLogs && logs.isEmpty {
                ProgressView("Loading logs…")
                    .frame(maxWidth: .infinity, alignment: .leading)
            } else if let logsError {
                Text(logsError)
                    .foregroundStyle(.red)
                    .frame(maxWidth: .infinity, alignment: .leading)
            } else if logs.isEmpty {
                Text("No logs")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
            } else {
                Text(logs.map { "[\($0.stream)] \($0.log)".trimmingCharacters(in: .whitespacesAndNewlines) }.joined(separator: "\n"))
                    .font(.system(.body, design: .monospaced))
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .padding(12)
        .background(.quaternary.opacity(0.2), in: RoundedRectangle(cornerRadius: 12))
    }

    private var inspectView: some View {
        Group {
            if isLoadingInspect && inspect == nil {
                ProgressView("Loading inspect…")
            } else if let inspectError {
                Text(inspectError)
                    .foregroundStyle(.red)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            } else if let inspect {
                Text(inspectText(for: inspect))
                    .font(.system(.body, design: .monospaced))
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            } else {
                Text("No inspect data")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            }
        }
    }

    private var mountsView: some View {
        Group {
            if let inspect {
                let volumes = inspect.spec.volumes
                if volumes.isEmpty {
                    Text("No explicit mounts")
                        .foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                } else {
                    Text(volumes.joined(separator: "\n"))
                        .font(.system(.body, design: .monospaced))
                        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                }
            } else if let inspectError {
                Text(inspectError)
                    .foregroundStyle(.red)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            } else {
                ProgressView("Loading mounts…")
            }
        }
    }

    private func inspectText(for inspect: NativeContainerInspect) -> String {
        let ports = inspect.spec.publishedPorts.map { "\($0.hostPort):\($0.containerPort)" }
        return """
        Name: \(inspect.name)
        Container ID: \(inspect.containerId)
        Image: \(inspect.image)
        State: \(inspect.state)
        Running: \(inspect.running ? "true" : "false")
        Exit Code: \(inspect.exitCode.map(String.init) ?? "-")
        Exe: \(inspect.spec.exe ?? "-")
        Args: \(inspect.spec.exeArgs.joined(separator: " "))
        Network Mode: \(inspect.spec.networkMode)
        Ports: \(ports.isEmpty ? "-" : ports.joined(separator: ", "))
        """
    }

    private func reloadInspect() {
        isLoadingInspect = true
        inspectError = nil
        session.fetchContainerInspect(container.containerId) { result in
            isLoadingInspect = false
            switch result {
            case .success(let value):
                inspect = value
            case .failure(let error):
                inspectError = error.localizedDescription
            }
        }
    }

    private func reloadLogs() {
        isLoadingLogs = true
        logsError = nil
        session.fetchContainerLogs(container.containerId, cursor: nil, follow: false, timeoutMs: 0) { result in
            isLoadingLogs = false
            switch result {
            case .success(let chunk):
                logs = chunk.entries
            case .failure(let error):
                logsError = error.localizedDescription
            }
        }
    }
}
