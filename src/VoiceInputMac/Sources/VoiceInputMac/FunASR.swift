import CryptoKit
import Darwin
import Foundation

struct FunAsrArtifact: Codable, Equatable, Sendable {
    let relativePath: String
    let url: URL
    let size: Int64
    let sha256: String
}

enum FunAsrRunner: String, Codable, Sendable {
    case senseVoice
    case paraformer
    case nano
    case qwen3Asr

    var executable: String? {
        switch self {
        case .senseVoice: "llama-funasr-sensevoice"
        case .paraformer: "llama-funasr-paraformer"
        case .nano: "llama-funasr-cli"
        case .qwen3Asr: nil
        }
    }
}

struct FunAsrModel: Codable, Equatable, Identifiable, Sendable {
    let id: String
    let displayName: String
    let description: String
    let runner: FunAsrRunner
    let languages: Set<String>
    let artifacts: [FunAsrArtifact]
    let source: URL
    let license: URL

    var downloadSize: Int64 { artifacts.reduce(0) { $0 + $1.size } }
    func supports(_ language: String) -> Bool {
        languages.contains { $0.caseInsensitiveCompare(language) == .orderedSame }
    }
}

enum FunAsrCatalog {
    static let defaultId = "sensevoice-small-q8"
    static let qwen3AsrId = "qwen3-asr-0.6b-q8"
    static let runtimeVersion = "v0.1.4"

    static let runtime = FunAsrArtifact(
        relativePath: "downloads/runtime-v0.1.4-macos-arm64.tar.gz",
        url: URL(string: "https://github.com/modelscope/FunASR/releases/download/runtime-llamacpp-v0.1.4/funasr-llamacpp-macos-arm64.tar.gz")!,
        size: 6_816_662,
        sha256: "010416baa6932c7ce67fda50eb421a65e8ae6fd248f06f8d2f7ec17d15ef2cba"
    )

    static let vad = FunAsrArtifact(
        relativePath: "shared/fsmn-vad.gguf",
        url: URL(string: "https://huggingface.co/FunAudioLLM/fsmn-vad-GGUF/resolve/6840bae4c5c92ee8c04faaf4db23dd0105098d7f/fsmn-vad.gguf")!,
        size: 1_720_512,
        sha256: "1270f2559c495f4e7b6e739541151027d360761a3fda43fc147034f5719f5479"
    )

    private static let apache = URL(string: "https://www.apache.org/licenses/LICENSE-2.0")!

    static let models: [FunAsrModel] = [
        FunAsrModel(
            id: defaultId,
            displayName: "SenseVoiceSmall",
            description: "Balanced local recognition for Chinese, English, Japanese, and Korean.",
            runner: .senseVoice,
            languages: ["en-US", "zh-CN", "zh-TW", "ja-JP", "ko-KR"],
            artifacts: [FunAsrArtifact(
                relativePath: "models/sensevoice-small-q8/sensevoice-small-q8.gguf",
                url: URL(string: "https://huggingface.co/FunAudioLLM/SenseVoiceSmall-GGUF/resolve/90c1c61912018b70ada0fcc024ea24aca62f2e63/sensevoice-small-q8.gguf")!,
                size: 254_208_320,
                sha256: "4ae45c94422de949b387e2e0fb10d7e14e4c42c69db30c3444ecc7d4b844b7c5"
            )],
            source: URL(string: "https://huggingface.co/FunAudioLLM/SenseVoiceSmall-GGUF")!,
            license: apache
        ),
        FunAsrModel(
            id: "paraformer-zh-q8",
            displayName: "Paraformer Chinese",
            description: "Fast Chinese and English recognition.",
            runner: .paraformer,
            languages: ["en-US", "zh-CN", "zh-TW"],
            artifacts: [FunAsrArtifact(
                relativePath: "models/paraformer-zh-q8/paraformer-q8.gguf",
                url: URL(string: "https://huggingface.co/FunAudioLLM/Paraformer-GGUF/resolve/de2cbaaa0f30b34f398d7a066fdfefb8e50d902c/paraformer-q8.gguf")!,
                size: 236_929_024,
                sha256: "42bf76ea1575a336aaca4c1b7c01a82b79113e6d04d0d6b799561bfcf07ee011"
            )],
            source: URL(string: "https://huggingface.co/FunAudioLLM/Paraformer-GGUF")!,
            license: apache
        ),
        FunAsrModel(
            id: "fun-asr-nano-q4",
            displayName: "Fun-ASR Nano",
            description: "Higher-quality recognition for difficult vocabulary and accents.",
            runner: .nano,
            languages: ["en-US", "zh-CN", "zh-TW", "ja-JP"],
            artifacts: [
                FunAsrArtifact(
                    relativePath: "models/fun-asr-nano-q4/funasr-encoder-f16.gguf",
                    url: URL(string: "https://huggingface.co/FunAudioLLM/Fun-ASR-Nano-GGUF/resolve/c1629cbf83548ea0d92077c09d3541ce407ee643/funasr-encoder-f16.gguf")!,
                    size: 469_331_008,
                    sha256: "f92f91d01a24fbed6c863495b2ee8c6a6788144a02858b75743f0946668de8a2"
                ),
                FunAsrArtifact(
                    relativePath: "models/fun-asr-nano-q4/qwen3-0.6b-q4km.gguf",
                    url: URL(string: "https://huggingface.co/FunAudioLLM/Fun-ASR-Nano-GGUF/resolve/c1629cbf83548ea0d92077c09d3541ce407ee643/qwen3-0.6b-q4km.gguf")!,
                    size: 484_219_776,
                    sha256: "cc5057552aa9dddedcda73ea8889854e8a257eb07d0a561b7234465c1e856f22"
                ),
            ],
            source: URL(string: "https://huggingface.co/FunAudioLLM/Fun-ASR-Nano-GGUF")!,
            license: apache
        ),
        FunAsrModel(
            id: qwen3AsrId,
            displayName: "Qwen3-ASR 0.6B",
            description: "High-accuracy multilingual recognition with Metal acceleration and automatic language detection.",
            runner: .qwen3Asr,
            languages: ["en-US", "zh-CN", "zh-TW", "ja-JP", "ko-KR", "vi-VN"],
            artifacts: [FunAsrArtifact(
                relativePath: "models/qwen3-asr-0.6b-q8/Qwen3-ASR-0.6B-Q8_0.gguf",
                url: URL(string: "https://huggingface.co/handy-computer/Qwen3-ASR-0.6B-gguf/resolve/e4e16599b900eb0cb36e524514756bb92eb092b7/Qwen3-ASR-0.6B-Q8_0.gguf")!,
                size: 850_423_456,
                sha256: "f081b2d5e23bd669d92cc331d722a8a0681943b8e6f34b48996fd5c319b5acd8"
            )],
            source: URL(string: "https://huggingface.co/Qwen/Qwen3-ASR-0.6B")!,
            license: apache
        ),
    ]

