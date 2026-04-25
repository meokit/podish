import SwiftUI

enum PodishSeedImageConfig {
    static let bundleDirectoryName = "PodishSeedImages"
    static let imageReferenceInfoKey = "PodishSeedImageReference"
    static let fallbackImageReference = "docker.io/i386/alpine:3.23"

    static var imageReference: String {
        let info = Bundle.main.infoDictionary ?? [:]
        let rawValue = info[imageReferenceInfoKey] as? String
        let trimmedValue = rawValue?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return trimmedValue.isEmpty ? fallbackImageReference : trimmedValue
    }

    static var safeDirectoryName: String {
        imageReference
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: ":", with: "_")
    }

    static func workDirectoryURL() -> URL {
        let fm = FileManager.default
        let base = (try? fm.url(
            for: .applicationSupportDirectory,
            in: .userDomainMask,
            appropriateFor: nil,
            create: true
        )) ?? URL(fileURLWithPath: fm.currentDirectoryPath)
        let dir = base.appendingPathComponent("Podish", isDirectory: true)
        try? fm.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }

    static func installedStoreURL() -> URL {
        workDirectoryURL()
            .appendingPathComponent(".fiberpod", isDirectory: true)
            .appendingPathComponent("oci", isDirectory: true)
            .appendingPathComponent("images", isDirectory: true)
            .appendingPathComponent(safeDirectoryName, isDirectory: true)
    }
}

private enum PodishRuntimeDiagnostics {
    static func logLinkedNativeArtifacts() {
        let info = Bundle.main.infoDictionary ?? [:]
        let sliceName = info["PodishCoreSliceName"] as? String ?? "unknown"
        let staticLibPath = info["PodishCoreStaticLibPath"] as? String ?? "missing"
        let bootstrapperPath = info["PodishCoreBootstrapperPath"] as? String ?? "missing"
        PodishLog.appInfo(
            "Native artifacts slice=\(sliceName) staticLib=\(staticLibPath) bootstrapper=\(bootstrapperPath)"
        )
    }

    static func logSeedImageConfiguration() {
        PodishLog.appInfo(
            "Seed image ref=\(PodishSeedImageConfig.imageReference) bundleDir=\(PodishSeedImageConfig.bundleDirectoryName) installDir=\(PodishSeedImageConfig.installedStoreURL().path)"
        )
    }
}

#if os(macOS)
import AppKit

final class PodishApplicationDelegate: NSObject, NSApplicationDelegate {
    var onBeforeTerminate: ((@escaping () -> Void) -> Void)?
    private var terminationInProgress = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        _ = NSApplication.shared.setActivationPolicy(.regular)
        NSApplication.shared.activate(ignoringOtherApps: true)
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard !terminationInProgress else { return .terminateLater }
        guard let onBeforeTerminate else { return .terminateNow }

        terminationInProgress = true
        onBeforeTerminate { [weak self] in
            Task { @MainActor in
                NSApplication.shared.reply(toApplicationShouldTerminate: true)
                self?.terminationInProgress = false
            }
        }
        return .terminateLater
    }
}
#endif

@main
struct PodishApp: App {
    #if os(macOS)
    @NSApplicationDelegateAdaptor(PodishApplicationDelegate.self) private var appDelegate
    #endif

    init() {
        PodishRuntimeDiagnostics.logLinkedNativeArtifacts()
        PodishRuntimeDiagnostics.logSeedImageConfiguration()
    }

    var body: some Scene {
        WindowGroup("Podish") {
            #if os(macOS)
            PodishRootView(onSessionReady: { session in
                appDelegate.onBeforeTerminate = { completion in
                    session.stopForAppTermination(completion: completion)
                }
            })
                .frame(minWidth: 1200, minHeight: 760)
            #else
            PodishRootView()
            #endif
        }
    }
}
