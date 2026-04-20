import SwiftUI

struct NewContainerSheetView: View {
    private enum DnsMode: String, CaseIterable, Identifiable {
        case host
        case custom

        var id: String { rawValue }

        var displayName: String {
            switch self {
            case .host:
                return "Automatic"
            case .custom:
                return "Custom"
            }
        }
    }

    private enum LaunchMode: String, CaseIterable, Identifiable {
        case ociDefault
        case custom

        var id: String { rawValue }

        var displayName: String {
            switch self {
            case .ociDefault:
                return "OCI Default"
            case .custom:
                return "Custom Entry"
            }
        }
    }

    @ObservedObject var store: PodishUiStore
    @Environment(\.dismiss) private var dismiss

    @State private var pullImageRef = PodishSeedImageConfig.imageReference
    @State private var selectedImageId: String?
    @State private var containerName = ""
    @State private var memoryLimitText = "\(PodishMemoryLimits.defaultMemoryQuotaMB)"
    @State private var networkMode: PodishNetworkMode = .host
    @State private var dnsMode: DnsMode = .host
    @State private var launchMode: LaunchMode = .ociDefault
    @State private var dnsServersText = ""
    @State private var portMappingsText = ""
    @State private var customExecutable = ""
    @State private var customArgumentsText = ""
    @State private var createError: String?