    static func model(_ id: String) throws -> FunAsrModel {
        guard let model = models.first(where: { $0.id == id }) else {
            throw FunAsrError.unknownModel(id)
        }
        return model
    }

    static func normalizedId(_ id: String?) -> String {
        guard let id, models.contains(where: { $0.id == id }) else { return defaultId }
        return id
    }
}

enum FunAsrInstallStage: String, Codable, Sendable {
    case notInstalled, downloading, verifying, testing, installed, failed
}

struct FunAsrInstallProgress: Equatable, Sendable {
    let modelId: String
    let stage: FunAsrInstallStage
    let artifact: String
    let downloadedBytes: Int64
    let totalBytes: Int64?
    let error: String?
}

struct FunAsrResolvedModel: Sendable {
    let model: FunAsrModel
    let executable: URL
    let vad: URL
    let artifacts: [String: URL]
}

enum FunAsrError: Error, LocalizedError {
    case unknownModel(String)
    case unsupportedArchitecture
    case installationBusy
    case notInstalled(String)
    case insufficientDiskSpace
    case invalidResponse(String)
    case invalidHash(String)
    case unsafeArchive(String)
    case incompleteRuntime
    case processTimedOut
    case processFailed(Int32, String)

    var errorDescription: String? {
        switch self {
        case .unknownModel(let id): "Unknown FunASR model '\(id)'."
        case .unsupportedArchitecture: "The packaged FunASR runtime currently supports Apple Silicon only."
        case .installationBusy: "Wait for the active FunASR installation to finish or cancel it first."
        case .notInstalled(let id): "FunASR model '\(id)' is not installed."
        case .insufficientDiskSpace: "There is not enough disk space for the FunASR package."
        case .invalidResponse(let detail): "The FunASR download response is invalid: \(detail)"
        case .invalidHash(let name): "SHA-256 verification failed for \(name)."
        case .unsafeArchive(let entry): "The FunASR runtime archive contains an unsafe entry: \(entry)"
        case .incompleteRuntime: "The FunASR runtime archive is missing required executables."
        case .processTimedOut: "FunASR transcription timed out."
        case .processFailed(let status, let detail):
            "FunASR exited with code \(status).\(detail.isEmpty ? "" : " \(detail)")"
        }
    }
}

final class FunAsrRuntimeManager: @unchecked Sendable {
    private static let stagingMargin: Int64 = 64 * 1_024 * 1_024
    private static let requiredExecutables = [
        "llama-funasr-sensevoice",
        "llama-funasr-paraformer",
        "llama-funasr-cli",
    ]

    var onProgress: ((FunAsrInstallProgress) -> Void)?

    private let root: URL
    private let session: URLSession
    private let files = FileManager.default
    private let lock = NSLock()
    private var installing = false
    private var progressByModel: [String: FunAsrInstallProgress] = [:]
    private var verified: [String: VerifiedFile] = [:]

    init(root: URL = AppPaths.funAsr, session: URLSession = .shared) {
        self.root = root.standardizedFileURL
        self.session = session
    }

    func state(for modelId: String) -> FunAsrInstallStage {
        lock.lock()
        let active = progressByModel[modelId]
        lock.unlock()
        if let active, active.stage != .installed { return active.stage }
        return hasInstalledFiles(modelId) ? .installed : .notInstalled
    }

    func runtimeState(verifyHashes: Bool = false) -> FunAsrInstallStage {
        isRuntimeInstalled(verifyHashes: verifyHashes) ? .installed : .notInstalled
    }

    func hasInstalledFiles(_ modelId: String) -> Bool {
        isInstalled(modelId, verifyHashes: false)
    }

    func isRuntimeInstalled(verifyHashes: Bool = true) -> Bool {
        guard let manifest = loadManifest(),
              manifest.runtimeVersion == FunAsrCatalog.runtimeVersion,
              manifest.runtimeSha256 == FunAsrCatalog.runtime.sha256 else { return false }
        return runtimeFilesMatch(manifest.runtimeFiles, verifyHashes: verifyHashes)
    }

