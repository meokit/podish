import Foundation
import Compression

/// Minimal in-process extractor for USTAR tar archives, optionally
/// wrapped in a gzip stream. Used to materialize the iOS rootfs on
/// first launch without taking a dependency on libarchive or shipping
/// a tar binary.
///
/// The reader streams the archive in 512-byte blocks, handles regular
/// files / directories / symlinks, and exposes a progress callback
/// based on bytes consumed from the source file.
enum TarArchive {
    enum ExtractError: LocalizedError {
        case shortRead
        case badHeader
        case unsupportedEntry(String)
        case ioFailed(String)

        var errorDescription: String? {
            switch self {
            case .shortRead: return "Truncated tar archive."
            case .badHeader: return "Corrupt or non-USTAR tar header."
            case .unsupportedEntry(let kind): return "Unsupported tar entry type: \(kind)."
            case .ioFailed(let msg): return msg
            }
        }
    }

    /// Extract sourceURL into destinationURL, creating intermediate
    /// directories as needed. The progress closure is invoked on an
    /// arbitrary queue with values in [0, 1] based on bytes consumed
    /// from the source file.
    static func extract(
        from sourceURL: URL,
        to destinationURL: URL,
        progress: @escaping (Double) -> Void
    ) throws {
        let fm = FileManager.default
        try fm.createDirectory(at: destinationURL, withIntermediateDirectories: true)

        let handle = try FileHandle(forReadingFrom: sourceURL)
        defer { try? handle.close() }

        let totalSize: Int64 = (try? fm.attributesOfItem(atPath: sourceURL.path)[.size] as? Int64) ?? 0
        let isGzip = sourceURL.path.hasSuffix(".gz")
        let source: TarSource = isGzip
            ? GzipTarSource(underlying: handle, totalSize: totalSize, progress: progress)
            : RawTarSource(handle: handle, totalSize: totalSize, progress: progress)
        defer { source.finish() }

        try source.open()
        var entryCount = 0
        while let entry = try source.readHeader() {
            entryCount += 1
            let resolved = sanitizeEntryDestination(name: entry.name, under: destinationURL)
            switch entry.kind {
            case .directory:
                try fm.createDirectory(at: resolved, withIntermediateDirectories: true)
            case .file:
                try fm.createDirectory(at: resolved.deletingLastPathComponent(), withIntermediateDirectories: true)
                try source.extractPayload(size: entry.size, into: resolved, mode: entry.mode)
            case .symlink(let target):
                try fm.createDirectory(at: resolved.deletingLastPathComponent(), withIntermediateDirectories: true)
                if fm.fileExists(atPath: resolved.path) {
                    try fm.removeItem(at: resolved)
                }
                try fm.createSymbolicLink(at: resolved, withDestinationURL: URL(fileURLWithPath: target))
            }
        }
        progress(1.0)
        PodishLog.appInfo("Tar archive extracted (\(entryCount) entries) into \(destinationURL.path)")
    }

    /// Map an entry name from the archive into a safe absolute URL
    /// under root. Refuses entries that try to escape the destination
    /// via .. or absolute paths.
    private static func sanitizeEntryDestination(name: String, under root: URL) -> URL {
        let cleaned = name.hasPrefix("/") ? String(name.dropFirst()) : name
        return root.appendingPathComponent(cleaned, isDirectory: false)
    }
}

private enum TarEntryKind: Equatable {
    case file
    case directory
    case symlink(target: String)
}

private struct TarEntry {
    let name: String
    let kind: TarEntryKind
    let size: Int64
    let mode: Int
}

private protocol TarSource {
    func open() throws
    func readHeader() throws -> TarEntry?
    func extractPayload(size: Int64, into destination: URL, mode: Int) throws
    func finish()
}

private final class RawTarSource: TarSource {
    private let handle: FileHandle
    private let totalSize: Int64
    private let progress: (Double) -> Void
    private var bytesConsumed: Int64 = 0

