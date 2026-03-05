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
    dependencies: [
        .package(path: "Vendor/SwiftTerm")
    ],
    targets: [
        .binaryTarget(
            name: "PodishCoreBinary",
            path: "artifacts/podish-native/PodishCore.xcframework"
        ),
        .executableTarget(
            name: "Podish",
            dependencies: [
                "PodishCoreBinary",
                .product(name: "SwiftTerm", package: "SwiftTerm")
            ],
            linkerSettings: [
                .unsafeFlags([
                    "-Xlinker", "-u",
                    "-Xlinker", "___managed__Startup"
                ], .when(platforms: [.macOS, .iOS])),
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
