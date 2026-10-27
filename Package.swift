// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "omux",
    platforms: [.macOS(.v14)],
    dependencies: [
        .package(url: "https://github.com/migueldeicaza/SwiftTerm.git", from: "1.2.0"),
    ],
    targets: [
        .executableTarget(
            name: "omux",
            dependencies: [.product(name: "SwiftTerm", package: "SwiftTerm")],
            path: "Sources/omux"
        ),
    ]
)
