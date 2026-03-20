import Foundation
import MessagePack
import SwiftUI
@preconcurrency import SwiftTerm

private enum NativeIpcEvent {
    case none
    case log(level: Int32, message: String)
    case containerStateChanged
}

private struct PollEventArgs: Encodable {
    let timeoutMs: Int32

    func encode(to encoder: Encoder) throws {
        var container = encoder.unkeyedContainer()
        try container.encode(timeoutMs)
    }
}

private struct NativeIpcEventEnvelope: Decodable {
    let event: NativeIpcEvent

    init(from decoder: Decoder) throws {
        var container = try decoder.unkeyedContainer()
        let eventType = try container.decode(Int32.self)
        switch eventType {
        case POD_IPC_EVENT_NONE:
            event = NativeIpcEvent.none
        case POD_IPC_EVENT_LOG_LINE:
            let level = try container.decode(Int32.self)
            let message = try container.decode(String.self)
            event = .log(level: level, message: message)
        case POD_IPC_EVENT_CONTAINER_STATE_CHANGED:
            event = .containerStateChanged
        default:
            throw DecodingError.dataCorruptedError(in: container, debugDescription: "unsupported ipc event: \(eventType)")
        }
    }
}

private func encodePollEventArgs(timeoutMs: Int32) -> [UInt8] {
    do {
        let data = try MessagePackEncoder().encode(PollEventArgs(timeoutMs: timeoutMs))
        return [UInt8](data)
    } catch {
        return []
    }
}

private func decodeNativeIpcEvent(_ frame: [UInt8]) -> NativeIpcEvent? {
    do {
        let decoded = try MessagePackDecoder().decode(NativeIpcEventEnvelope.self, from: Data(frame))
        return decoded.event
    } catch {
        return nil
    }
}

private struct PodishRunSpec: Codable {
    struct PublishedPortSpec: Codable {
        var hostPort: Int
        var containerPort: Int
        var protocolValue: Int
        var bindAddress: String

        enum CodingKeys: String, CodingKey {
            case hostPort
            case containerPort
            case protocolValue = "protocol"
            case bindAddress
        }
    }

    var name: String?
    var networkMode: Int = 0
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
    var publishedPorts: [PublishedPortSpec] = []
    var memoryQuotaBytes: Int64?
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
    private struct RuntimeTerminalSession {
        var container: UnsafeMutableRawPointer
        var terminal: UnsafeMutableRawPointer
        var readTask: Task<Void, Never>?
    }

    private let workDir: String
    private var ctx: UnsafeMutableRawPointer?
    private var eventTask: Task<Void, Never>?

    private var sessions: [String: RuntimeTerminalSession] = [:]
    private var onOutput: (@Sendable (String, [UInt8]) -> Void)?
    private var onLogLine: (@Sendable (String) -> Void)?
    private var onContainerState: (@Sendable ([NativeContainerListItem]) -> Void)?

    struct StartupState {
        let containers: [NativeContainerListItem]
        let attachedContainerId: String?
    }

    init(workDir: String) {
        self.workDir = workDir
    }

    func setObservers(
        onOutput: @escaping @Sendable (String, [UInt8]) -> Void,
        onLog: @escaping @Sendable (String) -> Void,
        onContainerState: @escaping @Sendable ([NativeContainerListItem]) -> Void
    ) {
        self.onOutput = onOutput
        self.onLogLine = onLog
        self.onContainerState = onContainerState
        guard ctx != nil else { return }
        startEventLoop()
    }

    func startDefaultShell() throws -> StartupState {
        try ensureContext()
        var containers = try listContainers()
        var attached: String?

        if containers.isEmpty {
            try pullImageInternal("docker.io/i386/alpine:latest")
            _ = try createAndStartContainer(imageRef: "docker.io/i386/alpine:latest", name: nil)
            containers = try listContainers()
            attached = containers.first(where: { $0.running })?.containerId ?? containers.first?.containerId
            if let attached {
                try ensureTerminalSession(containerId: attached)
            }
        }

        return StartupState(containers: containers, attachedContainerId: attached)
    }

