// swift-tools-version: 6.0
import Foundation
import PackageDescription

let packageDir = URL(fileURLWithPath: #filePath).deletingLastPathComponent().path
let macNativeLibDir = "\(packageDir)/artifacts/podish-native/osx-arm64"
let env = ProcessInfo.processInfo.environment
let platformName = env["PLATFORM_NAME"] ?? env["EFFECTIVE_PLATFORM_NAME"] ?? ""
let sdkName = env["SDK_NAME"] ?? ""
let sdkRoot = env["SDKROOT"] ?? ""
let isSimulatorBuild = platformName.contains("simulator")
    || sdkName.contains("simulator")
    || sdkRoot.contains("iPhoneSimulator")
let isDeviceBuild = platformName.contains("iphoneos")
    || sdkName.contains("iphoneos")
    || sdkRoot.contains("iPhoneOS")
// Prefer simulator when env is ambiguous.
let defaultIosRid = isDeviceBuild && !isSimulatorBuild ? "ios-arm64" : "iossimulator-arm64"
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
        .package(path: "Vendor/SwiftTerm")
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
                    "-F", iosNativeLibDir
                ], .when(platforms: [.iOS])),
                .unsafeFlags([
                    "-Xlinker", "-u",
                    "-Xlinker", "___managed__Startup"
                ], .when(platforms: [.macOS, .iOS])),
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
