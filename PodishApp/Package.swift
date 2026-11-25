// swift-tools-version: 6.0
import Foundation
import PackageDescription

let graphicsBackend = ProcessInfo.processInfo.environment["PODISH_GRAPHICS_BACKEND"] ?? "vulkan"
precondition(graphicsBackend == "vulkan" || graphicsBackend == "gles", "PODISH_GRAPHICS_BACKEND must be vulkan or gles")

var binaryTargets: [Target] = [
    .binaryTarget(
        name: "PodishCoreBinary",
        path: "artifacts/podish-native/\(graphicsBackend)/PodishCore.xcframework"
    )
]
var appDependencies: [Target.Dependency] = ["PodishCoreBinary"]
if graphicsBackend == "gles" {
    binaryTargets.append(.binaryTarget(name: "AngleEGL", path: "artifacts/angle/libEGL.xcframework"))
    binaryTargets.append(.binaryTarget(name: "AngleGLESv2", path: "artifacts/angle/libGLESv2.xcframework"))
    appDependencies += ["AngleEGL", "AngleGLESv2"]
}

let package = Package(
    name: "Podish",
    platforms: [
        .macOS(graphicsBackend == "vulkan" ? "26.0" : "14.0"),
        .iOS(graphicsBackend == "vulkan" ? "26.0" : "16.0")
    ],
    products: [
        .executable(name: "Podish", targets: ["Podish"])
    ],
    targets: binaryTargets + [
        .executableTarget(
            name: "Podish",
            dependencies: appDependencies,
            swiftSettings: graphicsBackend == "vulkan" ? [.define("PODISH_APPLE_VULKAN")] : [],
            linkerSettings: [
                .unsafeFlags([
                    "-Xlinker", "-u",
                    "-Xlinker", "___managed__Startup"
                ], .when(platforms: [.macOS, .iOS])),
                .linkedFramework("Foundation", .when(platforms: [.macOS, .iOS])),
                .linkedFramework("CoreFoundation", .when(platforms: [.macOS, .iOS])),
                .linkedFramework("CoreGraphics", .when(platforms: [.macOS, .iOS])),
                .linkedFramework("CoreText", .when(platforms: [.macOS, .iOS])),
                .linkedFramework("ImageIO", .when(platforms: [.macOS, .iOS])),
                .linkedFramework("Metal", .when(platforms: [.macOS, .iOS])),
                .linkedFramework("QuartzCore", .when(platforms: [.macOS, .iOS])),
                .linkedFramework("Security", .when(platforms: [.macOS, .iOS])),
                .linkedFramework("Network", .when(platforms: [.macOS, .iOS])),
                .linkedFramework("GSS", .when(platforms: [.macOS, .iOS])),
                .linkedLibrary("c++", .when(platforms: [.macOS, .iOS])),
                .linkedLibrary("sqlite3", .when(platforms: [.macOS, .iOS])),
                .linkedLibrary("z", .when(platforms: [.macOS, .iOS]))
            ]
        )
    ]
)
