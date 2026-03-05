// swift-tools-version: 6.0
import Foundation
import PackageDescription

let packageDir = URL(fileURLWithPath: #filePath).deletingLastPathComponent().path
let macNativeLibDir = "\(packageDir)/artifacts/podish-native/osx-arm64"
let env = ProcessInfo.processInfo.environment
let platformName = env["PLATFORM_NAME"] ?? env["EFFECTIVE_PLATFORM_NAME"] ?? ""
let defaultIosRid = platformName.contains("simulator") ? "iossimulator-arm64" : "ios-arm64"
let iosRid = env["PODISH_IOS_RID"] ?? defaultIosRid
let iosNativeLibDir = "\(packageDir)/artifacts/podish-native/\(iosRid)"

let package = Package(
    name: "Podish",
    platforms: [
        .macOS(.v14),
        .iOS(.v16)
    ],
    products: [
        .executable(name: "Podish", targets: ["Podish"])
    ],
    dependencies: [
        .package(url: "https://github.com/migueldeicaza/SwiftTerm.git", from: "1.2.0")
    ],
    targets: [
        .executableTarget(
            name: "Podish",
            dependencies: [
                .product(name: "SwiftTerm", package: "SwiftTerm")
            ],
            linkerSettings: [
                .unsafeFlags([
                    "-F", macNativeLibDir
                ], .when(platforms: [.macOS])),
                .unsafeFlags([
                    "\(macNativeLibDir)/libbootstrapperdll.o"
                ], .when(platforms: [.macOS])),
                .unsafeFlags([
                    "-F", iosNativeLibDir
                ], .when(platforms: [.iOS])),
                .unsafeFlags([
                    "\(iosNativeLibDir)/libbootstrapperdll.o"
                ], .when(platforms: [.iOS])),
                .linkedFramework("PodishCore", .when(platforms: [.macOS, .iOS])),
                .linkedFramework("Foundation", .when(platforms: [.macOS, .iOS])),
                .linkedFramework("CoreFoundation", .when(platforms: [.macOS, .iOS])),
                .linkedFramework("Security", .when(platforms: [.macOS, .iOS])),
                .linkedFramework("Network", .when(platforms: [.macOS, .iOS])),
                .linkedFramework("GSS", .when(platforms: [.macOS, .iOS])),
                .linkedLibrary("c++", .when(platforms: [.macOS, .iOS])),
                .linkedLibrary("z", .when(platforms: [.macOS, .iOS])),
            ]
        )
    ]
)
