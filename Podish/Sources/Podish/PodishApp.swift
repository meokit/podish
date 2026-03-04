import AppKit
import Foundation
import SwiftUI
@preconcurrency import SwiftTerm

private struct PodCtxOptionsNative {
    var work_dir_utf8: UnsafePointer<CChar>?
    var log_level_utf8: UnsafePointer<CChar>?
    var log_file_utf8: UnsafePointer<CChar>?
}

private typealias PodTerminalOutputCallback = @convention(c) (
    UnsafeMutableRawPointer?,
    Int32,
    UnsafePointer<UInt8>?,
    Int32
) -> Void

@_silgen_name("pod_ctx_create")
private func pod_ctx_create(
    _ options: UnsafePointer<PodCtxOptionsNative>?,
    _ out_ctx: UnsafeMutablePointer<UnsafeMutableRawPointer?>
) -> Int32

@_silgen_name("pod_ctx_destroy")
private func pod_ctx_destroy(_ ctx: UnsafeMutableRawPointer?)

@_silgen_name("pod_ctx_last_error")
private func pod_ctx_last_error(_ ctx: UnsafeMutableRawPointer?, _ buffer: UnsafeMutablePointer<UInt8>?, _ capacity: Int32) -> Int32

@_silgen_name("pod_image_pull")
private func pod_image_pull(_ ctx: UnsafeMutableRawPointer?, _ image_ref_utf8: UnsafePointer<CChar>?) -> Int32

@_silgen_name("pod_container_start_json")
private func pod_container_start_json(
    _ ctx: UnsafeMutableRawPointer?,
    _ run_spec_json_utf8: UnsafePointer<CChar>?,
    _ out_container: UnsafeMutablePointer<UnsafeMutableRawPointer?>
) -> Int32

@_silgen_name("pod_container_destroy")
private func pod_container_destroy(_ container: UnsafeMutableRawPointer?)

@_silgen_name("pod_container_set_output_callback")
private func pod_container_set_output_callback(
    _ container: UnsafeMutableRawPointer?,
    _ callback: PodTerminalOutputCallback?,
    _ user_data: UnsafeMutableRawPointer?
) -> Int32

@_silgen_name("pod_container_write_stdin")
private func pod_container_write_stdin(
    _ container: UnsafeMutableRawPointer?,
    _ data: UnsafePointer<UInt8>?,
    _ len: Int32,
    _ written: UnsafeMutablePointer<Int32>?
) -> Int32

@_silgen_name("pod_container_resize")
private func pod_container_resize(_ container: UnsafeMutableRawPointer?, _ rows: UInt16, _ cols: UInt16) -> Int32

private struct PodishRunSpec: Codable {
    var image: String?
    var rootfs: String?
    var exe: String?
    var exeArgs: [String] = []
    var volumes: [String] = []
    var env: [String] = []
    var dns: [String] = []
    var interactive: Bool = true
    var tty: Bool = true
    var strace: Bool = false
    var logDriver: String = "none"
}

enum PodishNativeError: LocalizedError {
    case code(String, Int32, String)
    case encodeFailure

    var errorDescription: String? {
        switch self {
        case .code(let op, let code, let message):
            if message.isEmpty { return "\(op) failed (\(code))" }
            return "\(op) failed (\(code)): \(message)"
        case .encodeFailure:
            return "failed to encode run spec"
        }
    }
}

final class TerminalSink: @unchecked Sendable {
    private let view: TerminalView

    init(view: TerminalView) {
        self.view = view
    }

    // Backpressure point: callback thread blocks here when main thread is slow.
    func feedWithBackpressure(_ bytes: [UInt8]) {
        if Thread.isMainThread {
            MainActor.assumeIsolated {
                view.feed(byteArray: bytes[...])
            }
            return
        }
        DispatchQueue.main.sync {
            MainActor.assumeIsolated {
                view.feed(byteArray: bytes[...])
            }
        }
    }
}

final class OutputBridge: @unchecked Sendable {
    private let sink: TerminalSink

    init(sink: TerminalSink) {
        self.sink = sink
    }

