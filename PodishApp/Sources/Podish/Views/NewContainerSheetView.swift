import SwiftUI

struct NewContainerSheetView: View {
    @ObservedObject var store: PodishUiStore
    @Environment(\.dismiss) private var dismiss

    @State private var pullImageRef = "docker.io/i386/alpine:latest"
    @State private var selectedImageId: String?
    @State private var containerName = ""
    @State private var memoryLimitText = "\(PodishMemoryLimits.defaultMemoryQuotaMB)"
    @State private var networkMode: PodishNetworkMode = .host
    @State private var portMappingsText = ""
    @State private var createError: String?

    var body: some View {
        NavigationStack {
            VStack(spacing: 12) {
                pullSection
                nameSection
                memorySection
                networkSection
                imageListSection
                if let createError {
                    Text(createError)
                        .font(.caption)
                        .foregroundStyle(.red)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
            .padding()
            .navigationTitle("New Container")
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
                            let memoryQuotaBytes: Int64?
                            do {
                                mappings = try parsePortMappings()
                                memoryQuotaBytes = try parseMemoryQuotaBytes()
                            } catch {
                                createError = error.localizedDescription
                                return
                            }

                            createError = nil
                            store.createContainer(
                                fromImage: imageRef,
                                name: trimmed.isEmpty ? nil : trimmed,
                                networkMode: networkMode,
                                portMappings: mappings,
                                memoryQuotaBytes: memoryQuotaBytes
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
            Text("Pull Image")
                .font(.headline)

            HStack(spacing: 8) {
                TextField("docker.io/library/alpine:latest", text: $pullImageRef)
                    #if os(macOS)
                    .textFieldStyle(.roundedBorder)
                    #endif
                Button("Pull") {
                    let trimmed = pullImageRef.trimmingCharacters(in: .whitespacesAndNewlines)
                    guard !trimmed.isEmpty else { return }
                    store.pullImage(trimmed)
                    pullImageRef = trimmed
                }
                .buttonStyle(.borderedProminent)
            }
        }
    }

    private var imageListSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Images")
                .font(.headline)

            if store.images.isEmpty {
                if #available(iOS 17.0, macOS 14.0, *) {
                    ContentUnavailableView("No Images", systemImage: "shippingbox", description: Text("Pull an image to continue."))
                } else {
                    VStack(spacing: 8) {
                        Image(systemName: "shippingbox")
                            .font(.title2)
                            .foregroundStyle(.secondary)
                        Text("No Images")
                            .font(.headline)
                        Text("Pull an image to continue.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    .frame(maxWidth: .infinity, minHeight: 160)
                }
            } else {
                List(selection: $selectedImageId) {
                    ForEach(store.images) { image in
                        HStack(spacing: 8) {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(image.repoTag)
                                    .font(.body)
                                    .lineLimit(1)
                                Text(image.digest)
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
                                    .lineLimit(1)
                            }
                            Spacer()
                            Button(role: .destructive) {
                                store.removeImage(image.repoTag)
                            } label: {
                                Image(systemName: "trash")
                            }
                            .buttonStyle(.borderless)
                        }
                        .tag(image.id)
                    }
                }
                .listStyle(.inset)
            }
        }
    }

    private var nameSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Container Name (Optional)")
                .font(.headline)
            TextField("my-container", text: $containerName)
                #if os(macOS)
                .textFieldStyle(.roundedBorder)
                #endif
        }
    }

    private var memorySection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Memory Limit (MB, optional)")
                .font(.headline)
            TextField("2048", text: $memoryLimitText)
                #if os(macOS)
                .textFieldStyle(.roundedBorder)
                #endif
            Text("Minimum \(PodishMemoryLimits.minimumMemoryQuotaMB) MB. Leave blank to use the default container limit.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private var networkSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Network")
                .font(.headline)
            Picker("Network", selection: $networkMode) {
                ForEach(PodishNetworkMode.allCases) { mode in
                    Text(mode.displayName).tag(mode)
                }
            }
            .pickerStyle(.segmented)

            if networkMode == .privateNet {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Port Forwarding (hostPort:containerPort, one per line)")
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

    private var selectedImageRef: String? {
        guard let selectedImageId else { return nil }
        return store.images.first(where: { $0.id == selectedImageId })?.repoTag
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

    private struct ParseError: LocalizedError {
        let message: String
        init(_ message: String) { self.message = message }
        var errorDescription: String? { message }
    }
}