    func refreshContainers() throws -> [NativeContainerListItem] {
        try ensureContext()
        return try listContainers()
    }

    func refreshImages() throws -> [NativeImageListItem] {
        try ensureContext()
        return try listImages()
    }

    func inspectContainer(containerId: String) throws -> NativeContainerInspect {
        try ensureContext()
        let handle = try openContainer(containerId: containerId)
        defer { pod_container_close(handle) }

        var capacity = 16 * 1024
        while true {
            var buffer = [UInt8](repeating: 0, count: capacity)
            var outLen: Int32 = 0
            let rc = buffer.withUnsafeMutableBufferPointer { ptr in
                pod_container_inspect_json(handle, ptr.baseAddress, Int32(ptr.count), &outLen)
            }

            if rc == 0 {
                let n = max(0, Int(outLen))
                let data = Data(buffer.prefix(n))
                return try JSONDecoder().decode(NativeContainerInspect.self, from: data)
            }

            if rc == 34 {
                capacity *= 2
                if capacity > 1_048_576 {
                    throw PodishRuntimeError.native(code: rc, message: "container inspect payload too large")
                }
                continue
            }

            throw PodishRuntimeError.native(code: rc, message: lastError())
        }
    }

    func readLogs(containerId: String, cursor: String?, follow: Bool, timeoutMs: Int32) throws -> NativeLogsChunk {
        try ensureContext()
        let handle = try openContainer(containerId: containerId)
        defer { pod_container_close(handle) }

        var capacity = 16 * 1024
        while true {
            var buffer = [UInt8](repeating: 0, count: capacity)
            var outLen: Int32 = 0
            let rc = (cursor ?? "").withCString { cstr in
                buffer.withUnsafeMutableBufferPointer { ptr in
                    pod_logs_read_json(
                        handle,
                        cstr,
                        follow ? 1 : 0,
                        timeoutMs,
                        ptr.baseAddress,
                        Int32(ptr.count),
                        &outLen
                    )
                }
            }

            if rc == 0 {
                let n = max(0, Int(outLen))
                let data = Data(buffer.prefix(n))
                return try JSONDecoder().decode(NativeLogsChunk.self, from: data)
            }

            if rc == 34 {
                capacity *= 2
                if capacity > 1_048_576 {
                    throw PodishRuntimeError.native(code: rc, message: "container logs payload too large")
                }
                continue
            }

            throw PodishRuntimeError.native(code: rc, message: lastError())
        }
    }

    func pullImage(imageRef: String) throws -> [NativeImageListItem] {
        try ensureContext()
        try pullImageInternal(imageRef)
        return try listImages()
    }

    func removeImage(imageRef: String) throws -> [NativeImageListItem] {
        try ensureContext()
        let rc = imageRef.withCString { pod_image_remove(ctx, $0, 1) }
        if rc != 0 {
            throw PodishRuntimeError.native(code: rc, message: lastError())
        }
        return try listImages()
    }

    func createContainer(
        imageRef: String,
        name: String?,
        networkMode: PodishNetworkMode,
        portMappings: [PodishPortMapping],
        memoryQuotaBytes: Int64?
    ) throws -> String {
        try ensureContext()
        return try createAndStartContainer(
            imageRef: imageRef,
            name: name,
            networkMode: networkMode,
            portMappings: portMappings,
            memoryQuotaBytes: memoryQuotaBytes
        )
    }

    func startContainer(containerId: String) throws -> [NativeContainerListItem] {
        try ensureContext()
        let handle = try openContainer(containerId: containerId)
        defer { pod_container_close(handle) }

        let rc = pod_container_start(handle)
        if rc != 0 {
            throw PodishRuntimeError.native(code: rc, message: lastError())
        }
        return try listContainers()
    }

    func stopContainer(containerId: String, signal: Int32 = 15, timeoutMs: Int32 = 2000) throws -> [NativeContainerListItem] {
        try ensureContext()
        let handle = try openContainer(containerId: containerId)
        defer { pod_container_close(handle) }

        let rc = pod_container_stop(handle, signal, timeoutMs)
        if rc != 0 {
            throw PodishRuntimeError.native(code: rc, message: lastError())
        }

        closeTerminalSession(containerId: containerId)
        return try listContainers()
    }