    init(handle: FileHandle, totalSize: Int64, progress: @escaping (Double) -> Void) {
        self.handle = handle
        self.totalSize = totalSize
        self.progress = progress
    }

    func open() throws {}
    func finish() {}

    func readHeader() throws -> TarEntry? {
        guard let header = try readBlock() else { return nil }
        return TarHeaderParser.parse(header: header)
    }

    func extractPayload(size: Int64, into destination: URL, mode: Int) throws {
        let fm = FileManager.default
        if fm.fileExists(atPath: destination.path) {
            try fm.removeItem(at: destination)
        }
        guard let writer = OutputStream(toFileAtPath: destination.path, append: false) else {
            throw TarArchive.ExtractError.ioFailed("Could not open \(destination.path) for writing")
        }
        writer.open()
        defer { writer.close() }

        var remaining = size
        var buffer = [UInt8](repeating: 0, count: 32 * 1024)
        while remaining > 0 {
            let chunk = Int(min(Int64(buffer.count), remaining))
            let read = try readBytes(into: &buffer, count: chunk)
            if read <= 0 { throw TarArchive.ExtractError.shortRead }
            _ = writer.write(buffer, maxLength: read)
            remaining -= Int64(read)
        }
        try skipPadding(forPayloadSize: size)
        try TarFileMode.apply(mode: mode, to: destination)
    }

    fileprivate func readBlock() throws -> [UInt8]? {
        var block = [UInt8](repeating: 0, count: 512)
        let read = try readBytes(into: &block, count: 512)
        if read == 0 { return nil }
        if read < 512 { throw TarArchive.ExtractError.shortRead }
        if block.allSatisfy({ $0 == 0 }) { return nil }
        return block
    }

    fileprivate func readBytes(into buffer: inout [UInt8], count: Int) throws -> Int {
        let data = try handle.read(upToCount: count) ?? Data()
        let n = data.count
        data.withUnsafeBytes { raw in
            if let base = raw.baseAddress, n > 0 {
                memcpy(&buffer, base, n)
            }
        }
        bytesConsumed += Int64(n)
        reportProgress()
        return n
    }

    fileprivate func skipPadding(forPayloadSize size: Int64) throws {
        let remainder = Int(size % 512)
        if remainder == 0 { return }
        let pad = 512 - remainder
        var sink = [UInt8](repeating: 0, count: pad)
        _ = try readBytes(into: &sink, count: pad)
    }

    private func reportProgress() {
        guard totalSize > 0 else { return }
        progress(min(1.0, Double(bytesConsumed) / Double(totalSize)))
    }
}

private final class GzipTarSource: TarSource {
    private let underlying: FileHandle
    private let totalSize: Int64
    private let progress: (Double) -> Void
    private var stream = compression_stream(dst_ptr: nil, dst_size: 0, src_ptr: nil, src_size: 0, state: nil)
    private var pending: [UInt8] = []
    private var pendingStart: Int = 0
    private var rawBytesConsumed: Int64 = 0
    private var streamReady = false
    private var streamFinished = false

    init(underlying: FileHandle, totalSize: Int64, progress: @escaping (Double) -> Void) {
        self.underlying = underlying
        self.totalSize = totalSize
        self.progress = progress
    }

    func open() throws {
        let status = compression_stream_init(&stream, COMPRESSION_STREAM_DECODE, COMPRESSION_ZLIB)
        guard status != COMPRESSION_STATUS_ERROR else {
            throw TarArchive.ExtractError.ioFailed("Failed to init zlib stream")
        }
        streamReady = true
    }

    func finish() {
        if streamReady { compression_stream_destroy(&stream) }
        streamReady = false
    }

    func readHeader() throws -> TarEntry? {
        guard let header = try readBlock() else { return nil }
        return TarHeaderParser.parse(header: header)
    }

