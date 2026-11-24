// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "Podish",
    platforms: [
        .macOS(.v14),
        .iOS(.v16)
    ],
    products: [
        .executable(name: "Podish", targets: ["Podish"])
    ],
    targets: [
        .binaryTarget(
            name: "PodishCoreBinary",
            path: "artifacts/podish-native/PodishCore.xcframework"
        ),
        .binaryTarget(
            name: "AngleEGL",
            path: "artifacts/angle/libEGL.xcframework"
        ),
        .binaryTarget(
            name: "AngleGLESv2",
            path: "artifacts/angle/libGLESv2.xcframework"
        ),
        .executableTarget(
            name: "Podish",
            dependencies: [
                "PodishCoreBinary",
                "AngleEGL",
                "AngleGLESv2"
            ],
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