    func removeContainer(containerId: String) throws -> [NativeContainerListItem] {
        try ensureContext()
        let handle = try openContainer(containerId: containerId)
        defer { pod_container_close(handle) }

        let rc = pod_container_remove(handle, 1)
        if rc != 0 {
            throw PodishRuntimeError.native(code: rc, message: lastError())
        }

        closeTerminalSession(containerId: containerId)
        return try listContainers()
    }

    func ensureTerminalSession(containerId: String) throws {
        try ensureContext()
        if sessions[containerId] != nil { return }

        let container = try openContainer(containerId: containerId)
        var terminal: UnsafeMutableRawPointer?
        let rc = pod_terminal_attach(container, &terminal)
        if rc != 0 || terminal == nil {
            pod_container_close(container)
            throw PodishRuntimeError.native(code: rc, message: lastError())
        }

        sessions[containerId] = RuntimeTerminalSession(container: container, terminal: terminal!, readTask: nil)
        startReadLoop(containerId: containerId)
    }

    func closeTerminalSession(containerId: String) {
        guard let session = sessions.removeValue(forKey: containerId) else { return }
        session.readTask?.cancel()
        pod_terminal_close(session.terminal)
        pod_container_close(session.container)
    }

    func writeInput(containerId: String, _ bytes: [UInt8]) throws {
        guard !bytes.isEmpty, let term = sessions[containerId]?.terminal else { return }

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

    func resize(containerId: String, cols: Int, rows: Int) {
        guard let term = sessions[containerId]?.terminal else { return }
        let safeRows = UInt16(max(0, min(65535, rows)))
        let safeCols = UInt16(max(0, min(65535, cols)))
        _ = pod_terminal_resize(term, safeRows, safeCols)
    }

    func stop() async {
        let runningEventTask = eventTask
        eventTask = nil
        runningEventTask?.cancel()
        if let runningEventTask {
            await runningEventTask.value
        }

        for (containerId, _) in sessions {
            closeTerminalSession(containerId: containerId)
        }
        sessions.removeAll()

        if let c = ctx {
            pod_ctx_destroy(c)
            ctx = nil
        }
    }

    func shutdownForAppExit(stopTimeoutMs: Int32 = 10_000) async {
        if ctx != nil {
            let runningIds = ((try? listContainers()) ?? [])
                .filter { $0.running }
                .map(\.containerId)

            for containerId in runningIds {
                do {
                    _ = try stopContainer(containerId: containerId, signal: 15, timeoutMs: stopTimeoutMs)
                } catch {
                    onLogLine?("shutdown: stop container \(containerId) failed: \(error.localizedDescription)")
                }
            }
        }

        await stop()
    }

    private func startReadLoop(containerId: String) {
        guard var session = sessions[containerId], session.readTask == nil else { return }

        let task = Task { [weak self] in
            guard let self else { return }
            await self.readLoop(containerId: containerId)
        }
        session.readTask = task
        sessions[containerId] = session
    }

    private func readLoop(containerId: String) async {
        while !Task.isCancelled {
            guard let term = sessions[containerId]?.terminal else { return }
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
                onOutput?(containerId, chunk)
            }
        }
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
        startEventLoop()
    }

    private func startEventLoop() {
        guard eventTask == nil, let c = ctx else { return }
        let ctxAddress = UInt(bitPattern: c)
        eventTask = Task { [weak self] in
            guard let self else { return }
            await self.eventLoop(ctxAddress: ctxAddress)
        }
    }

