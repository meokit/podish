import Foundation
import MessagePack
import SwiftUI
@preconcurrency import SwiftTerm

private enum NativeIpcEvent {
    case none
    case log(level: Int32, message: String)
    case containerStateChanged
}

enum PodishRuntimeEvent: Sendable {
    case log(String)
    case containerState([NativeContainerListItem])
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

final class PodishRuntimeActor: @unchecked Sendable {
    private struct RuntimeTerminalSession {
        var container: UnsafeMutableRawPointer
        var terminal: UnsafeMutableRawPointer
        var outputStream: AsyncStream<[UInt8]>
        var outputContinuation: AsyncStream<[UInt8]>.Continuation
    }

    private struct WorkerState {
        var ctx: UnsafeMutableRawPointer?
        var sessions: [String: RuntimeTerminalSession] = [:]
    }

    struct StartupState: Sendable {
        let containers: [NativeContainerListItem]
        let attachedContainerId: String?
    }

    private let workDir: String
    private let commandLock = NSCondition()
    private let eventLock = NSLock()
    private var pendingCommands: [(inout WorkerState) -> Void] = []
    private var state = WorkerState()
    private var workerShouldExit = false
    private var eventContinuation: AsyncStream<PodishRuntimeEvent>.Continuation?
    let events: AsyncStream<PodishRuntimeEvent>

    init(workDir: String) {
        self.workDir = workDir
        var continuation: AsyncStream<PodishRuntimeEvent>.Continuation?
        self.events = AsyncStream<PodishRuntimeEvent> { continuation = $0 }
        self.eventContinuation = continuation
        let thread = Thread { [weak self] in
            self?.workerMain()
        }
        thread.name = "PodishRuntimeWorker"
        thread.qualityOfService = .userInitiated
        thread.start()
    }

    deinit {
        commandLock.lock()
        workerShouldExit = true
        commandLock.signal()
        commandLock.unlock()
        eventLock.lock()
        eventContinuation?.finish()
        eventContinuation = nil
        eventLock.unlock()
    }

    func startDefaultShell() async throws -> StartupState {
        try await perform { [self] state in
            try self.ensureContext(state: &state)
            var containers = try self.listContainers(state: state)
            var attached: String?

            if containers.isEmpty {
                try self.pullImageInternal("docker.io/i386/alpine:latest", state: state)
                _ = try self.createAndStartContainer(
                    imageRef: "docker.io/i386/alpine:latest",
                    name: nil,
                    networkMode: .host,
                    portMappings: [],
                    memoryQuotaBytes: nil,
                    state: &state
                )
                containers = try self.listContainers(state: state)
                attached = containers.first(where: { $0.running })?.containerId ?? containers.first?.containerId
                if let attached {
                    try self.ensureTerminalSession(containerId: attached, state: &state)
                }
            }

            return StartupState(containers: containers, attachedContainerId: attached)
        }
    }

    func refreshContainers() async throws -> [NativeContainerListItem] {
        try await perform { [self] state in
            try self.ensureContext(state: &state)
            return try self.listContainers(state: state)
        }
    }

    func refreshImages() async throws -> [NativeImageListItem] {
        try await perform { [self] state in
            try self.ensureContext(state: &state)
            return try self.listImages(state: state)
        }
    }

    func inspectContainer(containerId: String) async throws -> NativeContainerInspect {
        try await perform { [self] state in
            try self.ensureContext(state: &state)
            let handle = try self.openContainer(containerId: containerId, state: state)
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

                throw PodishRuntimeError.native(code: rc, message: self.lastError(state: state))
            }
        }
    }

