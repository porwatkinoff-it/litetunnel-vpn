// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "LiteKit",
    defaultLocalization: "ru",
    platforms: [.iOS(.v16), .macOS(.v13)],
    products: [
        .library(name: "LiteKit", targets: ["LiteKit"]),
    ],
    targets: [
        .target(name: "LiteKit"),
        .testTarget(name: "LiteKitTests", dependencies: ["LiteKit"]),
    ]
)
