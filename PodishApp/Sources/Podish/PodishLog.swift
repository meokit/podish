import Foundation
import os

enum PodishLog {
    private static let subsystem = Bundle.main.bundleIdentifier ?? "dev.podish.app"
    private static let appLogger = Logger(subsystem: subsystem, category: "app")
    private static let coreLogger = Logger(subsystem: subsystem, category: "core")

    static func appInfo(_ message: String) {
        appLogger.info("\(message, privacy: .public)")
    }

    static func appError(_ message: String) {
        appLogger.error("\(message, privacy: .public)")
    }

    static func core(_ message: String) {
        coreLogger.log("\(message, privacy: .public)")
    }

    static func coreError(_ message: String) {
        coreLogger.error("\(message, privacy: .public)")
    }
}
