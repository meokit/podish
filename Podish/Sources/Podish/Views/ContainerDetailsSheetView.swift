import SwiftUI

struct ContainerDetailsSheetView: View {
    let container: PodishContainer
    @Environment(\.dismiss) private var dismiss
    @State private var selectedTab = 1

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
                        ScrollView {
                            Text("[INFO] container-start \(container.name)\n[INFO] terminal attached")
                                .font(.system(.body, design: .monospaced))
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .padding(12)
                        }
                        .background(.quaternary.opacity(0.2), in: RoundedRectangle(cornerRadius: 12))
                    case 2:
                        let cpuText = String(format: "%.1f%%", container.cpu)
                        Text("Name: \(container.name)\nImage: \(container.image)\nState: \(container.state.rawValue)\nCPU: \(cpuText)\nMemory: \(container.memoryMB) MB")
                            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                    default:
                        Text("/etc/resolv.conf -> detached tmpfs\n/proc -> procfs\n/dev -> devtmpfs")
                            .font(.system(.body, design: .monospaced))
                            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
            .padding(16)
            .navigationTitle(container.name)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Close") { dismiss() }
                }
            }
        }
        #if os(macOS)
        .frame(minWidth: 880, minHeight: 560)
        #endif
    }
}
