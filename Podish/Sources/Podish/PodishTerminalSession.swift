import Foundation
import SwiftUI
@preconcurrency import SwiftTerm

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
    var logDriver: String = "json-file"
}

enum PodishRuntimeError: LocalizedError {
    case native(code: Int32, message: String)

    var errorDescription: String? {
        switch self {
        case .native(let code, let message):
            return "Native error \(code): \(message)"
        }
    }
}

final class TerminalSink: @unchecked Sendable {
    private let view: TerminalView

    init(view: TerminalView) {
        self.view = view
    }

    func feed(_ bytes: [UInt8]) {
        guard !bytes.isEmpty else { return }
        DispatchQueue.main.async { [view] in
            view.feed(byteArray: bytes[...])
        }
    }
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

actor PodishRuntimeActor {
    private let workDir: String
    private let sink: TerminalSink

    private var ctx: UnsafeMutableRawPointer?
    private var container: UnsafeMutableRawPointer?
    private var terminal: UnsafeMutableRawPointer?
    private var readTask: Task<Void, Never>?

    init(workDir: String, sink: TerminalSink) {
        self.workDir = workDir
        self.sink = sink
    }

    func startDefaultShell() throws {
        if terminal != nil { return }

        try ensureContext()
        try pullImage("docker.io/i386/alpine:latest")
        try createAndStartContainer()
        try attachTerminal()
        startReadLoop()
    }

    func stop() {
        readTask?.cancel()
        readTask = nil

        if let term = terminal {
            pod_terminal_close(term)
            terminal = nil
        }
        if let c = container {
            pod_container_destroy(c)
            container = nil
        }
        if let c = ctx {
            pod_ctx_destroy(c)
            ctx = nil
        }
    }

    func writeInput(_ bytes: [UInt8]) throws {
        guard let term = terminal, !bytes.isEmpty else {
            return
        }

        var offset = 0
        while offset < bytes.count {
            var wrote: Int32 = 0
            let rc = bytes.withUnsafeBytes { raw in
                guard let base = raw.baseAddress?.assumingMemoryBound(to: UInt8.self) else {
                    return Int32(22)
                }
                return pod_terminal_write(term, base.advanced(by: offset), Int32(bytes.count - offset), &wrote)
            }
            if rc != 0 {
                throw PodishRuntimeError.native(code: rc, message: lastError())
            }
            if wrote <= 0 { break }
            offset += Int(wrote)
        }
    }

    func resize(cols: Int, rows: Int) {
        guard let term = terminal else { return }
        let safeRows = UInt16(max(0, min(65535, rows)))
        let safeCols = UInt16(max(0, min(65535, cols)))
        _ = pod_terminal_resize(term, safeRows, safeCols)
    }

    private func ensureContext() throws {
        if ctx != nil { return }

        var out: UnsafeMutableRawPointer?
        let rc = workDir.withCString { cwd in
            "warn".withCString { level in
                var opts = PodCtxOptionsNative(work_dir_utf8: cwd, log_level_utf8: level, log_file_utf8: nil)
                return pod_ctx_create(&opts, &out)
            }
        }

        if rc != 0 {
            throw PodishRuntimeError.native(code: rc, message: "failed to create context")
        }

        ctx = out
    }

    private func pullImage(_ image: String) throws {
        guard let c = ctx else { throw PodishRuntimeError.native(code: -1, message: "context nil") }
        let rc = image.withCString { pod_image_pull(c, $0) }
        if rc != 0 {
            throw PodishRuntimeError.native(code: rc, message: lastError())
        }
    }

    private func createAndStartContainer() throws {
        guard let c = ctx else { throw PodishRuntimeError.native(code: -1, message: "context nil") }
        if container != nil { return }

        let spec = PodishRunSpec(
            image: "docker.io/i386/alpine:latest",
            rootfs: nil,
            exe: "/bin/ash",
            exeArgs: [],
            volumes: [],
            env: [],
            dns: [],
            interactive: true,
            tty: true,
            strace: false,
            logDriver: "json-file"
        )
        let data = try JSONEncoder().encode(spec)
        guard let json = String(data: data, encoding: .utf8) else {
            throw PodishRuntimeError.native(code: -1, message: "encode run spec failed")
        }

        var outContainer: UnsafeMutableRawPointer?
        let rcCreate = json.withCString { pod_container_create_json(c, $0, &outContainer) }
        if rcCreate != 0 {
            throw PodishRuntimeError.native(code: rcCreate, message: lastError())
        }
        guard let outContainer else {
            throw PodishRuntimeError.native(code: -1, message: "container handle nil")
        }

        let rcStart = pod_container_start(outContainer)
        if rcStart != 0 {
            pod_container_destroy(outContainer)
            throw PodishRuntimeError.native(code: rcStart, message: lastError())
        }

        container = outContainer
    }

    private func attachTerminal() throws {
        guard let c = container else { throw PodishRuntimeError.native(code: -1, message: "container nil") }
        if terminal != nil { return }

        var outTerm: UnsafeMutableRawPointer?
        let rc = pod_terminal_attach(c, &outTerm)
        if rc != 0 {
            throw PodishRuntimeError.native(code: rc, message: lastError())
        }
        terminal = outTerm
    }

    private func startReadLoop() {
        guard readTask == nil else { return }

        readTask = Task { [weak self] in
            guard let self else { return }
            await self.readLoop()
        }
    }

    private func readLoop() async {
        while !Task.isCancelled {
            guard let term = terminal else { return }
            let termAddress = UInt(bitPattern: term)
            let (rc, chunk) = await Task.detached(priority: .utility) { () -> (Int32, [UInt8]) in
                guard let termPtr = UnsafeMutableRawPointer(bitPattern: termAddress) else {
                    return (Int32(22), [])
                }
                var localBuffer = [UInt8](repeating: 0, count: 4096)
                var count: Int32 = 0
                let rc = localBuffer.withUnsafeMutableBufferPointer { ptr in
                    pod_terminal_read(termPtr, ptr.baseAddress, Int32(ptr.count), 200, &count)
                }
                if rc != 0 || count <= 0 {
                    return (rc, [])
                }
                return (rc, Array(localBuffer.prefix(Int(count))))
            }.value

            if rc != 0 {
                try? await Task.sleep(nanoseconds: 100_000_000)
                continue
            }

            if !chunk.isEmpty {
                sink.feed(chunk)
            }
        }
    }

    private func lastError() -> String {
        guard let c = ctx else { return "unknown" }
        var buf = [UInt8](repeating: 0, count: 1024)
        _ = pod_ctx_last_error(c, &buf, Int32(buf.count))
        let end = buf.firstIndex(of: 0) ?? buf.count
        return String(decoding: buf[..<end], as: UTF8.self)
    }
}

@MainActor
final class PodishTerminalSession: ObservableObject {
    let terminalView: TerminalView
    private let delegate = TerminalDelegateAdapter()
    private let runtime: PodishRuntimeActor