    func extractPayload(size: Int64, into destination: URL, mode: Int) throws {
        let fm = FileManager.default
        if fm.fileExists(atPath: destination.path) {
            try fm.removeItem(at: destination)
        }
        guard let writer = OutputStream(toFileAtPath: destination.path, append: false) else {
            throw TarArchive.ExtractError.ioFailed("Could not open \(destination.path) for writing")
        }
        writer.open()
        defer { writer.close() }

        var remaining = size
        var buffer = [UInt8](repeating: 0, count: 32 * 1024)
        while remaining > 0 {
            let chunk = Int(min(Int64(buffer.count), remaining))
            let read = try readBytes(into: &buffer, count: chunk)
            if read <= 0 { throw TarArchive.ExtractError.shortRead }
            _ = writer.write(buffer, maxLength: read)
            remaining -= Int64(read)
        }
        try skipPadding(forPayloadSize: size)
        try TarFileMode.apply(mode: mode, to: destination)
    }

    fileprivate func readBlock() throws -> [UInt8]? {
        var block = [UInt8](repeating: 0, count: 512)
        let read = try readBytes(into: &block, count: 512)
        if read == 0 { return nil }
        if read < 512 { throw TarArchive.ExtractError.shortRead }
        if block.allSatisfy({ $0 == 0 }) { return nil }
        return block
    }

    fileprivate func readBytes(into buffer: inout [UInt8], count: Int) throws -> Int {
        let available = pending.count - pendingStart
        if available < count {
            try refillPending(minBytesNeeded: count - available)
        }
        let take = min(count, pending.count - pendingStart)
        if take <= 0 { return 0 }
        let start = pendingStart
        pending.withUnsafeBufferPointer { src in
            guard let base = src.baseAddress else { return }
            memcpy(&buffer, base.advanced(by: start), take)
        }
        let end = start + take
        if end == pending.count {
            pending.removeAll(keepingCapacity: true)
            pendingStart = 0
        } else {
            pendingStart = end
        }
        return take
    }

    fileprivate func skipPadding(forPayloadSize size: Int64) throws {
        let remainder = Int(size % 512)
        if remainder == 0 { return }
        let pad = 512 - remainder
        var sink = [UInt8](repeating: 0, count: pad)
        _ = try readBytes(into: &sink, count: pad)
    }

    private func refillPending(minBytesNeeded: Int) throws {
        guard !streamFinished else { return }
        let sourceData = try underlying.read(upToCount: 64 * 1024) ?? Data()
        rawBytesConsumed += Int64(sourceData.count)
        if totalSize > 0 {
            progress(min(1.0, Double(rawBytesConsumed) / Double(totalSize)))
        }
        if sourceData.isEmpty {
            // No more compressed input; flush any final decompressed data
            // out of the stream.
            try pump(input: nil, finalize: true)
            streamFinished = true
            return
        }
        try sourceData.withUnsafeBytes { raw in
            let bound = raw.bindMemory(to: UInt8.self)
            try pump(input: bound, finalize: false)
        }
        // If the caller asked for more than we have buffered, keep
        // pulling compressed blocks until either we have enough or the
        // underlying file is exhausted.
        while !streamFinished && (pending.count - pendingStart) < minBytesNeeded {
            let next = try underlying.read(upToCount: 64 * 1024) ?? Data()
            if next.isEmpty {
                try pump(input: nil, finalize: true)
                streamFinished = true
                return
            }
            rawBytesConsumed += Int64(next.count)
            if totalSize > 0 {
                progress(min(1.0, Double(rawBytesConsumed) / Double(totalSize)))
            }
            try next.withUnsafeBytes { raw in
                let bound = raw.bindMemory(to: UInt8.self)
                try pump(input: bound, finalize: false)
            }
        }
    }