    func onOutput(streamKind: Int32, bytes: [UInt8]) {
        _ = streamKind
        sink.feedWithBackpressure(bytes)
    }
}

actor PodishRuntimeActor {
    private let workDir: String
    private let outputBridge: OutputBridge

    private var ctx: UnsafeMutableRawPointer?
    private var container: UnsafeMutableRawPointer?
    private var callbackUserData: UnsafeMutableRawPointer?

    init(workDir: String, outputBridge: OutputBridge) {
        self.workDir = workDir
        self.outputBridge = outputBridge
    }

    func startAlpineAsh() throws {
        try ensureContext()
        try pullImage("docker.io/i386/alpine:latest")
        try startContainer()
    }

    func writeInput(_ bytes: [UInt8]) async throws {
        guard let c = container, !bytes.isEmpty else { return }
        var offset = 0
        while offset < bytes.count {
            var written: Int32 = 0
            let rc: Int32 = bytes.withUnsafeBufferPointer { buf in
                let base = buf.baseAddress!.advanced(by: offset)
                let len = Int32(bytes.count - offset)
                return pod_container_write_stdin(c, base, len, &written)
            }
            if rc != 0 {
                throw PodishNativeError.code("pod_container_write_stdin", rc, lastError())
            }
            if written <= 0 {
                try await Task.sleep(nanoseconds: 2_000_000)
                continue
            }
            offset += Int(written)
        }
    }

    func resize(cols: Int, rows: Int) {
        guard let c = container else { return }
        let safeRows = UInt16(max(0, min(65535, rows)))
        let safeCols = UInt16(max(0, min(65535, cols)))
        _ = pod_container_resize(c, safeRows, safeCols)
    }

    func shutdown() {
        if let c = container {
            _ = pod_container_set_output_callback(c, nil, nil)
            pod_container_destroy(c)
            container = nil
        }
        if let ud = callbackUserData {
            Unmanaged<OutputBridge>.fromOpaque(ud).release()
            callbackUserData = nil
        }
        if let c = ctx {
            pod_ctx_destroy(c)
            ctx = nil
        }
    }

    private func ensureContext() throws {
        if ctx != nil { return }

        var out: UnsafeMutableRawPointer?
        let rc = workDir.withCString { cwdPtr in
            "warn".withCString { levelPtr in
                var opts = PodCtxOptionsNative(work_dir_utf8: cwdPtr, log_level_utf8: levelPtr, log_file_utf8: nil)
                return pod_ctx_create(&opts, &out)
            }
        }
        guard rc == 0, let out else {
            throw PodishNativeError.code("pod_ctx_create", rc, "unable to initialize context")
        }
        ctx = out
    }

    private func pullImage(_ image: String) throws {
        guard let c = ctx else { throw PodishNativeError.code("pod_image_pull", -1, "context is nil") }
        let rc = image.withCString { pod_image_pull(c, $0) }
        if rc != 0 {
            throw PodishNativeError.code("pod_image_pull", rc, lastError())
        }
    }

    private func startContainer() throws {
        guard let c = ctx else { throw PodishNativeError.code("pod_container_start_json", -1, "context is nil") }
        if container != nil { return }

        let spec = PodishRunSpec(
            image: "docker.io/i386/alpine:latest",
            rootfs: nil,
            exe: "/bin/ash"
        )
        let data = try JSONEncoder().encode(spec)
        guard let json = String(data: data, encoding: .utf8) else {
            throw PodishNativeError.encodeFailure
        }

        var outContainer: UnsafeMutableRawPointer?
        let rc = json.withCString {
            pod_container_start_json(c, $0, &outContainer)
        }
        guard rc == 0, let outContainer else {
            throw PodishNativeError.code("pod_container_start_json", rc, lastError())
        }

        let retainedBridge = Unmanaged.passRetained(outputBridge)
        let userData = retainedBridge.toOpaque()
        let cbRc = pod_container_set_output_callback(outContainer, podishTerminalOutputCallback, userData)
        if cbRc != 0 {
            retainedBridge.release()
            pod_container_destroy(outContainer)
            throw PodishNativeError.code("pod_container_set_output_callback", cbRc, lastError())
        }

        callbackUserData = userData
        container = outContainer
    }

    private func lastError() -> String {
        guard let c = ctx else { return "unknown" }
        var buf = [UInt8](repeating: 0, count: 4096)
        let n = Int(pod_ctx_last_error(c, &buf, Int32(buf.count)))
        if n <= 0 { return "unknown" }
        return String(decoding: buf.prefix(n), as: UTF8.self)
    }
}