    @Published var startupError: String?
    private var started = false

    init() {
        terminalView = TerminalView(frame: .zero)

        let sink = TerminalSink(view: terminalView)
        let workDir = PodishTerminalSession.makeWorkDir()
        runtime = PodishRuntimeActor(workDir: workDir, sink: sink)

        terminalView.terminalDelegate = delegate

        delegate.onInput = { [weak self] data in
            guard let self else { return }
            Task {
                do {
                    try await self.runtime.writeInput(data)
                } catch {
                    await MainActor.run { self.startupError = error.localizedDescription }
                }
            }
        }

        delegate.onResize = { [weak self] cols, rows in
            guard let self else { return }
            Task {
                await runtime.resize(cols: cols, rows: rows)
            }
        }
    }

    func startIfNeeded() {
        guard !started else { return }
        started = true

        Task {
            do {
                try await runtime.startDefaultShell()
            } catch {
                await MainActor.run {
                    self.startupError = error.localizedDescription
                }
            }
        }
    }

    func stop() {
        Task {
            await runtime.stop()
        }
    }

    private static func makeWorkDir() -> String {
        let fm = FileManager.default
        let base = (try? fm.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true))
            ?? URL(fileURLWithPath: fm.currentDirectoryPath)
        let dir = base.appendingPathComponent("Podish", isDirectory: true)
        try? fm.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir.path
    }

}

#if os(macOS)
import AppKit

struct TerminalViewHost: NSViewRepresentable {
    let terminalView: TerminalView

    func makeNSView(context: Context) -> TerminalView {
        terminalView
    }

    func updateNSView(_ nsView: TerminalView, context: Context) {
        guard let window = nsView.window else { return }
        if !window.isKeyWindow {
            window.makeKeyAndOrderFront(nil)
        }
        if window.firstResponder !== nsView {
            _ = window.makeFirstResponder(nsView)
        }
    }
}
#else
import UIKit

struct TerminalViewHost: UIViewRepresentable {
    let terminalView: TerminalView

    func makeUIView(context: Context) -> TerminalView {
        terminalView
    }

    func updateUIView(_ uiView: TerminalView, context: Context) {}
}
#endif
