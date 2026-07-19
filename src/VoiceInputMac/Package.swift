// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "VoiceInputMac",
    platforms: [.macOS(.v15)],
    products: [
        .executable(name: "gujiguji-mac", targets: ["VoiceInputMac"]),
    ],
    targets: [
        .binaryTarget(
            name: "MicrosoftCognitiveServicesSpeech",
            url: "https://csspeechstorage.blob.core.windows.net/drop/1.50.0/MicrosoftCognitiveServicesSpeech-MacOSXCFramework-1.50.0.zip",
            checksum: "3b748dd2222c7ae06567878467bbc39b17a8dea015284a9a3117b0ea12a55a0b"
        ),
        .binaryTarget(
            name: "CTranscribe",
            url: "https://github.com/handy-computer/transcribe.cpp/releases/download/v0.1.3/TranscribeCpp.xcframework.zip",
            checksum: "b7a3442e2f3552cac1ee71b5e164934dd4db243f6b4b16b1e3e3ed5d1645eefd"
        ),
        .executableTarget(
            name: "VoiceInputMac",
            dependencies: ["MicrosoftCognitiveServicesSpeech", "CTranscribe"],
            linkerSettings: [
                .linkedLibrary("c++"),
                .linkedLibrary("z"),
                .linkedFramework("AppKit"),
                .linkedFramework("ApplicationServices"),
                .linkedFramework("Accelerate"),
                .linkedFramework("AVFoundation"),
                .linkedFramework("Metal"),
                .linkedFramework("MetalKit"),
                .linkedFramework("Security"),
                .linkedFramework("ServiceManagement"),
                .linkedFramework("Speech"),
                .linkedFramework("UserNotifications"),
            ]
        ),
        .testTarget(name: "VoiceInputMacTests", dependencies: ["VoiceInputMac"]),
    ],
    swiftLanguageModes: [.v5]
)
