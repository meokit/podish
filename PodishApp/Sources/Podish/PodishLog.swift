import Foundation
import os

enum PodishLog {
    private static let subsystem = Bundle.main.bundleIdentifier ?? "dev.podish.app"
    private static let appLogger = Logger(subsystem: subsystem, category: "app")
    private static let coreLogger = Logger(subsystem: subsystem, category: "core")
    private static let uiLogger = Logger(subsystem: subsystem, category: "ui")

    static func appInfo(_ message: String) {
        appLogger.info("\(message, privacy: .public)")
        emitConsole("[APP] \(message)")
    }

    static func appError(_ message: String) {
        appLogger.error("\(message, privacy: .public)")
        emitConsole("[APP] \(message)", isError: true)
    }

    static func core(_ message: String) {
        coreLogger.log("\(message, privacy: .public)")
        emitConsole("[CORE] \(message)")
    }

    static func coreError(_ message: String) {
        coreLogger.error("\(message, privacy: .public)")
        emitConsole("[CORE] \(message)", isError: true)
    }

    static func ui(_ message: String) {
        uiLogger.log("[t+\(uptimeStamp(), privacy: .public)] \(message, privacy: .public)")
        emitConsole("[UI] [t+\(uptimeStamp())] \(message)")
    }

    private static func uptimeStamp() -> String {
        String(format: "%.3f", ProcessInfo.processInfo.systemUptime)
    }

    private static func emitConsole(_ message: String, isError: Bool = false) {
        let line = "[Podish] \(message)\n"
        if isError {
            FileHandle.standardError.write(Data(line.utf8))
        } else {
            FileHandle.standardOutput.write(Data(line.utf8))
        }
    }
}