    func readLogs(containerId: String, cursor: String?, follow: Bool, timeoutMs: Int32) async throws -> NativeLogsChunk {
        try await perform { [self] state in
            try self.ensureContext(state: &state)
            let handle = try self.openContainer(containerId: containerId, state: state)
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

                throw PodishRuntimeError.native(code: rc, message: self.lastError(state: state))
            }
        }
    }

    func pullImage(imageRef: String) async throws -> [NativeImageListItem] {
        try await perform { [self] state in
            try self.ensureContext(state: &state)
            try self.pullImageInternal(imageRef, state: state)
            return try self.listImages(state: state)
        }
    }

    func removeImage(imageRef: String) async throws -> [NativeImageListItem] {
        try await perform { [self] state in
            try self.ensureContext(state: &state)
            let rc = imageRef.withCString { pod_image_remove(state.ctx, $0, 1) }
            if rc != 0 {
                throw PodishRuntimeError.native(code: rc, message: self.lastError(state: state))
            }
            return try self.listImages(state: state)
        }
    }

    func createContainer(
        imageRef: String,
        name: String?,
        networkMode: PodishNetworkMode,
        portMappings: [PodishPortMapping],
        memoryQuotaBytes: Int64?
    ) async throws -> String {
        try await perform { [self] state in
            try self.ensureContext(state: &state)
            return try self.createAndStartContainer(
                imageRef: imageRef,
                name: name,
                networkMode: networkMode,
                portMappings: portMappings,
                memoryQuotaBytes: memoryQuotaBytes,
                state: &state
            )
        }
    }

    func startContainer(containerId: String) async throws -> [NativeContainerListItem] {
        try await perform { [self] state in
            try self.ensureContext(state: &state)
            let handle = try self.openContainer(containerId: containerId, state: state)
            defer { pod_container_close(handle) }

            let rc = pod_container_start(handle)
            if rc != 0 {
                throw PodishRuntimeError.native(code: rc, message: self.lastError(state: state))
            }
            return try self.listContainers(state: state)
        }
    }

    func stopContainer(containerId: String, signal: Int32 = 15, timeoutMs: Int32 = 2000) async throws -> [NativeContainerListItem] {
        try await perform { [self] state in
            try self.ensureContext(state: &state)
            let handle = try self.openContainer(containerId: containerId, state: state)
            defer { pod_container_close(handle) }

            let rc = pod_container_stop(handle, signal, timeoutMs)
            if rc != 0 {
                throw PodishRuntimeError.native(code: rc, message: self.lastError(state: state))
            }

            self.closeTerminalSession(containerId: containerId, state: &state)
            return try self.listContainers(state: state)
        }
    }

    func removeContainer(containerId: String) async throws -> [NativeContainerListItem] {
        try await perform { [self] state in
            try self.ensureContext(state: &state)
            let handle = try self.openContainer(containerId: containerId, state: state)
            defer { pod_container_close(handle) }

            let rc = pod_container_remove(handle, 1)
            if rc != 0 {
                throw PodishRuntimeError.native(code: rc, message: self.lastError(state: state))
            }

            self.closeTerminalSession(containerId: containerId, state: &state)
            return try self.listContainers(state: state)
        }
    }

    func attachTerminal(containerId: String) async throws -> AsyncStream<[UInt8]> {
        try await perform { [self] state in
            try self.ensureContext(state: &state)
            try self.ensureTerminalSession(containerId: containerId, state: &state)
            guard let outputStream = state.sessions[containerId]?.outputStream else {
                throw PodishRuntimeError.native(code: -1, message: "terminal stream unavailable")
            }
            return outputStream
        }
    }

    func closeTerminalSession(containerId: String) async {
        await perform { [self] state in
            self.closeTerminalSession(containerId: containerId, state: &state)
        }
    }

    func writeInput(containerId: String, _ bytes: [UInt8]) async throws {
        try await perform { [self] state in
            guard !bytes.isEmpty, let term = state.sessions[containerId]?.terminal else { return }

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
                    throw PodishRuntimeError.native(code: rc, message: self.lastError(state: state))
                }
                if wrote <= 0 { break }
                offset += Int(wrote)
            }
        }
    }

    func resize(containerId: String, cols: Int, rows: Int) async {
        await perform { state in
            guard let term = state.sessions[containerId]?.terminal else { return }
            let safeRows = UInt16(max(0, min(65535, rows)))
            let safeCols = UInt16(max(0, min(65535, cols)))
            _ = pod_terminal_resize(term, safeRows, safeCols)
        }
    }

    func stop() async {
        await perform { [self] state in
            self.teardownRuntime(destroyContext: true, state: &state)
        }
    }

    func shutdownForAppExit(stopTimeoutMs: Int32 = 10_000) async {
        await perform { [self] state in
            if state.ctx != nil {
                let runningIds = ((try? self.listContainers(state: state)) ?? [])
                    .filter { $0.running }
                    .map(\.containerId)

                for containerId in runningIds {
                    do {
                        _ = try self.stopContainer(containerId: containerId, signal: 15, timeoutMs: stopTimeoutMs, state: &state)
                    } catch {
                        self.emitLog("shutdown: stop container \(containerId) failed: \(error.localizedDescription)")
                    }
                }
            }

            self.teardownRuntime(destroyContext: false, state: &state)
        }
    }

    private func workerMain() {
        while true {
            autoreleasepool {
                var commands: [(inout WorkerState) -> Void] = []

                commandLock.lock()
                while pendingCommands.isEmpty && state.ctx == nil && !workerShouldExit {
                    commandLock.wait()
                }

                if workerShouldExit {
                    teardownRuntime(destroyContext: true, state: &state)
                    commandLock.unlock()
                    return
                }

                if !pendingCommands.isEmpty {
                    commands = pendingCommands
                    pendingCommands.removeAll()
                }
                commandLock.unlock()

                for command in commands {
                    command(&state)
                }

                if state.ctx != nil {
                    pollEvent(state: &state, timeoutMs: commands.isEmpty ? 50 : 0)
                    pollTerminals(state: &state)
                }
            }
        }
    }

    private func enqueue(_ command: @escaping (inout WorkerState) -> Void) {
        commandLock.lock()
        pendingCommands.append(command)
        commandLock.signal()
        commandLock.unlock()
    }

    private func perform<T: Sendable>(_ body: @escaping (inout WorkerState) throws -> T) async throws -> T {
        try await withCheckedThrowingContinuation { continuation in
            enqueue { state in
                do {
                    continuation.resume(returning: try body(&state))
                } catch {
                    continuation.resume(throwing: error)
                }
            }
        }
    }

    private func perform(_ body: @escaping (inout WorkerState) -> Void) async {
        await withCheckedContinuation { continuation in
            enqueue { state in
                body(&state)
                continuation.resume()
            }
        }
    }

    private func pollEvent(state: inout WorkerState, timeoutMs: Int32) {
        guard let ctx = state.ctx else { return }

        let args = encodePollEventArgs(timeoutMs: timeoutMs)
        var capacity = 1024

        while true {
            var response = [UInt8](repeating: 0, count: capacity)
            var outLen: Int32 = 0

            let rc = args.withUnsafeBufferPointer { argsPtr in
                response.withUnsafeMutableBufferPointer { responsePtr in
                    pod_ctx_call_msgpack(
                        ctx,
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

            guard rc == 0 else { return }

            let n = max(0, min(Int(outLen), response.count))
            guard n > 0, let event = decodeNativeIpcEvent(Array(response.prefix(n))) else { return }

            switch event {
            case .none:
                return
            case .log(_, let message):
                if !message.isEmpty {
                    emitLog(message)
                }
            case .containerStateChanged:
                if let items = try? listContainers(state: state) {
                    emitContainerState(items)
                }
            }
            return
        }
    }

    private func pollTerminals(state: inout WorkerState) {
        for (containerId, session) in state.sessions {
            var localBuffer = [UInt8](repeating: 0, count: 4096)
            var count: Int32 = 0
            let rc = localBuffer.withUnsafeMutableBufferPointer { ptr in
                pod_terminal_read(session.terminal, ptr.baseAddress, Int32(ptr.count), 0, &count)
            }

            guard rc == 0, count > 0 else { continue }
            emitOutput(containerId: containerId, chunk: Array(localBuffer.prefix(Int(count))), state: &state)
        }
    }

    private func emitLog(_ message: String) {
        emitEvent(.log(message))
    }

    private func emitContainerState(_ items: [NativeContainerListItem]) {
        emitEvent(.containerState(items))
    }

    private func emitEvent(_ event: PodishRuntimeEvent) {
        eventLock.lock()
        let continuation = eventContinuation
        eventLock.unlock()
        continuation?.yield(event)
    }

    private func teardownRuntime(destroyContext: Bool, state: inout WorkerState) {
        for (containerId, _) in state.sessions {
            closeTerminalSession(containerId: containerId, state: &state)
        }
        state.sessions.removeAll()

        if let c = state.ctx {
            if destroyContext {
                pod_ctx_destroy(c)
            }
            state.ctx = nil
        }
    }

    private func ensureContext(state: inout WorkerState) throws {
        if state.ctx != nil { return }

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

        state.ctx = out
    }

    private func ensureTerminalSession(containerId: String, state: inout WorkerState) throws {
        if state.sessions[containerId] != nil { return }

        let container = try openContainer(containerId: containerId, state: state)
        var terminal: UnsafeMutableRawPointer?
        let rc = pod_terminal_attach(container, &terminal)
        if rc != 0 || terminal == nil {
            pod_container_close(container)
            throw PodishRuntimeError.native(code: rc, message: lastError(state: state))
        }

        var outputContinuation: AsyncStream<[UInt8]>.Continuation?
        let outputStream = AsyncStream<[UInt8]> { continuation in
            outputContinuation = continuation
        }
        guard let outputContinuation else {
            pod_terminal_close(terminal!)
            pod_container_close(container)
            throw PodishRuntimeError.native(code: -1, message: "failed to create terminal output stream")
        }

        state.sessions[containerId] = RuntimeTerminalSession(
            container: container,
            terminal: terminal!,
            outputStream: outputStream,
            outputContinuation: outputContinuation
        )
    }

    private func closeTerminalSession(containerId: String, state: inout WorkerState) {
        guard let session = state.sessions.removeValue(forKey: containerId) else { return }
        session.outputContinuation.finish()
        pod_terminal_close(session.terminal)
        pod_container_close(session.container)
    }

    private func emitOutput(containerId: String, chunk: [UInt8], state: inout WorkerState) {
        state.sessions[containerId]?.outputContinuation.yield(chunk)
    }

    private func stopContainer(
        containerId: String,
        signal: Int32,
        timeoutMs: Int32,
        state: inout WorkerState
    ) throws -> [NativeContainerListItem] {
        let handle = try openContainer(containerId: containerId, state: state)
        defer { pod_container_close(handle) }

        let rc = pod_container_stop(handle, signal, timeoutMs)
        if rc != 0 {
            throw PodishRuntimeError.native(code: rc, message: lastError(state: state))
        }

        closeTerminalSession(containerId: containerId, state: &state)
        return try listContainers(state: state)
    }

    private func pullImageInternal(_ image: String, state: WorkerState) throws {
        guard let c = state.ctx else { throw PodishRuntimeError.native(code: -1, message: "context nil") }
        let rc = image.withCString { pod_image_pull(c, $0) }
        if rc != 0 {
            throw PodishRuntimeError.native(code: rc, message: lastError(state: state))
        }
    }

    private func listContainers(state: WorkerState) throws -> [NativeContainerListItem] {
        guard let c = state.ctx else { throw PodishRuntimeError.native(code: -1, message: "context nil") }

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

            throw PodishRuntimeError.native(code: rc, message: lastError(state: state))
        }
    }

    private func listImages(state: WorkerState) throws -> [NativeImageListItem] {
        guard let c = state.ctx else { throw PodishRuntimeError.native(code: -1, message: "context nil") }

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

            throw PodishRuntimeError.native(code: rc, message: lastError(state: state))
        }
    }

    private func createAndStartContainer(
        imageRef: String,
        name: String?,
        networkMode: PodishNetworkMode,
        portMappings: [PodishPortMapping],
        memoryQuotaBytes: Int64?,
        state: inout WorkerState
    ) throws -> String {
        guard let c = state.ctx else { throw PodishRuntimeError.native(code: -1, message: "context nil") }
        let beforeIds = Set(try listContainers(state: state).map(\.containerId))
        let publishedPorts = portMappings.map { mapping in
            PodishRunSpec.PublishedPortSpec(
                hostPort: mapping.hostPort,
                containerPort: mapping.containerPort,
                protocolValue: 0,
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
            throw PodishRuntimeError.native(code: rcCreate, message: lastError(state: state))
        }
        guard let outContainer else {
            throw PodishRuntimeError.native(code: -1, message: "container handle nil")
        }
        defer { pod_container_close(outContainer) }

        let rcStart = pod_container_start(outContainer)
        if rcStart != 0 {
            throw PodishRuntimeError.native(code: rcStart, message: lastError(state: state))
        }

        let after = try listContainers(state: state)
        if let created = after.first(where: { !beforeIds.contains($0.containerId) }) {
            return created.containerId
        }
        if let running = after.first(where: { $0.running && $0.image == imageRef }) {
            return running.containerId
        }

        throw PodishRuntimeError.native(code: -1, message: "failed to resolve created container id")
    }

    private func openContainer(containerId: String, state: WorkerState) throws -> UnsafeMutableRawPointer {
        guard let c = state.ctx else { throw PodishRuntimeError.native(code: -1, message: "context nil") }
        var outContainer: UnsafeMutableRawPointer?
        let rc = containerId.withCString { pod_container_open(c, $0, &outContainer) }
        if rc != 0 || outContainer == nil {
            throw PodishRuntimeError.native(code: rc, message: lastError(state: state))
        }
        return outContainer!
    }

    private func lastError(state: WorkerState) -> String {
        guard let c = state.ctx else { return "unknown" }
        var buf = [UInt8](repeating: 0, count: 1024)
        _ = pod_ctx_last_error(c, &buf, Int32(buf.count))
        let end = buf.firstIndex(of: 0) ?? buf.count
        return String(decoding: buf[..<end], as: UTF8.self)
    }
}

@MainActor
final class PodishTerminalSession: ObservableObject {
    let appearance: PodishTerminalAppearance
    private let placeholderView: TerminalView
    #if os(macOS)
    private let displayView: TerminalView
    #endif
    private let runtime: PodishRuntimeActor
    private var terminalViews: [String: TerminalView] = [:]
    private var terminalDelegates: [String: TerminalDelegateAdapter] = [:]
    private var terminalOutputTasks: [String: Task<Void, Never>] = [:]
    #if os(macOS)
    private var displayDelegate: TerminalDelegateAdapter?
    #endif

    @Published var startupError: String?
    @Published private(set) var activeContainerId: String?

    private var started = false
    private var shutdownTask: Task<Void, Never>?
    private var runtimeEventTask: Task<Void, Never>?
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

    var hasActiveTerminal: Bool {
        activeContainerId != nil
    }

    var terminalBackgroundColor: SwiftUI.Color {
        appearance.terminalBackgroundColor
    }

    init(appearance: PodishTerminalAppearance = PodishTerminalAppearance()) {
        self.appearance = appearance
        #if os(macOS)
        placeholderView = PodishTerminalView(frame: .zero)
        displayView = PodishTerminalView(frame: .zero)
        #else
        placeholderView = PodishTerminalView(frame: .zero)
        #endif
        runtime = PodishRuntimeActor(workDir: PodishTerminalSession.makeWorkDir())
        #if os(iOS)
        placeholderView.inputAccessoryView = nil
        placeholderView.keyboardDismissMode = .interactive
        #endif
        self.appearance.apply(to: placeholderView)
        #if os(macOS)
        self.appearance.apply(to: displayView)
        configureDisplayDelegate()
        displayView.terminal = placeholderView.terminal
        #endif

        runtimeEventTask = Task { [weak self] in
            guard let self else { return }
            for await event in self.runtime.events {
                self.handleRuntimeEvent(event)
            }
        }
    }

    deinit {
        runtimeEventTask?.cancel()
    }

    func startIfNeeded() {
        guard !started else { return }
        started = true

        Task {
            do {
                let state = try await runtime.startDefaultShell()
                let images = try await runtime.refreshImages()
                await MainActor.run {
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
                await MainActor.run {
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
            self.runtimeEventTask?.cancel()
            self.runtimeEventTask = nil
            self.cancelAllTerminalOutputTasks()
            self.terminalViews.removeAll()
            self.terminalDelegates.removeAll()
            #if os(macOS)
            self.displayView.terminal = self.placeholderView.terminal
            self.requestDisplayRefresh()
            #endif
            completion?()
        }
    }

    private func handleRuntimeEvent(_ event: PodishRuntimeEvent) {
        switch event {
        case .log(let line):
            PodishLog.core(line)
        case .containerState(let items):
            lastContainerSnapshot = items
            reconcileRunningSessions(items)
            onContainerList?(items)
            onContainerStateChanged?(items)
        }
    }

    func refreshContainerList() {
        Task {
            do {
                let items = try await runtime.refreshContainers()
                await MainActor.run {
                    self.lastContainerSnapshot = items
                    self.reconcileRunningSessions(items)
                    self.onContainerList?(items)
                    self.onContainerStateChanged?(items)
                }
            } catch {
                await MainActor.run { self.startupError = error.localizedDescription }
            }
        }
    }

    func refreshImageList() {
        Task {
            do {
                let items = try await runtime.refreshImages()
                await MainActor.run {
                    self.onImageList?(items)
                    self.startupError = nil
                }
            } catch {
                await MainActor.run { self.startupError = error.localizedDescription }
            }
        }
    }

    func fetchContainerInspect(_ containerId: String, completion: @escaping (Result<NativeContainerInspect, Error>) -> Void) {
        Task {
            do {
                let inspect = try await runtime.inspectContainer(containerId: containerId)
                await MainActor.run { completion(.success(inspect)) }
            } catch {
                await MainActor.run { completion(.failure(error)) }
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
                await MainActor.run { completion(.success(chunk)) }
            } catch {
                await MainActor.run { completion(.failure(error)) }
            }
        }
    }

    func startContainer(_ containerId: String, completion: ((Bool) -> Void)? = nil) {
        Task {
            do {
                _ = try await runtime.startContainer(containerId: containerId)
                self.startTerminalOutputTask(containerId: containerId)
                await MainActor.run {
                    self.ensureTerminalView(containerId: containerId)
                    self.startupError = nil
                    completion?(true)
                }
            } catch {
                await MainActor.run {
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
                await MainActor.run {
                    // Separate the next session prompt from the previous line content.
                    self.terminalViews[containerId]?.feed(byteArray: ArraySlice([0x0D, 0x0A]))
                    self.startupError = nil
                    completion?(true)
                }
            } catch {
                await MainActor.run {
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
                await MainActor.run {
                    if self.activeContainerId == containerId {
                        self.activeContainerId = nil
                        #if os(macOS)
                        self.displayView.terminal = self.placeholderView.terminal
                        self.requestDisplayRefresh()
                        #endif
                    }
                    self.terminalViews.removeValue(forKey: containerId)
                    self.terminalDelegates.removeValue(forKey: containerId)
                    self.cancelTerminalOutputTask(containerId: containerId)
                    self.startupError = nil
                }
            } catch {
                await MainActor.run { self.startupError = error.localizedDescription }
            }
        }
    }

    func pullImage(_ imageRef: String) {
        Task {
            do {
                let items = try await runtime.pullImage(imageRef: imageRef)
                await MainActor.run {
                    self.onImageList?(items)
                    self.startupError = nil
                }
            } catch {
                await MainActor.run { self.startupError = error.localizedDescription }
            }
        }
    }

    func removeImage(_ imageRef: String) {
        Task {
            do {
                let items = try await runtime.removeImage(imageRef: imageRef)
                await MainActor.run {
                    self.onImageList?(items)
                    self.startupError = nil
                }
            } catch {
                await MainActor.run { self.startupError = error.localizedDescription }
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
                    self.startTerminalOutputTask(containerId: containerId)
                }

                await MainActor.run {
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
                await MainActor.run { self.startupError = error.localizedDescription }
            }
        }
    }

    func attachContainer(_ containerId: String) {
        PodishLog.ui("attachContainer start id=\(containerId)")
        ensureTerminalView(containerId: containerId)
        activateContainer(containerId)

        guard isContainerRunning(containerId) else {
            // Keep showing the last buffered terminal content for non-running containers.
            PodishLog.ui("attachContainer id=\(containerId) not running; skip output task")
            return
        }

        Task {
            self.startTerminalOutputTask(containerId: containerId)
            await MainActor.run {
                if self.activeContainerId == containerId {
                    self.startupError = nil
                }
                PodishLog.ui("attachContainer completed id=\(containerId) active=\(self.activeContainerId == containerId)")
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
        terminalView = PodishTerminalView(frame: .zero)
        #endif
        #if os(iOS)
        terminalView.inputAccessoryView = nil
        terminalView.keyboardDismissMode = .interactive
        #endif
        appearance.apply(to: terminalView)

        let delegate = TerminalDelegateAdapter()
        delegate.onInput = { [weak self] data in
            Task { @MainActor [weak self] in
                guard let self else { return }
                do {
                    try await self.runtime.writeInput(containerId: containerId, data)
                } catch {
                    self.startupError = error.localizedDescription
                }
            }
        }
        delegate.onResize = { [weak self] cols, rows in
            Task { @MainActor [weak self] in
                guard let self else { return }
                await self.runtime.resize(containerId: containerId, cols: cols, rows: rows)
            }
        }

        terminalView.terminalDelegate = delegate
        terminalViews[containerId] = terminalView
        terminalDelegates[containerId] = delegate
        PodishLog.ui("ensureTerminalView created id=\(containerId)")
        startTerminalOutputTask(containerId: containerId)
    }

    private func reconcileRunningSessions(_ items: [NativeContainerListItem]) {
        let runningIds = Set(items.filter { $0.running }.map(\.containerId))
        let allIds = Set(items.map(\.containerId))

        for id in runningIds {
            ensureTerminalView(containerId: id)
            startTerminalOutputTask(containerId: id)
        }

        for (id, _) in terminalViews where !allIds.contains(id) {
            terminalViews.removeValue(forKey: id)
            terminalDelegates.removeValue(forKey: id)
            cancelTerminalOutputTask(containerId: id)
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
            Task { @MainActor [weak self] in
                guard let self else { return }
                guard let active = self.activeContainerId else { return }
                do {
                    try await self.runtime.writeInput(containerId: active, data)
                } catch {
                    self.startupError = error.localizedDescription
                }
            }
        }
        delegate.onResize = { [weak self] cols, rows in
            Task { @MainActor [weak self] in
                guard let self else { return }
                guard let active = self.activeContainerId else { return }
                await self.runtime.resize(containerId: active, cols: cols, rows: rows)
            }
        }
        displayView.terminalDelegate = delegate
        displayDelegate = delegate
    }
    #endif

    private func activateContainer(_ containerId: String) {
        let previousActiveId = activeContainerId
        PodishLog.ui("activateContainer start id=\(containerId) previous=\(previousActiveId ?? "nil")")
        ensureTerminalView(containerId: containerId)
        if let previousActiveId,
           let previousView = terminalViews[previousActiveId] {
            clearTransientViewState(previousView)
        }
        activeContainerId = containerId
        PodishLog.ui("activateContainer set active id=\(containerId)")
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

    private func startTerminalOutputTask(containerId: String) {
        guard terminalOutputTasks[containerId] == nil else { return }

        terminalOutputTasks[containerId] = Task { [weak self] in
            guard let self else { return }
            do {
                let stream = try await self.runtime.attachTerminal(containerId: containerId)
                for await bytes in stream {
                    await MainActor.run {
                        self.feedTerminalOutput(containerId: containerId, bytes: bytes)
                    }
                }
            } catch {
                await MainActor.run {
                    if self.activeContainerId == containerId {
                        self.startupError = error.localizedDescription
                    }
                }
            }

            _ = await MainActor.run {
                self.terminalOutputTasks.removeValue(forKey: containerId)
            }
        }
    }

    private func cancelTerminalOutputTask(containerId: String) {
        terminalOutputTasks.removeValue(forKey: containerId)?.cancel()
    }

    private func cancelAllTerminalOutputTasks() {
        for (_, task) in terminalOutputTasks {
            task.cancel()
        }
        terminalOutputTasks.removeAll()
    }

    private func feedTerminalOutput(containerId: String, bytes: [UInt8]) {
        ensureTerminalView(containerId: containerId)
        #if os(macOS)
        if isContainerDisplayed(containerId),
           displayView.terminal === terminalViews[containerId]?.terminal {
            displayView.feed(byteArray: bytes[...])
        } else {
            terminalViews[containerId]?.feed(byteArray: bytes[...])
        }
        #else
        terminalViews[containerId]?.feed(byteArray: bytes[...])
        #endif
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
    let shouldFocus: Bool

    func makeUIView(context: Context) -> TerminalView {
        PodishLog.ui("TerminalViewHost makeUIView terminal=\(ObjectIdentifier(terminalView))")
        return terminalView
    }

    func updateUIView(_ uiView: TerminalView, context: Context) {
        PodishLog.ui("TerminalViewHost updateUIView terminal=\(ObjectIdentifier(uiView)) shouldFocus=\(shouldFocus) window=\(uiView.window != nil) firstResponder=\(uiView.isFirstResponder)")
        guard shouldFocus else { return }
        guard uiView.window != nil else { return }
        guard !uiView.isFirstResponder else { return }

        Task { @MainActor in
            PodishLog.ui("TerminalViewHost focus task terminal=\(ObjectIdentifier(uiView)) window=\(uiView.window != nil) firstResponder=\(uiView.isFirstResponder)")
            guard uiView.window != nil else { return }
            guard !uiView.isFirstResponder else { return }
            _ = uiView.becomeFirstResponder()
        }
    }
}
#endif