    func isInstalled(_ modelId: String, verifyHashes: Bool = true) -> Bool {
        guard let model = try? FunAsrCatalog.model(modelId),
              let manifest = loadManifest(),
              manifest.runtimeVersion == FunAsrCatalog.runtimeVersion,
              manifest.runtimeSha256 == FunAsrCatalog.runtime.sha256,
              manifest.vad == InstalledArtifact(FunAsrCatalog.vad),
              manifest.models[model.id] == model.artifacts.map(InstalledArtifact.init),
              runtimeFilesMatch(manifest.runtimeFiles, verifyHashes: verifyHashes),
              fileMatches(FunAsrCatalog.vad, verifyHash: verifyHashes),
              model.artifacts.allSatisfy({ fileMatches($0, verifyHash: verifyHashes) }) else {
            return false
        }
        return true
    }

    func install(_ modelId: String) async throws {
        let model = try FunAsrCatalog.model(modelId)
        guard model.runner != .qwen3Asr else { throw FunAsrError.unknownModel(modelId) }
#if !arch(arm64)
        throw FunAsrError.unsupportedArchitecture
#endif
        try beginOperation()
        defer { endOperation() }

        let total = FunAsrCatalog.runtime.size + FunAsrCatalog.vad.size + model.downloadSize
        if isInstalled(model.id) {
            report(model.id, .installed, downloaded: total, total: total)
            return
        }

        do {
            var completed: Int64 = 0
            try await installRuntime(for: model.id, completed: completed, total: total)
            completed += FunAsrCatalog.runtime.size
            try await download(FunAsrCatalog.vad, modelId: model.id, completed: completed, total: total)
            completed += FunAsrCatalog.vad.size
            for artifact in model.artifacts {
                try await download(artifact, modelId: model.id, completed: completed, total: total)
                completed += artifact.size
            }

            report(model.id, .testing, artifact: model.displayName, downloaded: total, total: total)
            let resolved = try resolveFiles(model)
            try await smokeTest(resolved)
            try markInstalled(model)
            report(model.id, .installed, artifact: model.displayName, downloaded: total, total: total)
        } catch is CancellationError {
            report(model.id, .notInstalled)
            throw CancellationError()
        } catch {
            report(model.id, .failed, error: error.localizedDescription)
            throw error
        }
    }

    func resolve(_ modelId: String) throws -> FunAsrResolvedModel {
        guard try FunAsrCatalog.model(modelId).runner != .qwen3Asr else {
            throw FunAsrError.unknownModel(modelId)
        }
        guard isInstalled(modelId, verifyHashes: true) else { throw FunAsrError.notInstalled(modelId) }
        return try resolveFiles(FunAsrCatalog.model(modelId))
    }

    func remove(_ modelId: String) throws {
        let model = try FunAsrCatalog.model(modelId)
        guard model.runner != .qwen3Asr else { throw FunAsrError.unknownModel(modelId) }
        try beginOperation()
        defer { endOperation() }
        for artifact in model.artifacts {
            let url = try managedURL(artifact.relativePath)
            if files.fileExists(atPath: url.path) { try files.removeItem(at: url) }
            let part = try managedURL("\(artifact.relativePath).part")
            if files.fileExists(atPath: part.path) { try files.removeItem(at: part) }
        }
        var manifest = loadManifest() ?? InstallationManifest()
        manifest.models.removeValue(forKey: model.id)
        try saveManifest(manifest)
        lock.lock()
        progressByModel.removeValue(forKey: model.id)
        lock.unlock()
    }

    private func beginOperation() throws {
        lock.lock()
        defer { lock.unlock() }
        guard !installing else { throw FunAsrError.installationBusy }
        installing = true
    }

    private func endOperation() {
        lock.lock()
        installing = false
        lock.unlock()
    }

    private func installRuntime(for modelId: String, completed: Int64, total: Int64) async throws {
        let destination = try managedURL("runtime/\(FunAsrCatalog.runtimeVersion)")
        if let manifest = loadManifest(),
           manifest.runtimeVersion == FunAsrCatalog.runtimeVersion,
           manifest.runtimeSha256 == FunAsrCatalog.runtime.sha256,
           runtimeFilesMatch(manifest.runtimeFiles, verifyHashes: true) {
            return
        }

        try await download(FunAsrCatalog.runtime, modelId: modelId, completed: completed, total: total)
        let archive = try managedURL(FunAsrCatalog.runtime.relativePath)
        let runtimeRoot = try managedURL("runtime")
        try files.createDirectory(at: runtimeRoot, withIntermediateDirectories: true)
        let staging = runtimeRoot.appendingPathComponent(".staging-\(UUID().uuidString)", isDirectory: true)
        try files.createDirectory(at: staging, withIntermediateDirectories: true)
        defer { try? files.removeItem(at: staging) }

        try await extractTarSafely(archive, to: staging)
        guard let payload = runtimePayload(in: staging) else { throw FunAsrError.incompleteRuntime }
        let backup = runtimeRoot.appendingPathComponent(".backup-\(UUID().uuidString)", isDirectory: true)
        var movedOld = false
        do {
            if files.fileExists(atPath: destination.path) {
                try files.moveItem(at: destination, to: backup)
                movedOld = true
            }
            try files.moveItem(at: payload, to: destination)
            if movedOld { try files.removeItem(at: backup) }
        } catch {
            if !files.fileExists(atPath: destination.path), movedOld,
               files.fileExists(atPath: backup.path) {
                try? files.moveItem(at: backup, to: destination)
            }
            throw error
        }
    }