    private func eventLoop(ctxAddress: UInt) async {
        while !Task.isCancelled {
            let (rc, frame) = await Task.detached(priority: .utility) { () -> (Int32, [UInt8]) in
                guard let ctxPtr = UnsafeMutableRawPointer(bitPattern: ctxAddress) else {
                    return (Int32(22), [])
                }

                let args = encodePollEventArgs(timeoutMs: 200)
                var capacity = 1024

                while true {
                    var response = [UInt8](repeating: 0, count: capacity)
                    var outLen: Int32 = 0

                    let rc = args.withUnsafeBufferPointer { argsPtr in
                        response.withUnsafeMutableBufferPointer { responsePtr in
                            pod_ctx_call_msgpack(
                                ctxPtr,
                                POD_IPC_OP_POLL_EVENT,
                                argsPtr.baseAddress,
                                Int32(argsPtr.count),
                                responsePtr.baseAddress,
                                Int32(responsePtr.count),
                                &outLen
                            )
                        }
                    }

                    if rc == 22 && Int(outLen) > capacity && capacity < 1_048_576 {
                        capacity = max(capacity * 2, Int(outLen))
                        continue
                    }

                    if rc != 0 {
                        return (rc, [])
                    }

                    let n = max(0, min(Int(outLen), response.count))
                    return (rc, Array(response.prefix(n)))
                }
            }.value

            if Task.isCancelled {
                return
            }

            if rc != 0 {
                try? await Task.sleep(nanoseconds: 100_000_000)
                continue
            }

            guard let event = decodeNativeIpcEvent(frame) else {
                continue
            }

            switch event {
            case .none:
                continue
            case .log(_, let message):
                if !message.isEmpty {
                    onLogLine?(message)
                }
            case .containerStateChanged:
                do {
                    let items = try listContainers()
                    onContainerState?(items)
                } catch {
                    continue
                }
            }
        }
    }

    private func pullImageInternal(_ image: String) throws {
        guard let c = ctx else { throw PodishRuntimeError.native(code: -1, message: "context nil") }
        let rc = image.withCString { pod_image_pull(c, $0) }
        if rc != 0 {
            throw PodishRuntimeError.native(code: rc, message: lastError())
        }
    }

    private func listContainers() throws -> [NativeContainerListItem] {
        guard let c = ctx else { throw PodishRuntimeError.native(code: -1, message: "context nil") }

        var capacity = 16 * 1024
        while true {
            var buffer = [UInt8](repeating: 0, count: capacity)
            var outLen: Int32 = 0
            let rc = buffer.withUnsafeMutableBufferPointer { ptr in
                pod_container_list_json(c, ptr.baseAddress, Int32(ptr.count), &outLen)
            }

            if rc == 0 {
                let n = max(0, Int(outLen))
                if n == 0 {
                    return []
                }
                let data = Data(buffer.prefix(n))
                return try JSONDecoder().decode([NativeContainerListItem].self, from: data)
            }

            if rc == 34 {
                capacity *= 2
                if capacity > 1_048_576 {
                    throw PodishRuntimeError.native(code: rc, message: "container list too large")
                }
                continue
            }

            throw PodishRuntimeError.native(code: rc, message: lastError())
        }
    }

    private func listImages() throws -> [NativeImageListItem] {
        guard let c = ctx else { throw PodishRuntimeError.native(code: -1, message: "context nil") }

        var capacity = 16 * 1024
        while true {
            var buffer = [UInt8](repeating: 0, count: capacity)
            var outLen: Int32 = 0
            let rc = buffer.withUnsafeMutableBufferPointer { ptr in
                pod_image_list_json(c, ptr.baseAddress, Int32(ptr.count), &outLen)
            }

            if rc == 0 {
                let n = max(0, Int(outLen))
                if n == 0 {
                    return []
                }
                let data = Data(buffer.prefix(n))
                return try JSONDecoder().decode([NativeImageListItem].self, from: data)
            }

            if rc == 34 {
                capacity *= 2
                if capacity > 1_048_576 {
                    throw PodishRuntimeError.native(code: rc, message: "image list too large")
                }
                continue
            }

            throw PodishRuntimeError.native(code: rc, message: lastError())
        }
    }

