// swift-tools-version: 6.0
import Foundation
import PackageDescription

let packageDir = URL(fileURLWithPath: #filePath).deletingLastPathComponent().path
let nativeLibDir = "\(packageDir)/artifacts/podish-native/osx-arm64"

let package = Package(
    name: "Podish",
    platforms: [
        .macOS(.v14)
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
                    "-L", nativeLibDir,
                    "\(nativeLibDir)/libbootstrapperdll.o"
                ]),
                .linkedLibrary("podishcore"),
                .linkedLibrary("Runtime.WorkstationGC"),
                .linkedLibrary("eventpipe-disabled"),
                .linkedLibrary("stdc++compat"),
                .linkedLibrary("System.Native"),
                .linkedLibrary("System.IO.Compression.Native"),
                .linkedLibrary("System.Net.Security.Native"),
                .linkedLibrary("System.Security.Cryptography.Native.Apple"),
                .linkedLibrary("System.Security.Cryptography.Native.OpenSsl"),
                .linkedLibrary("z"),
                .linkedLibrary("c++"),
                .linkedLibrary("resolv"),
                .linkedLibrary("iconv"),
                .linkedFramework("Foundation"),
                .linkedFramework("Security"),
                .linkedFramework("CoreFoundation"),
                .linkedFramework("CoreServices"),
                .linkedFramework("GSS")
            ]
        )
    ]
)