    var body: some View {
        NavigationStack {
            VStack(spacing: 12) {
                pullSection
                nameSection
                launchSection
                memorySection
                networkSection
                dnsSection
                imageListSection
                if let createError {
                    Text(createError)
                        .font(.caption)
                        .foregroundStyle(.red)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
            .padding()
            .navigationTitle("New Workspace")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Close") {
                        dismiss()
                    }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("Create") {
                        if let imageRef = selectedImageRef {
                            let trimmed = containerName.trimmingCharacters(in: .whitespacesAndNewlines)
                            let mappings: [PodishPortMapping]
                            let dnsServers: [String]
                            let memoryQuotaBytes: Int64?
                            let customLaunchExecutable: String?
                            let customLaunchArguments: [String]
                            do {
                                mappings = try parsePortMappings()
                                dnsServers = try parseDnsServers()
                                memoryQuotaBytes = try parseMemoryQuotaBytes()
                                (customLaunchExecutable, customLaunchArguments) = try parseLaunchConfiguration()
                            } catch {
                                createError = error.localizedDescription
                                return
                            }

                            createError = nil
                            store.createContainer(
                                fromImage: imageRef,
                                name: trimmed.isEmpty ? nil : trimmed,
                                networkMode: networkMode,
                                dnsServers: dnsServers,
                                portMappings: mappings,
                                memoryQuotaBytes: memoryQuotaBytes,
                                customExecutable: customLaunchExecutable,
                                customArguments: customLaunchArguments
                            )
                            dismiss()
                        }
                    }
                    .disabled(selectedImageRef == nil)
                }
            }
            .onAppear {
                if selectedImageRef == nil {
                    selectedImageId = store.images.first?.id
                }
            }
            .onChange(of: store.images) { newImages in
                if let selectedImageId,
                   newImages.contains(where: { $0.id == selectedImageId }) {
                    return
                }
                self.selectedImageId = newImages.first?.id
            }
        }
        #if os(macOS)
        .frame(minWidth: 560, minHeight: 420)
        #endif
    }

    private var pullSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Add Environment")
                .font(.headline)

            HStack(spacing: 8) {
                TextField(PodishSeedImageConfig.imageReference, text: $pullImageRef)
                    #if os(macOS)
                    .textFieldStyle(.roundedBorder)
                    #endif
                Button("Add") {
                    let trimmed = pullImageRef.trimmingCharacters(in: .whitespacesAndNewlines)
                    guard !trimmed.isEmpty else { return }
                    store.pullImage(trimmed)
                    pullImageRef = trimmed
                }
                .buttonStyle(.borderedProminent)
                .disabled(store.imagePullStatus?.isActive == true)
            }

            if let status = store.imagePullStatus {
                imagePullStatusCard(status)
            }
        }
    }

    private var imageListSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Environments")
                .font(.headline)

            if store.images.isEmpty {
                if #available(iOS 17.0, macOS 14.0, *) {
                    ContentUnavailableView("No Environments", systemImage: "shippingbox", description: Text("Add an environment to continue."))
                } else {
                    VStack(spacing: 8) {
                        Image(systemName: "shippingbox")
                            .font(.title2)
                            .foregroundStyle(.secondary)
                        Text("No Environments")
                            .font(.headline)
                        Text("Add an environment to continue.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    .frame(maxWidth: .infinity, minHeight: 160)
                }
            } else {
                List(selection: $selectedImageId) {
                    ForEach(store.images) { image in
                        let blockedReason = store.imageRemovalBlockedReason(image.repoTag)
                        HStack(spacing: 8) {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(image.repoTag)
                                    .font(.body)
                                    .lineLimit(1)
                                Text(image.digest)
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                                    .lineLimit(1)
                                if let blockedReason {
                                    Text(blockedReason)
                                        .font(.caption)
                                        .foregroundStyle(.orange)
                                        .lineLimit(1)
                                }
                            }
                            Spacer()
                            Button(role: .destructive) {
                                store.removeImage(image.repoTag)
                            } label: {
                                Image(systemName: "trash")
                            }
                            .buttonStyle(.borderless)
                            .disabled(blockedReason != nil)
                        }
                        .tag(image.id)
                    }
                }
                .listStyle(.inset)
                .frame(minHeight: 180, maxHeight: 240)
            }
        }
    }

    private var nameSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Workspace Name (Optional)")
                .font(.headline)
            TextField("my-workspace", text: $containerName)
                #if os(macOS)
                .textFieldStyle(.roundedBorder)
                #endif
        }
    }

    private var memorySection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Workspace Memory (MB, optional)")
                .font(.headline)
            TextField("2048", text: $memoryLimitText)
                #if os(macOS)
                .textFieldStyle(.roundedBorder)
                #endif
            Text("Minimum \(PodishMemoryLimits.minimumMemoryQuotaMB) MB. Leave blank to use the default workspace limit.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private var launchSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Launch")
                .font(.headline)
            Picker("Launch", selection: $launchMode) {
                ForEach(LaunchMode.allCases) { mode in
                    Text(mode.displayName).tag(mode)
                }
            }
            .pickerStyle(.segmented)

            Text(launchMode == .ociDefault
                 ? "Use the image's OCI Entrypoint/Cmd."
                 : "Override the image command with a custom executable and one argument per line.")
                .font(.caption)
                .foregroundStyle(.secondary)

            if launchMode == .custom {
                TextField("/bin/sh", text: $customExecutable)
                    #if os(macOS)
                    .textFieldStyle(.roundedBorder)
                    #endif
                TextEditor(text: $customArgumentsText)
                    .frame(minHeight: 90)
                    .overlay(
                        RoundedRectangle(cornerRadius: 6)
                            .stroke(.secondary.opacity(0.25), lineWidth: 1)
                    )
                Text("Arguments are passed literally, one per line.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
    }

    private var networkSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Connection")
                .font(.headline)
            Picker("Connection", selection: $networkMode) {
                ForEach(PodishNetworkMode.allCases) { mode in
                    Text(mode.displayName).tag(mode)
                }
            }
            .pickerStyle(.segmented)

            if networkMode == .privateNet {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Service Access (devicePort:workspacePort, one per line)")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                    TextEditor(text: $portMappingsText)
                        .frame(minHeight: 90)
                        .overlay(
                            RoundedRectangle(cornerRadius: 6)
                                .stroke(.secondary.opacity(0.25), lineWidth: 1)
                        )
                }
            }
        }
    }

    private var dnsSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Name Resolution")
                .font(.headline)
            Picker("Name Resolution", selection: $dnsMode) {
                ForEach(DnsMode.allCases) { mode in
                    Text(mode.displayName).tag(mode)
                }
            }
            .pickerStyle(.segmented)

            Text(dnsMode == .host
                 ? "Use the default name resolution settings."
                 : "Enter one resolver per line. Commas and semicolons are also supported.")
                .font(.caption)
                .foregroundStyle(.secondary)

            if dnsMode == .custom {
                TextEditor(text: $dnsServersText)
                    .frame(minHeight: 90)
                    .overlay(
                        RoundedRectangle(cornerRadius: 6)
                            .stroke(.secondary.opacity(0.25), lineWidth: 1)
                    )
            }
        }
    }

    private var selectedImageRef: String? {
        guard let selectedImageId else { return nil }
        return store.images.first(where: { $0.id == selectedImageId })?.repoTag
    }

    @ViewBuilder
    private func imagePullStatusCard(_ status: PodishImagePullStatus) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Image(systemName: statusIconName(status.phase))
                    .foregroundStyle(statusTint(status.phase))
                Text(statusTitle(status))
                    .font(.subheadline.weight(.semibold))
                Spacer()
                if let progressText = progressText(status) {
                    Text(progressText)
                        .font(.caption.monospacedDigit())
                        .foregroundStyle(.secondary)
                }
            }

            if let fraction = status.progressFraction {
                ProgressView(value: fraction)
                    .progressViewStyle(.linear)
            } else if status.isActive {
                ProgressView()
                    .progressViewStyle(.linear)
            }

            Text(status.message)
                .font(.caption)
                .foregroundStyle(.secondary)

            HStack(spacing: 12) {
                if let layerText = layerText(status) {
                    Label(layerText, systemImage: "square.stack.3d.down.forward")
                }
                if let bytesText = bytesText(status) {
                    Label(bytesText, systemImage: "arrow.down.circle")
                }
            }
            .font(.caption)
            .foregroundStyle(.secondary)
        }
        .padding(10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .fill(statusTint(status.phase).opacity(0.10))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .stroke(statusTint(status.phase).opacity(0.18), lineWidth: 1)
        )
    }

    private func statusTitle(_ status: PodishImagePullStatus) -> String {
        switch status.phase {
        case .resolving:
            return "Resolving \(status.imageReference)"
        case .downloading:
            return "Adding \(status.imageReference)"
        case .extracting:
            return "Preparing \(status.imageReference)"
        case .completed:
            return "Added \(status.imageReference)"
        case .failed:
            return "Failed to add \(status.imageReference)"
        }
    }

    private func statusIconName(_ phase: PodishImagePullPhase) -> String {
        switch phase {
        case .resolving:
            return "magnifyingglass"
        case .downloading:
            return "arrow.down.circle.fill"
        case .extracting:
            return "shippingbox.fill"
        case .completed:
            return "checkmark.circle.fill"
        case .failed:
            return "xmark.octagon.fill"
        }
    }

    private func statusTint(_ phase: PodishImagePullPhase) -> Color {
        switch phase {
        case .resolving:
            return .blue
        case .downloading:
            return .teal
        case .extracting:
            return .orange
        case .completed:
            return .green
        case .failed:
            return .red
        }
    }

    private func progressText(_ status: PodishImagePullStatus) -> String? {
        guard let fraction = status.progressFraction else { return nil }
        return "\(Int((fraction * 100).rounded()))%"
    }

    private func layerText(_ status: PodishImagePullStatus) -> String? {
        guard let layerIndex = status.layerIndex, let layerCount = status.layerCount, layerCount > 0 else {
            return nil
        }
        return "Layer \(layerIndex)/\(layerCount)"
    }

    private func bytesText(_ status: PodishImagePullStatus) -> String? {
        guard let totalBytes = status.overallTotalBytes, totalBytes > 0 else { return nil }
        let formatter = ByteCountFormatter()
        formatter.allowedUnits = [.useMB, .useGB]
        formatter.countStyle = .file
        let completed = formatter.string(fromByteCount: status.overallBytes ?? 0)
        let total = formatter.string(fromByteCount: totalBytes)
        return "\(completed) / \(total)"
    }

    private func parsePortMappings() throws -> [PodishPortMapping] {
        if networkMode != .privateNet {
            return []
        }

        let lines = portMappingsText
            .split(whereSeparator: \.isNewline)
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
        if lines.isEmpty {
            return []
        }

        var result: [PodishPortMapping] = []
        var hostPorts = Set<Int>()
        for line in lines {
            let parts = line.split(separator: ":", omittingEmptySubsequences: false)
            guard parts.count == 2,
                  let host = Int(parts[0]),
                  let container = Int(parts[1]),
                  (1...65535).contains(host),
                  (1...65535).contains(container) else {
                throw ParseError("Invalid mapping '\(line)'. Expected hostPort:containerPort with ports 1-65535.")
            }
            if hostPorts.contains(host) {
                throw ParseError("Duplicate host port \(host).")
            }
            hostPorts.insert(host)
            result.append(PodishPortMapping(hostPort: host, containerPort: container))
        }
        return result
    }

    private func parseMemoryQuotaBytes() throws -> Int64? {
        let trimmed = memoryLimitText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            return nil
        }

        guard let memoryMB = Int64(trimmed), memoryMB > 0 else {
            throw ParseError("Memory limit must be a positive integer number of MB.")
        }
        guard memoryMB >= Int64(PodishMemoryLimits.minimumMemoryQuotaMB) else {
            throw ParseError("Memory limit must be at least \(PodishMemoryLimits.minimumMemoryQuotaMB) MB.")
        }
        return memoryMB * PodishMemoryLimits.bytesPerMiB
    }

    private func parseDnsServers() throws -> [String] {
        guard dnsMode == .custom else {
            return []
        }

        let servers = dnsServersText
            .components(separatedBy: CharacterSet(charactersIn: ",;\n"))
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }

        guard !servers.isEmpty else {
            throw ParseError("Enter at least one resolver, or switch Name Resolution back to Automatic.")
        }

        for server in servers where server.contains(where: { $0.isWhitespace }) {
            throw ParseError("Resolver '\(server)' is invalid.")
        }

        return servers
    }

    private func parseLaunchConfiguration() throws -> (String?, [String]) {
        guard launchMode == .custom else {
            return (nil, [])
        }

        let executable = customExecutable.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !executable.isEmpty else {
            throw ParseError("Executable is required when using Custom Entry.")
        }

        let arguments = customArgumentsText
            .split(whereSeparator: \.isNewline)
            .map(String.init)
        return (executable, arguments)
    }

    private struct ParseError: LocalizedError {
        let message: String
        init(_ message: String) { self.message = message }
        var errorDescription: String? { message }
    }
}
