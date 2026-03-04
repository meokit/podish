// swift-tools-version: 6.0
import PackageDescription

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
            ]
        )
    ]
)