private func podishTerminalOutputCallback(
    userData: UnsafeMutableRawPointer?,
    streamKind: Int32,
    data: UnsafePointer<UInt8>?,
    len: Int32
) {
    guard let userData, let data, len > 0 else { return }
    let bridge = Unmanaged<OutputBridge>.fromOpaque(userData).takeUnretainedValue()
    let bytes = Array(UnsafeBufferPointer(start: data, count: Int(len)))
    bridge.onOutput(streamKind: streamKind, bytes: bytes)
}

final class TerminalDelegateAdapter: NSObject, TerminalViewDelegate {
    var onResize: ((Int, Int) -> Void)?
    var onInput: (([UInt8]) -> Void)?

    func sizeChanged(source: TerminalView, newCols: Int, newRows: Int) {
        onResize?(newCols, newRows)
    }

    func setTerminalTitle(source: TerminalView, title: String) {}
    func hostCurrentDirectoryUpdate(source: TerminalView, directory: String?) {}

    func send(source: TerminalView, data: ArraySlice<UInt8>) {
        onInput?(Array(data))
    }

    func scrolled(source: TerminalView, position: Double) {}
    func requestOpenLink(source: TerminalView, link: String, params: [String: String]) {}
    func bell(source: TerminalView) {}
    func clipboardCopy(source: TerminalView, content: Data) {}
    func iTermContent(source: TerminalView, content: ArraySlice<UInt8>) {}
    func rangeChanged(source: TerminalView, startY: Int, endY: Int) {}
}

@MainActor
final class PodishTerminalSession: ObservableObject {
    let view: TerminalView
    @Published var status: String = "Initializing..."

    private let runtime: PodishRuntimeActor
    private let delegateAdapter = TerminalDelegateAdapter()
    private var startupTask: Task<Void, Never>?

    init() {
        view = TerminalView(frame: .zero)
        let sink = TerminalSink(view: view)
        let bridge = OutputBridge(sink: sink)
        runtime = PodishRuntimeActor(workDir: FileManager.default.currentDirectoryPath, outputBridge: bridge)

        delegateAdapter.onResize = { [runtime] cols, rows in
            Task {
                await runtime.resize(cols: cols, rows: rows)
            }
        }
        delegateAdapter.onInput = { [runtime] bytes in
            Task {
                try? await runtime.writeInput(bytes)
            }
        }
        view.terminalDelegate = delegateAdapter

        startupTask = Task { [weak self] in
            await self?.start()
        }
    }

    deinit {
        startupTask?.cancel()
        let runtime = self.runtime
        Task {
            await runtime.shutdown()
        }
    }

    private func start() async {
        status = "Pulling docker.io/i386/alpine:latest..."
        do {
            try await runtime.startAlpineAsh()
            status = "Running /bin/ash"
        } catch {
            status = "Startup failed: \(error.localizedDescription)"
        }
    }
}

struct TerminalContainer: NSViewRepresentable {
    @ObservedObject var session: PodishTerminalSession

    func makeNSView(context: Context) -> TerminalView {
        session.view
    }

    func updateNSView(_ nsView: TerminalView, context: Context) {}
}

struct PodishRootView: View {
    @StateObject private var session = PodishTerminalSession()

    var body: some View {
        ZStack(alignment: .topLeading) {
            TerminalContainer(session: session)
                .frame(minWidth: 900, minHeight: 560)

            Text(session.status)
                .font(.system(size: 11, weight: .medium, design: .monospaced))
                .padding(8)
                .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 8))
                .padding(10)
        }
    }
}

@main
struct PodishApp: App {
    var body: some Scene {
        WindowGroup("Podish") {
            PodishRootView()
        }
    }
}
