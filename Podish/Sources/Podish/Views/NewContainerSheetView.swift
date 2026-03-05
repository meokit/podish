import SwiftUI

struct NewContainerSheetView: View {
    @ObservedObject var store: PodishUiStore
    @Environment(\.dismiss) private var dismiss

    @State private var pullImageRef = "docker.io/i386/alpine:latest"
    @State private var selectedImageId: String?
    @State private var containerName = ""

    var body: some View {
        NavigationStack {
            VStack(spacing: 12) {
                pullSection
                nameSection
                imageListSection
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
                            store.createContainer(fromImage: imageRef, name: trimmed.isEmpty ? nil : trimmed)
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

    private var selectedImageRef: String? {
        guard let selectedImageId else { return nil }
        return store.images.first(where: { $0.id == selectedImageId })?.repoTag
    }
}