    private func download(
        _ artifact: FunAsrArtifact,
        modelId: String,
        completed: Int64,
        total: Int64
    ) async throws {
        let final = try managedURL(artifact.relativePath)
        let part = try managedURL("\(artifact.relativePath).part")
        try files.createDirectory(at: final.deletingLastPathComponent(), withIntermediateDirectories: true)
        if fileMatches(artifact, verifyHash: true) { return }

        var existing = fileSize(part) ?? 0
        if existing > artifact.size {
            try files.removeItem(at: part)
            existing = 0
        }
        if existing == artifact.size {
            try activateVerifiedPart(part, as: final, artifact: artifact, modelId: modelId,
                                     completed: completed, total: total)
            return
        }
        try checkDiskSpace(bytes: artifact.size - existing)

        var request = URLRequest(url: artifact.url)
        if existing > 0 { request.setValue("bytes=\(existing)-", forHTTPHeaderField: "Range") }
        report(modelId, .downloading, artifact: final.lastPathComponent,
               downloaded: completed + existing, total: total)
        let transfer = PersistentDownload(
            configuration: session.configuration,
            request: request,
            destination: part,
            existingBytes: existing,
            pinnedSize: artifact.size
        ) { [weak self] downloaded in
            self?.report(modelId, .downloading, artifact: final.lastPathComponent,
                         downloaded: completed + downloaded, total: total)
        }
        let downloaded = try await transfer.run()
        try Task.checkCancellation()
        guard downloaded == artifact.size else {
            throw FunAsrError.invalidResponse("download ended before the pinned size")
        }
        try activateVerifiedPart(part, as: final, artifact: artifact, modelId: modelId,
                                 completed: completed, total: total)
    }

    private func activateVerifiedPart(
        _ part: URL,
        as final: URL,
        artifact: FunAsrArtifact,
        modelId: String,
        completed: Int64,
        total: Int64
    ) throws {
        report(modelId, .verifying, artifact: final.lastPathComponent,
               downloaded: completed + artifact.size, total: total)
        guard try Self.sha256(part) == artifact.sha256 else {
            try? files.removeItem(at: part)
            throw FunAsrError.invalidHash(final.lastPathComponent)
        }
        if files.fileExists(atPath: final.path) {
            _ = try files.replaceItemAt(final, withItemAt: part)
        } else {
            try files.moveItem(at: part, to: final)
        }
        cacheVerified(final, sha256: artifact.sha256)
    }

    private func extractTarSafely(_ archive: URL, to destination: URL) async throws {
        let listing = try await NativeProcess.run(
            executable: URL(fileURLWithPath: "/usr/bin/tar"),
            arguments: ["-tzf", archive.path]
        )
        guard listing.status == 0 else {
            throw FunAsrError.invalidResponse(String(decoding: listing.stderr, as: UTF8.self))
        }
        let entries = String(decoding: listing.stdout, as: UTF8.self)
            .split(separator: "\n", omittingEmptySubsequences: false)
            .map(String.init)
            .filter { !$0.isEmpty }
        guard !entries.isEmpty else { throw FunAsrError.incompleteRuntime }
        for entry in entries where !Self.isSafeArchivePath(entry) {
            throw FunAsrError.unsafeArchive(entry)
        }

        let verbose = try await NativeProcess.run(
            executable: URL(fileURLWithPath: "/usr/bin/tar"),
            arguments: ["-tvzf", archive.path]
        )
        let verboseLines = String(decoding: verbose.stdout, as: UTF8.self)
            .split(separator: "\n").map(String.init)
        guard verbose.status == 0, verboseLines.count == entries.count else {
            throw FunAsrError.unsafeArchive("malformed listing")
        }
        for line in verboseLines {
            guard let type = line.first, type == "-" || type == "d" else {
                throw FunAsrError.unsafeArchive("links and special files are not allowed")
            }
        }

        let result = try await NativeProcess.run(
            executable: URL(fileURLWithPath: "/usr/bin/tar"),
            arguments: ["-xzf", archive.path, "-C", destination.path, "--no-same-owner", "--no-same-permissions"]
        )
        guard result.status == 0 else {
            throw FunAsrError.invalidResponse(String(decoding: result.stderr, as: UTF8.self))
        }
        try verifyExtractedTree(destination)
    }

    static func isSafeArchivePath(_ path: String) -> Bool {
        guard !path.isEmpty, !path.hasPrefix("/"), !path.contains("\0") else { return false }
        let components = path.replacingOccurrences(of: "\\", with: "/")
            .split(separator: "/", omittingEmptySubsequences: true)
        return !components.isEmpty && !components.contains("..")
    }

    private func verifyExtractedTree(_ root: URL) throws {
        let rootPath = root.standardizedFileURL.path + "/"
        guard let enumerator = files.enumerator(
            at: root,
            includingPropertiesForKeys: [.isSymbolicLinkKey],
            options: [.skipsHiddenFiles]
        ) else { throw FunAsrError.incompleteRuntime }
        for case let item as URL in enumerator {
            let values = try item.resourceValues(forKeys: [.isSymbolicLinkKey])
            guard values.isSymbolicLink != true,
                  item.standardizedFileURL.path.hasPrefix(rootPath) else {
                throw FunAsrError.unsafeArchive(item.lastPathComponent)
            }
        }
    }