    private func createAndStartContainer(
        imageRef: String,
        name: String?,
        networkMode: PodishNetworkMode = .host,
        portMappings: [PodishPortMapping] = [],
        memoryQuotaBytes: Int64? = nil
    ) throws -> String {
        guard let c = ctx else { throw PodishRuntimeError.native(code: -1, message: "context nil") }
        let beforeIds = Set(try listContainers().map(\.containerId))
        let publishedPorts = portMappings.map { mapping in
            PodishRunSpec.PublishedPortSpec(
                hostPort: mapping.hostPort,
                containerPort: mapping.containerPort,
                protocolValue: 0, // TCP
                bindAddress: "0.0.0.0"
            )
        }
        let spec = PodishRunSpec(
            name: name,
            networkMode: networkMode.nativeValue,
            image: imageRef,
            rootfs: nil,
            exe: "/bin/ash",
            exeArgs: ["-i"],
            volumes: [],
            env: [],
            dns: [],
            interactive: true,
            tty: true,
            strace: false,
            logDriver: "json-file",
            publishedPorts: publishedPorts,
            memoryQuotaBytes: memoryQuotaBytes
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
        defer { pod_container_close(outContainer) }

        let rcStart = pod_container_start(outContainer)
        if rcStart != 0 {
            throw PodishRuntimeError.native(code: rcStart, message: lastError())
        }

        let after = try listContainers()
        if let created = after.first(where: { !beforeIds.contains($0.containerId) }) {
            return created.containerId
        }
        if let running = after.first(where: { $0.running && $0.image == imageRef }) {
            return running.containerId
        }

        throw PodishRuntimeError.native(code: -1, message: "failed to resolve created container id")
    }

    private func openContainer(containerId: String) throws -> UnsafeMutableRawPointer {
        guard let c = ctx else { throw PodishRuntimeError.native(code: -1, message: "context nil") }
        var outContainer: UnsafeMutableRawPointer?
        let rc = containerId.withCString { pod_container_open(c, $0, &outContainer) }
        if rc != 0 || outContainer == nil {
            throw PodishRuntimeError.native(code: rc, message: lastError())
        }
        return outContainer!
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
    private let placeholderView: TerminalView
    #if os(macOS)
    private let displayView: TerminalView
    #endif
    private let runtime: PodishRuntimeActor
    private var terminalViews: [String: TerminalView] = [:]
    private var terminalDelegates: [String: TerminalDelegateAdapter] = [:]
    #if os(macOS)
    private var displayDelegate: TerminalDelegateAdapter?
    #endif

    @Published var startupError: String?
    @Published private(set) var activeContainerId: String?

    private var started = false
    private var shutdownTask: Task<Void, Never>?
    var onContainerList: (([NativeContainerListItem]) -> Void)?
    var onContainerStateChanged: (([NativeContainerListItem]) -> Void)?
    var onImageList: (([NativeImageListItem]) -> Void)?
    private var lastContainerSnapshot: [NativeContainerListItem] = []

    var currentTerminalView: TerminalView {
        #if os(macOS)
        displayView
        #else
        if let id = activeContainerId, let view = terminalViews[id] {
            return view
        }
        return placeholderView
        #endif
    }

    var activeTerminalIdentity: String {
        #if os(macOS)
        "__display__"
        #else
        activeContainerId ?? "__placeholder__"
        #endif
    }

    init() {
        #if os(macOS)
        placeholderView = PodishTerminalView(frame: .zero)
        displayView = PodishTerminalView(frame: .zero)
        #else
        placeholderView = TerminalView(frame: .zero)
        #endif
        runtime = PodishRuntimeActor(workDir: PodishTerminalSession.makeWorkDir())
        #if os(macOS)
        configureDisplayDelegate()
        displayView.terminal = placeholderView.terminal
        #endif

        Task { [weak self] in
            guard let self else { return }
            await runtime.setObservers(
                onOutput: { [weak self] containerId, bytes in
                    guard let self else { return }
                    Task { @MainActor in
                        self.ensureTerminalView(containerId: containerId)
                        #if os(macOS)
                        if self.isContainerDisplayed(containerId),
                           self.displayView.terminal === self.terminalViews[containerId]?.terminal {
                            self.displayView.feed(byteArray: bytes[...])
                        } else {
                            self.terminalViews[containerId]?.feed(byteArray: bytes[...])
                        }
                        #else
                        self.terminalViews[containerId]?.feed(byteArray: bytes[...])
                        #endif
                    }
                },
                onLog: { line in
                    PodishLog.core(line)
                },
                onContainerState: { [weak self] items in
                    guard let self else { return }
                    Task { @MainActor in
                        self.lastContainerSnapshot = items
                        self.reconcileRunningSessions(items)
                        self.onContainerList?(items)
                        self.onContainerStateChanged?(items)
                    }
                }
            )
        }
    }

    func startIfNeeded() {
        guard !started else { return }
        started = true

        Task {
            do {
                let state = try await runtime.startDefaultShell()
                let images = try await runtime.refreshImages()
                DispatchQueue.main.async {
                    self.lastContainerSnapshot = state.containers
                    self.reconcileRunningSessions(state.containers)
                    self.onContainerList?(state.containers)
                    self.onContainerStateChanged?(state.containers)
                    self.onImageList?(images)
                    if let attached = state.attachedContainerId {
                        self.attachContainer(attached)
                    }
                    self.startupError = nil
                }
            } catch {
                DispatchQueue.main.async {
                    self.startupError = error.localizedDescription
                }
            }
        }
    }

    func stop() {
        stopForAppTermination()
    }

    func stopForAppTermination(completion: (() -> Void)? = nil) {
        if let shutdownTask {
            Task { @MainActor in
                await shutdownTask.value
                completion?()
            }
            return
        }

        let task = Task { [runtime] in
            await runtime.shutdownForAppExit()
        }
        shutdownTask = task

        Task { @MainActor [weak self] in
            await task.value
            guard let self else {
                completion?()
                return
            }

            self.shutdownTask = nil
            self.started = false
            self.activeContainerId = nil
            self.lastContainerSnapshot = []
            self.terminalViews.removeAll()
            self.terminalDelegates.removeAll()
            #if os(macOS)
            self.displayView.terminal = self.placeholderView.terminal
            self.requestDisplayRefresh()
            #endif
            completion?()
        }
    }

    func refreshContainerList() {
        Task {
            do {
                let items = try await runtime.refreshContainers()
                DispatchQueue.main.async {
                    self.lastContainerSnapshot = items
                    self.reconcileRunningSessions(items)
                    self.onContainerList?(items)
                    self.onContainerStateChanged?(items)
                }
            } catch {
                DispatchQueue.main.async { self.startupError = error.localizedDescription }
            }
        }
    }

    func refreshImageList() {
        Task {
            do {
                let items = try await runtime.refreshImages()
                DispatchQueue.main.async {
                    self.onImageList?(items)
                    self.startupError = nil
                }
            } catch {
                DispatchQueue.main.async { self.startupError = error.localizedDescription }
            }
        }
    }

    func fetchContainerInspect(_ containerId: String, completion: @escaping (Result<NativeContainerInspect, Error>) -> Void) {
        Task {
            do {
                let inspect = try await runtime.inspectContainer(containerId: containerId)
                DispatchQueue.main.async { completion(.success(inspect)) }
            } catch {
                DispatchQueue.main.async { completion(.failure(error)) }
            }
        }
    }

    func fetchContainerLogs(
        _ containerId: String,
        cursor: String? = nil,
        follow: Bool = false,
        timeoutMs: Int32 = 0,
        completion: @escaping (Result<NativeLogsChunk, Error>) -> Void
    ) {
        Task {
            do {
                let chunk = try await runtime.readLogs(
                    containerId: containerId,
                    cursor: cursor,
                    follow: follow,
                    timeoutMs: timeoutMs
                )
                DispatchQueue.main.async { completion(.success(chunk)) }
            } catch {
                DispatchQueue.main.async { completion(.failure(error)) }
            }
        }
    }

    func startContainer(_ containerId: String, completion: ((Bool) -> Void)? = nil) {
        Task {
            do {
                _ = try await runtime.startContainer(containerId: containerId)
                try await runtime.ensureTerminalSession(containerId: containerId)
                DispatchQueue.main.async {
                    self.ensureTerminalView(containerId: containerId)
                    self.startupError = nil
                    completion?(true)
                }
            } catch {
                DispatchQueue.main.async {
                    self.startupError = error.localizedDescription
                    completion?(false)
                }
            }
        }
    }

    func stopContainer(_ containerId: String, completion: ((Bool) -> Void)? = nil) {
        Task {
            do {
                _ = try await runtime.stopContainer(containerId: containerId)
                DispatchQueue.main.async {
                    // Separate the next session prompt from the previous line content.
                    self.terminalViews[containerId]?.feed(byteArray: ArraySlice([0x0D, 0x0A]))
                    self.startupError = nil
                    completion?(true)
                }
            } catch {
                DispatchQueue.main.async {
                    self.startupError = error.localizedDescription
                    completion?(false)
                }
            }
        }
    }

    func removeContainer(_ containerId: String) {
        Task {
            do {
                _ = try await runtime.removeContainer(containerId: containerId)
                DispatchQueue.main.async {
                    if self.activeContainerId == containerId {
                        self.activeContainerId = nil
                        #if os(macOS)
                        self.displayView.terminal = self.placeholderView.terminal
                        self.requestDisplayRefresh()
                        #endif
                    }
                    self.terminalViews.removeValue(forKey: containerId)
                    self.terminalDelegates.removeValue(forKey: containerId)
                    self.startupError = nil
                }
            } catch {
                DispatchQueue.main.async { self.startupError = error.localizedDescription }
            }
        }
    }

    func pullImage(_ imageRef: String) {
        Task {
            do {
                let items = try await runtime.pullImage(imageRef: imageRef)
                DispatchQueue.main.async {
                    self.onImageList?(items)
                    self.startupError = nil
                }
            } catch {
                DispatchQueue.main.async { self.startupError = error.localizedDescription }
            }
        }
    }

    func removeImage(_ imageRef: String) {
        Task {
            do {
                let items = try await runtime.removeImage(imageRef: imageRef)
                DispatchQueue.main.async {
                    self.onImageList?(items)
                    self.startupError = nil
                }
            } catch {
                DispatchQueue.main.async { self.startupError = error.localizedDescription }
            }
        }
    }

    func createContainer(
        from imageRef: String,
        name: String?,
        networkMode: PodishNetworkMode,
        portMappings: [PodishPortMapping],
        memoryQuotaBytes: Int64?
    ) {
        Task {
            do {
                let previousActiveId = await MainActor.run { self.activeContainerId }
                let containerId = try await runtime.createContainer(
                    imageRef: imageRef,
                    name: name,
                    networkMode: networkMode,
                    portMappings: portMappings,
                    memoryQuotaBytes: memoryQuotaBytes
                )
                let containers = try await runtime.refreshContainers()
                let keepCurrentActive = previousActiveId != nil
                    && containers.contains(where: { $0.containerId == previousActiveId && $0.running })

                if !keepCurrentActive {
                    try await runtime.ensureTerminalSession(containerId: containerId)
                }

                DispatchQueue.main.async {
                    self.lastContainerSnapshot = containers
                    self.reconcileRunningSessions(containers)
                    self.onContainerList?(containers)
                    self.onContainerStateChanged?(containers)
                    if !keepCurrentActive {
                        self.activateContainer(containerId)
                    }
                    self.startupError = nil
                }
            } catch {
                DispatchQueue.main.async { self.startupError = error.localizedDescription }
            }
        }
    }

    func attachContainer(_ containerId: String) {
        ensureTerminalView(containerId: containerId)
        activateContainer(containerId)

        guard isContainerRunning(containerId) else {
            // Keep showing the last buffered terminal content for non-running containers.
            return
        }

        Task {
            do {
                try await runtime.ensureTerminalSession(containerId: containerId)
                DispatchQueue.main.async {
                    if self.activeContainerId == containerId {
                        self.startupError = nil
                    }
                }
            } catch {
                DispatchQueue.main.async {
                    if self.activeContainerId == containerId {
                        self.startupError = error.localizedDescription
                    }
                }
            }
        }
    }

    private func isContainerRunning(_ containerId: String) -> Bool {
        lastContainerSnapshot.contains { $0.containerId == containerId && $0.running }
    }

    private func ensureTerminalView(containerId: String) {
        if terminalViews[containerId] != nil { return }

        let terminalView: TerminalView
        #if os(macOS)
        terminalView = PodishTerminalView(frame: .zero)
        #else
        terminalView = TerminalView(frame: .zero)
        #endif

        let delegate = TerminalDelegateAdapter()
        delegate.onInput = { [weak self] data in
            guard let self else { return }
            Task {
                do {
                    try await self.runtime.writeInput(containerId: containerId, data)
                } catch {
                    await MainActor.run { self.startupError = error.localizedDescription }
                }
            }
        }
        delegate.onResize = { [weak self] cols, rows in
            guard let self else { return }
            Task {
                await self.runtime.resize(containerId: containerId, cols: cols, rows: rows)
            }
        }

        terminalView.terminalDelegate = delegate
        terminalViews[containerId] = terminalView
        terminalDelegates[containerId] = delegate
    }

    private func reconcileRunningSessions(_ items: [NativeContainerListItem]) {
        let runningIds = Set(items.filter { $0.running }.map(\.containerId))
        let allIds = Set(items.map(\.containerId))

        for id in runningIds {
            ensureTerminalView(containerId: id)
            Task {
                try? await runtime.ensureTerminalSession(containerId: id)
            }
        }

        for (id, _) in terminalViews where !allIds.contains(id) {
            terminalViews.removeValue(forKey: id)
            terminalDelegates.removeValue(forKey: id)
            if activeContainerId == id {
                activeContainerId = nil
                #if os(macOS)
                displayView.terminal = placeholderView.terminal
                requestDisplayRefresh()
                #endif
            }
            Task {
                await runtime.closeTerminalSession(containerId: id)
            }
        }

        if let active = activeContainerId, !allIds.contains(active) {
            activeContainerId = nil
            #if os(macOS)
            displayView.terminal = placeholderView.terminal
            requestDisplayRefresh()
            #endif
        }
    }

    #if os(macOS)
    private func configureDisplayDelegate() {
        let delegate = TerminalDelegateAdapter()
        delegate.onInput = { [weak self] data in
            guard let self else { return }
            guard let active = self.activeContainerId else { return }
            Task {
                do {
                    try await self.runtime.writeInput(containerId: active, data)
                } catch {
                    await MainActor.run { self.startupError = error.localizedDescription }
                }
            }
        }
        delegate.onResize = { [weak self] cols, rows in
            guard let self else { return }
            guard let active = self.activeContainerId else { return }
            Task {
                await self.runtime.resize(containerId: active, cols: cols, rows: rows)
            }
        }
        displayView.terminalDelegate = delegate
        displayDelegate = delegate
    }
    #endif

    private func activateContainer(_ containerId: String) {
        let previousActiveId = activeContainerId
        ensureTerminalView(containerId: containerId)
        if let previousActiveId,
           let previousView = terminalViews[previousActiveId] {
            clearTransientViewState(previousView)
        }
        activeContainerId = containerId
        #if os(macOS)
        if let term = terminalViews[containerId]?.terminal {
            if let currentView = terminalViews[containerId] {
                clearTransientViewState(currentView)
            }
            clearTransientViewState(displayView)
            displayView.terminal = term
            // Re-emit current terminal geometry and force a display pass so caret/scroller
            // are recomputed immediately after switching buffers.
            displayView.sizeChanged(source: term)
            displayView.feed(byteArray: ArraySlice<UInt8>())
            requestDisplayRefresh()
        }
        #endif
    }

    #if os(macOS)
    private func isContainerDisplayed(_ containerId: String) -> Bool {
        guard activeContainerId == containerId else { return false }
        return terminalViews[containerId]?.terminal === displayView.terminal
    }
    #endif

    private func requestDisplayRefresh() {
        #if os(macOS)
        displayView.needsDisplay = true
        #else
        currentTerminalView.setNeedsDisplay()
        #endif
    }

    private func clearTransientViewState(_ view: TerminalView) {
        view.selectNone()
        view.clearSearch()
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
