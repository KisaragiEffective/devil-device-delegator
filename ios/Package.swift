// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "DDDProbes",
    platforms: [.iOS(.v18), .macOS(.v14)],
    products: [.library(name: "DDDProbes", targets: ["DDDProbes"])],
    dependencies: [
        .package(url: "https://github.com/norsehorse-dev/AgePonyiOS.git",
                 revision: "33731a8d875493a466fc0668d8d9e9aec55f0005")
    ],
    targets: [
        .target(name: "DDDProbes", dependencies: [
            .product(name: "AgePonyCore", package: "AgePonyiOS")
        ]),
        .testTarget(name: "DDDProbesTests", dependencies: [
                    "DDDProbes",
                    .product(name: "AgePonyCore", package: "AgePonyiOS")
                ])
    ]
)