    private func runtimePayload(in staging: URL) -> URL? {
        if runtimeComplete(staging) { return staging }
        guard let enumerator = files.enumerator(
            at: staging,
            includingPropertiesForKeys: [.isDirectoryKey],
            options: [.skipsHiddenFiles]
        ) else { return nil }
        for case let item as URL in enumerator {
            if (try? item.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true,
               runtimeComplete(item) { return item }
        }
        return nil
    }

    private func runtimeComplete(_ directory: URL) -> Bool {
        Self.requiredExecutables.allSatisfy {
            files.isExecutableFile(atPath: directory.appendingPathComponent($0).path)
        }
    }

    private func resolveFiles(_ model: FunAsrModel) throws -> FunAsrResolvedModel {
        let runtime = try managedURL("runtime/\(FunAsrCatalog.runtimeVersion)")
        guard let executableName = model.runner.executable else { throw FunAsrError.notInstalled(model.id) }
        let executable = runtime.appendingPathComponent(executableName)
        let vad = try managedURL(FunAsrCatalog.vad.relativePath)
        let artifacts = try Dictionary(uniqueKeysWithValues: model.artifacts.map {
            ($0.relativePath, try managedURL($0.relativePath))
        })
        guard files.isExecutableFile(atPath: executable.path), files.fileExists(atPath: vad.path),
              artifacts.values.allSatisfy({ files.fileExists(atPath: $0.path) }) else {
            throw FunAsrError.notInstalled(model.id)
        }
        return FunAsrResolvedModel(model: model, executable: executable, vad: vad, artifacts: artifacts)
    }

    private func smokeTest(_ resolved: FunAsrResolvedModel) async throws {
        let directory = files.temporaryDirectory.appendingPathComponent("gujiguji", isDirectory: true)
        try files.createDirectory(at: directory, withIntermediateDirectories: true)
        let wave = directory.appendingPathComponent("funasr-smoke-\(UUID().uuidString).wav")
        defer { try? files.removeItem(at: wave) }
        try PcmWave.wrap(Data(count: 32_000)).write(to: wave, options: .atomic)
        let result = try await NativeProcess.run(
            executable: resolved.executable,
            arguments: FunAsrEngine.arguments(for: resolved, wave: wave),
            currentDirectory: resolved.executable.deletingLastPathComponent(),
            timeout: 60
        )
        guard result.status == 0 else {
            throw FunAsrError.processFailed(
                result.status,
                String(decoding: result.stderr.prefix(500), as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
            )
        }
    }

    private func markInstalled(_ model: FunAsrModel) throws {
        var manifest = loadManifest() ?? InstallationManifest()
        manifest.runtimeVersion = FunAsrCatalog.runtimeVersion
        manifest.runtimeSha256 = FunAsrCatalog.runtime.sha256
        manifest.vad = InstalledArtifact(FunAsrCatalog.vad)
        manifest.models[model.id] = model.artifacts.map(InstalledArtifact.init)
        let runtime = try managedURL("runtime/\(FunAsrCatalog.runtimeVersion)")
        manifest.runtimeFiles = try Self.requiredExecutables.map { name in
            let url = runtime.appendingPathComponent(name)
            return InstalledArtifact(relativePath: "runtime/\(FunAsrCatalog.runtimeVersion)/\(name)",
                                     size: fileSize(url) ?? 0, sha256: try Self.sha256(url))
        }
        try saveManifest(manifest)
    }

    private func runtimeFilesMatch(_ installed: [InstalledArtifact], verifyHashes: Bool) -> Bool {
        guard installed.count == Self.requiredExecutables.count else { return false }
        for executable in Self.requiredExecutables {
            let relative = "runtime/\(FunAsrCatalog.runtimeVersion)/\(executable)"
            guard let item = installed.first(where: { $0.relativePath == relative }),
                  let url = try? managedURL(relative), fileSize(url) == item.size,
                  files.isExecutableFile(atPath: url.path) else { return false }
            if verifyHashes && cachedHash(url) != item.sha256 { return false }
        }
        return true
    }

    private func fileMatches(_ artifact: FunAsrArtifact, verifyHash: Bool) -> Bool {
        guard let url = try? managedURL(artifact.relativePath), fileSize(url) == artifact.size else { return false }
        return !verifyHash || cachedHash(url) == artifact.sha256
    }

    private func cachedHash(_ url: URL) -> String? {
        guard let attributes = try? files.attributesOfItem(atPath: url.path),
              let size = (attributes[.size] as? NSNumber)?.int64Value,
              let modified = attributes[.modificationDate] as? Date else { return nil }
        lock.lock()
        let cached = verified[url.path]
        lock.unlock()
        if cached?.size == size, cached?.modified == modified { return cached?.sha256 }
        guard let hash = try? Self.sha256(url) else { return nil }
        lock.lock()
        verified[url.path] = VerifiedFile(size: size, modified: modified, sha256: hash)
        lock.unlock()
        return hash
    }

    private func cacheVerified(_ url: URL, sha256: String) {
        guard let attributes = try? files.attributesOfItem(atPath: url.path),
              let size = (attributes[.size] as? NSNumber)?.int64Value,
              let modified = attributes[.modificationDate] as? Date else { return }
        lock.lock()
        verified[url.path] = VerifiedFile(size: size, modified: modified, sha256: sha256)
        lock.unlock()
    }

    private func managedURL(_ relativePath: String) throws -> URL {
        guard Self.isSafeArchivePath(relativePath) else { throw FunAsrError.unsafeArchive(relativePath) }
        let candidate = root.appendingPathComponent(relativePath).standardizedFileURL
        let prefix = root.path.hasSuffix("/") ? root.path : root.path + "/"
        guard candidate.path.hasPrefix(prefix) else { throw FunAsrError.unsafeArchive(relativePath) }
        var current = root
        if isSymbolicLink(current) { throw FunAsrError.unsafeArchive(root.path) }
        for component in relativePath.replacingOccurrences(of: "\\", with: "/").split(separator: "/") {
            current.appendPathComponent(String(component))
            if isSymbolicLink(current) { throw FunAsrError.unsafeArchive(relativePath) }
        }
        return candidate
    }

    private func isSymbolicLink(_ url: URL) -> Bool {
        var information = stat()
        return url.withUnsafeFileSystemRepresentation { path in
            guard let path else { return false }
            return Darwin.lstat(path, &information) == 0
                && information.st_mode & S_IFMT == S_IFLNK
        }
    }

    private func checkDiskSpace(bytes: Int64) throws {
        let values = try? root.deletingLastPathComponent().resourceValues(
            forKeys: [.volumeAvailableCapacityForImportantUsageKey])
        if let available = values?.volumeAvailableCapacityForImportantUsage,
           available < bytes + Self.stagingMargin { throw FunAsrError.insufficientDiskSpace }
    }

    private func fileSize(_ url: URL) -> Int64? {
        (try? files.attributesOfItem(atPath: url.path)[.size] as? NSNumber)?.int64Value
    }

    static func sha256(_ url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hash = SHA256()
        while let data = try handle.read(upToCount: 1_048_576), !data.isEmpty {
            try Task.checkCancellation()
            hash.update(data: data)
        }
        return hash.finalize().map { String(format: "%02x", $0) }.joined()
    }

    private func loadManifest() -> InstallationManifest? {
        guard let url = try? managedURL("installation.json"), let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode(InstallationManifest.self, from: data)
    }

    private func saveManifest(_ manifest: InstallationManifest) throws {
        try files.createDirectory(at: root, withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(manifest).write(to: managedURL("installation.json"), options: .atomic)
    }

    private func report(
        _ modelId: String,
        _ stage: FunAsrInstallStage,
        artifact: String = "",
        downloaded: Int64 = 0,
        total: Int64? = nil,
        error: String? = nil
    ) {
        let progress = FunAsrInstallProgress(modelId: modelId, stage: stage, artifact: artifact,
                                             downloadedBytes: downloaded, totalBytes: total, error: error)
        lock.lock()
        progressByModel[modelId] = progress
        let callback = onProgress
        lock.unlock()
        callback?(progress)
    }
}

private struct InstalledArtifact: Codable, Equatable, Sendable {
    let relativePath: String
    let size: Int64
    let sha256: String

    init(relativePath: String, size: Int64, sha256: String) {
        self.relativePath = relativePath
        self.size = size
        self.sha256 = sha256
    }

    init(_ artifact: FunAsrArtifact) {
        self.init(relativePath: artifact.relativePath, size: artifact.size, sha256: artifact.sha256)
    }
}

private struct InstallationManifest: Codable, Sendable {
    var runtimeVersion = ""
    var runtimeSha256 = ""
    var runtimeFiles: [InstalledArtifact] = []
    var vad: InstalledArtifact?
    var models: [String: [InstalledArtifact]] = [:]
}

private struct VerifiedFile {
    let size: Int64
    let modified: Date
    let sha256: String
}

private struct NativeProcessResult: Sendable {
    let status: Int32
    let stdout: Data
    let stderr: Data
}

final class PersistentDownload: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private static let progressStride: Int64 = 1_048_576

    private let configuration: URLSessionConfiguration
    private let request: URLRequest
    private let destination: URL
    private let existingBytes: Int64
    private let pinnedSize: Int64
    private let progress: (Int64) -> Void
    private let files = FileManager.default
    private let lock = NSLock()

    private var continuation: CheckedContinuation<Int64, Error>?
    private var activeSession: URLSession?
    private var activeTask: URLSessionDataTask?
    private var cancellationRequested = false
    private var finished = false

    // Accessed only by the serial URLSession delegate queue.
    private var output: FileHandle?
    private var receivedBytes: Int64
    private var lastReportedBytes: Int64
    private var acceptedResponse = false
    private var failure: Error?
    private var discardPartial = false

    init(
        configuration: URLSessionConfiguration,
        request: URLRequest,
        destination: URL,
        existingBytes: Int64,
        pinnedSize: Int64,
        progress: @escaping (Int64) -> Void
    ) {
        self.configuration = configuration
        self.request = request
        self.destination = destination
        self.existingBytes = existingBytes
        self.pinnedSize = pinnedSize
        self.progress = progress
        receivedBytes = existingBytes
        lastReportedBytes = existingBytes
    }

    func run() async throws -> Int64 {
        try await withTaskCancellationHandler {
            try Task.checkCancellation()
            return try await withCheckedThrowingContinuation { continuation in
                let queue = OperationQueue()
                queue.maxConcurrentOperationCount = 1
                queue.qualityOfService = .utility
                let downloadSession = URLSession(
                    configuration: configuration,
                    delegate: self,
                    delegateQueue: queue)
                let task = downloadSession.dataTask(with: request)
                let shouldStart = lock.withLock {
                    guard !cancellationRequested else { return false }
                    self.continuation = continuation
                    activeSession = downloadSession
                    activeTask = task
                    return true
                }
                if shouldStart {
                    task.resume()
                } else {
                    downloadSession.invalidateAndCancel()
                    continuation.resume(throwing: CancellationError())
                }
            }
        } onCancel: {
            self.cancel()
        }
    }

    func cancel() {
        let task = lock.withLock {
            cancellationRequested = true
            return activeTask
        }
        task?.cancel()
    }

    func urlSession(
        _ session: URLSession,
        dataTask: URLSessionDataTask,
        didReceive response: URLResponse,
        completionHandler: @escaping (URLSession.ResponseDisposition) -> Void
    ) {
        guard let http = response as? HTTPURLResponse else {
            reject(FunAsrError.invalidResponse("non-HTTP response"), completionHandler: completionHandler)
            return
        }

        let startAt: Int64
        switch (existingBytes, http.statusCode) {
        case (0, 200):
            startAt = 0
        case (let existing, 206) where existing > 0:
            let expected = "bytes \(existing)-\(pinnedSize - 1)/\(pinnedSize)"
            guard http.value(forHTTPHeaderField: "Content-Range")?
                .trimmingCharacters(in: .whitespacesAndNewlines)
                .lowercased() == expected else {
                reject(FunAsrError.invalidResponse("bad Content-Range"), discard: true,
                       completionHandler: completionHandler)
                return
            }
            startAt = existing
        case (_, 200):
            // The origin ignored Range. Restart safely from byte zero.
            startAt = 0
        default:
            reject(FunAsrError.invalidResponse("HTTP \(http.statusCode)"),
                   completionHandler: completionHandler)
            return
        }

        let expectedResponseBytes = pinnedSize - startAt
        guard response.expectedContentLength <= expectedResponseBytes else {
            reject(FunAsrError.invalidResponse("response exceeds pinned size"),
                   completionHandler: completionHandler)
            return
        }

        do {
            if !files.fileExists(atPath: destination.path) {
                guard files.createFile(atPath: destination.path, contents: nil) else {
                    throw CocoaError(.fileWriteUnknown)
                }
            }
            let handle = try FileHandle(forWritingTo: destination)
            if startAt == 0 {
                try handle.truncate(atOffset: 0)
                try handle.seek(toOffset: 0)
            } else {
                let actual = try handle.seekToEnd()
                guard actual == UInt64(startAt) else {
                    try? handle.close()
                    reject(FunAsrError.invalidResponse("partial file changed during download"), discard: true,
                           completionHandler: completionHandler)
                    return
                }
            }
            output = handle
            receivedBytes = startAt
            lastReportedBytes = startAt
            acceptedResponse = true
            if startAt == 0, existingBytes > 0 { progress(0) }
            completionHandler(.allow)
        } catch {
            reject(error, completionHandler: completionHandler)
        }
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        guard failure == nil, let output else { return }
        let next = receivedBytes + Int64(data.count)
        guard next <= pinnedSize else {
            failure = FunAsrError.invalidResponse("download exceeds pinned size")
            discardPartial = true
            dataTask.cancel()
            return
        }
        do {
            try output.write(contentsOf: data)
            receivedBytes = next
            if receivedBytes == pinnedSize
                || receivedBytes - lastReportedBytes >= Self.progressStride {
                lastReportedBytes = receivedBytes
                progress(receivedBytes)
            }
        } catch {
            failure = error
            dataTask.cancel()
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        var completionError = failure ?? error
        if let output {
            do {
                try output.synchronize()
                try output.close()
            } catch {
                if completionError == nil { completionError = error }
            }
            self.output = nil
        }

        let wasCancelled = lock.withLock { cancellationRequested }
        if wasCancelled {
            completionError = CancellationError()
        } else if completionError == nil {
            if !acceptedResponse || receivedBytes != pinnedSize {
                completionError = FunAsrError.invalidResponse("download ended before the pinned size")
            }
        }
        if discardPartial { try? files.removeItem(at: destination) }

        let state = lock.withLock { () -> (CheckedContinuation<Int64, Error>?, URLSession?) in
            guard !finished else { return (nil, nil) }
            finished = true
            let state = (continuation, activeSession)
            continuation = nil
            activeSession = nil
            activeTask = nil
            return state
        }
        state.1?.finishTasksAndInvalidate()
        guard let continuation = state.0 else { return }
        if let completionError {
            continuation.resume(throwing: completionError)
        } else {
            continuation.resume(returning: receivedBytes)
        }
    }

    private func reject(
        _ error: Error,
        discard: Bool = false,
        completionHandler: (URLSession.ResponseDisposition) -> Void
    ) {
        failure = error
        discardPartial = discard
        completionHandler(.cancel)
    }
}

private final class NativeProcess: @unchecked Sendable {
    private let process = Process()
    private let lock = NSLock()
    private var cancellationRequested = false

    fileprivate init(executable: URL, arguments: [String], currentDirectory: URL?) {
        process.executableURL = executable
        process.arguments = arguments
        if let currentDirectory {
            process.currentDirectoryURL = currentDirectory
        }
    }

    static func run(
        executable: URL,
        arguments: [String],
        currentDirectory: URL? = nil,
        timeout: TimeInterval? = nil
    ) async throws -> NativeProcessResult {
        let native = NativeProcess(executable: executable, arguments: arguments, currentDirectory: currentDirectory)
        return try await native.run(timeout: timeout)
    }

    func run(timeout: TimeInterval? = nil) async throws -> NativeProcessResult {
        guard let timeout else { return try await runProcess() }
        return try await withThrowingTaskGroup(of: NativeProcessResult.self) { group in
            group.addTask { try await self.runProcess() }
            group.addTask {
                let seconds = max(0, timeout)
                let nanoseconds = UInt64(min(seconds * 1_000_000_000, Double(UInt64.max)))
                try await Task<Never, Never>.sleep(nanoseconds: nanoseconds)
                throw FunAsrError.processTimedOut
            }
            defer { group.cancelAll() }
            guard let first = try await group.next() else { throw CancellationError() }
            return first
        }
    }

    private func runProcess() async throws -> NativeProcessResult {
        try checkCancellationRequest()
        let output = Pipe()
        let errors = Pipe()
        process.standardOutput = output
        process.standardError = errors
        try process.run()
        if isCancellationRequested() { process.terminate() }
        return try await withTaskCancellationHandler {
            try Task.checkCancellation()
            async let stdout = Self.read(output.fileHandleForReading)
            async let stderr = Self.read(errors.fileHandleForReading)
            await Task.detached { [process] in process.waitUntilExit() }.value
            let result = NativeProcessResult(status: process.terminationStatus,
                                             stdout: await stdout, stderr: await stderr)
            try Task.checkCancellation()
            return result
        } onCancel: {
            self.cancel()
        }
    }

    func cancel() {
        let processToStop = lock.withLock { () -> (Process, pid_t)? in
            cancellationRequested = true
            guard process.isRunning else { return nil }
            return (process, process.processIdentifier)
        }
        guard let (process, pid) = processToStop else { return }
        process.terminate()
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 1) {
            guard process.isRunning, process.processIdentifier == pid else { return }
            _ = Darwin.kill(pid, SIGKILL)
        }
    }

    private func isCancellationRequested() -> Bool {
        lock.withLock { cancellationRequested }
    }

    private func checkCancellationRequest() throws {
        if isCancellationRequested() { throw CancellationError() }
    }

    private static func read(_ handle: FileHandle) async -> Data {
        await Task.detached {
            var result = Data()
            while let chunk = try? handle.read(upToCount: 65_536), !chunk.isEmpty {
                result.append(chunk)
            }
            return result
        }.value
    }
}

final class FunAsrEngine: SpeechEngine, @unchecked Sendable {
    let needsAudioFeed = true
    let hasInterimResults = false
    let stopTimeout: TimeInterval

    var onPartial: ((String) -> Void)?
    var onFinal: ((String) -> Void)?
    var onFault: ((SpeechFault) -> Void)?

    private let resolved: FunAsrResolvedModel
    private let lock = NSLock()
    private var pcm = Data()
    private var started = false
    private var cancelled = false
    private var process: NativeProcess?
    private var generation = UUID()

    init(resolved: FunAsrResolvedModel, stopTimeout: TimeInterval = 60) {
        self.resolved = resolved
        self.stopTimeout = stopTimeout
    }

    func start(language: String) async throws {
        guard resolved.model.supports(language) else {
            throw SpeechFault(.service, "\(resolved.model.displayName) does not support \(language).")
        }
        let previous = lock.withLock {
            generation = UUID()
            pcm.removeAll(keepingCapacity: true)
            started = true
            cancelled = false
            let previous = process
            process = nil
            return previous
        }
        previous?.cancel()
    }

    func feed(_ pcm16kMono: Data) {
        lock.withLock {
            if started && !cancelled { pcm.append(pcm16kMono) }
        }
    }

    func stop() async {
        guard let (recording, wasCancelled, generation) = lock.withLock({ () -> (Data, Bool, UUID)? in
            guard started else { return nil }
            started = false
            let recording = pcm
            pcm.removeAll(keepingCapacity: true)
            return (recording, cancelled, self.generation)
        }) else { return }
        guard !recording.isEmpty, !wasCancelled else { return }

        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("gujiguji", isDirectory: true)
        let wave = directory.appendingPathComponent("funasr-\(UUID().uuidString).wav")
        defer { try? FileManager.default.removeItem(at: wave) }
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            try PcmWave.wrap(recording).write(to: wave, options: .atomic)
            let native = NativeProcess(executable: resolved.executable,
                                       arguments: Self.arguments(for: resolved, wave: wave),
                                       currentDirectory: resolved.executable.deletingLastPathComponent())
            let accepted = lock.withLock {
                guard self.generation == generation, !cancelled else { return false }
                process = native
                return true
            }
            guard accepted else { native.cancel(); return }
            defer { lock.withLock { if process === native { process = nil } } }
            let result = try await native.run(timeout: stopTimeout)
            let ignore = lock.withLock { cancelled || self.generation != generation }
            guard !ignore else { return }
            guard result.status == 0 else {
                let detail = String(decoding: result.stderr.prefix(500), as: UTF8.self)
                    .trimmingCharacters(in: .whitespacesAndNewlines)
                throw FunAsrError.processFailed(result.status, detail)
            }
            let text = String(decoding: result.stdout, as: UTF8.self)
                .trimmingCharacters(in: .whitespacesAndNewlines)
            if !text.isEmpty { onFinal?(text) }
        } catch is CancellationError {
            // Explicit cancellation is a normal dictation outcome.
        } catch {
            let ignore = lock.withLock {
                cancelled || self.generation != generation
            }
            if !ignore {
                onFault?(SpeechFault(.service,
                    "Local FunASR transcription failed. Check the selected model in Settings.",
                    detail: error.localizedDescription))
            }
        }
    }

    func cancel() {
        let running = lock.withLock {
            cancelled = true
            started = false
            generation = UUID()
            pcm.removeAll(keepingCapacity: true)
            let running = process
            process = nil
            return running
        }
        running?.cancel()
    }

    static func arguments(for resolved: FunAsrResolvedModel, wave: URL) -> [String] {
        let modelURLs = resolved.model.artifacts.compactMap { resolved.artifacts[$0.relativePath] }
        var arguments: [String]
        if resolved.model.runner == .nano {
            let encoder = modelURLs.first { $0.lastPathComponent.localizedCaseInsensitiveContains("encoder") }!
            let decoder = modelURLs.first { $0 != encoder }!
            arguments = ["--enc", encoder.path, "-m", decoder.path]
        } else {
            arguments = ["-m", modelURLs[0].path]
        }
        arguments += ["-a", wave.path, "--vad", resolved.vad.path]
        return arguments
    }
}

private extension NSLock {
    func withLock<T>(_ body: () throws -> T) rethrows -> T {
        lock()
        defer { unlock() }
        return try body()
    }
}