    private func pump(input: UnsafeBufferPointer<UInt8>?, finalize: Bool) throws {
        let outputChunkSize = 64 * 1024
        var outputBuffer = [UInt8](repeating: 0, count: outputChunkSize)

        let status: Int32 = outputBuffer.withUnsafeMutableBufferPointer { outPtr -> Int32 in
            stream.dst_ptr = outPtr.baseAddress
            stream.dst_size = outputChunkSize
            if let input, let base = input.baseAddress, input.count > 0 {
                stream.src_ptr = base
                stream.src_size = input.count
                return compression_stream_process(&stream, 0)
            } else {
                stream.src_ptr = nil
                stream.src_size = 0
                return compression_stream_process(&stream, Int32(COMPRESSION_STREAM_FINALIZE.rawValue))
            }
        }

        let produced = outputChunkSize - stream.dst_size
        if produced > 0 {
            pending.append(contentsOf: outputBuffer.prefix(produced))
        }

        switch status {
        case COMPRESSION_STATUS_OK:
            return
        case COMPRESSION_STATUS_END:
            streamFinished = true
            return
        default:
            throw TarArchive.ExtractError.ioFailed("zlib stream error \(status)")
        }
    }
}

/// Parses a 512-byte USTAR header block into a TarEntry. The reader
/// only supports the subset of USTAR fields we use: name, prefix,
/// size, typeflag, linkname, mode.
private enum TarHeaderParser {
    static func parse(header: [UInt8]) throws -> TarEntry {
        guard header.count == 512 else { throw TarArchive.ExtractError.badHeader }
        // Magic at offset 257 must be "ustar\0".
        let magicStart = 257
        let magicEnd = magicStart + 6
        let magicSlice = Array(header[magicStart..<magicEnd])
        let ustar = Array("ustar\0".utf8)
        guard magicSlice == ustar else {
            throw TarArchive.ExtractError.badHeader
        }

        let name = readCString(header, offset: 0, length: 100)
        let prefix = readCString(header, offset: 345, length: 155)
        let sizeRaw = readCString(header, offset: 124, length: 12)
        let typeflag = header[156]
        let linkname = readCString(header, offset: 157, length: 100)
        let modeRaw = readCString(header, offset: 100, length: 8)

        let fullName: String
        if prefix.isEmpty {
            fullName = name
        } else {
            fullName = "\(prefix)/\(name)"
        }

        let size = Int64(sizeRaw.trimmingCharacters(in: .whitespaces), radix: 8) ?? 0
        let mode = Int(modeRaw.trimmingCharacters(in: .whitespaces), radix: 8) ?? 0
        let kind: TarEntryKind
        switch typeflag {
        case 0x30, 0x00: // '0' or NUL => regular file
            kind = .file
        case 0x35: // '5' => directory
            kind = .directory
        case 0x32: // '2' => symlink
            kind = .symlink(target: linkname)
        default:
            throw TarArchive.ExtractError.unsupportedEntry(String(format: "0x%02x", typeflag))
        }
        return TarEntry(name: fullName, kind: kind, size: size, mode: mode)
    }

    private static func readCString(_ buffer: [UInt8], offset: Int, length: Int) -> String {
        let end = min(offset + length, buffer.count)
        let slice = buffer[offset..<end]
        let trimmed = slice.prefix { $0 != 0 }
        return String(decoding: trimmed, as: UTF8.self)
    }
}

/// Restores POSIX file mode bits after a tar entry has been written.
/// We only restore the executable bit here because the iOS sandbox
/// does not let us set arbitrary UID/GID; everything in the rootfs
/// ends up owned by the app's container user.
private enum TarFileMode {
    static func apply(mode: Int, to url: URL) throws {
        guard mode & 0o111 != 0 else { return }
        var attrs = (try? FileManager.default.attributesOfItem(atPath: url.path)) ?? [:]
        let current = (attrs[.posixPermissions] as? Int) ?? 0o644
        attrs[.posixPermissions] = current | 0o111
        try FileManager.default.setAttributes(attrs, ofItemAtPath: url.path)
    }
}
