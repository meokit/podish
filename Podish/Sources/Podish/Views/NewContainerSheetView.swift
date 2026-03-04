import SwiftUI

struct NewContainerSheetView: View {
    @ObservedObject var store: PodishUiStore
    @Environment(\.dismiss) private var dismiss

    @State private var pullImageRef = "docker.io/i386/alpine:latest"
    @State private var selectedImageId: String?

    var body: some View {
        NavigationStack {
            VStack(spacing: 12) {
                pullSection
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
                            store.createContainer(fromImage: imageRef)
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
            .onChange(of: store.images) { _, newImages in
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
                ContentUnavailableView("No Images", systemImage: "shippingbox", description: Text("Pull an image to continue."))
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

    private var selectedImageRef: String? {
        guard let selectedImageId else { return nil }
        return store.images.first(where: { $0.id == selectedImageId })?.repoTag
    }
}
