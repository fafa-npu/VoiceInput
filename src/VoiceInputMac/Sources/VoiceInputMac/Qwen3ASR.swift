import CTranscribe
import CryptoKit
import Foundation
import os

enum Qwen3AsrError: Error, LocalizedError {
    case notInstalled
    case installationBusy
    case insufficientDiskSpace
    case invalidResponse(String)
    case invalidHash(String)
    case native(String)

    var errorDescription: String? {
        switch self {
        case .notInstalled:
            "The selected Qwen3-ASR model is not installed. Download it from Model Selection first."
        case .installationBusy:
            "Wait for the active local-model operation to finish."
        case .insufficientDiskSpace:
            "There is not enough disk space for the selected Qwen3-ASR model."
        case .invalidResponse(let detail):
            "The Qwen3-ASR download response is invalid: \(detail)"
        case .invalidHash(let name):
            "SHA-256 verification failed for \(name)."
        case .native(let detail):
            "Qwen3-ASR could not run: \(detail)"
        }
    }
}

final class Qwen3AsrCancellation: @unchecked Sendable {
    private let state = OSAllocatedUnfairLock(initialState: false)

    func cancel() { state.withLock { $0 = true } }
    var isCancelled: Bool { state.withLock { $0 } }
}

private func qwen3AsrAbortTrampoline(_ userData: UnsafeMutableRawPointer?) -> Bool {
    guard let userData else { return false }
    return Unmanaged<Qwen3AsrCancellation>.fromOpaque(userData)
        .takeUnretainedValue().isCancelled
}

protocol Qwen3AsrTranscribing: AnyObject {
    func transcribe(
        model: FunAsrModel,
        pcm16kMono: Data,
        cancellation: Qwen3AsrCancellation
    ) async throws -> String
}

/// Owns the downloaded Qwen model and one process-wide native model handle.
/// A lightweight session is created per utterance; the expensive model and
/// Metal buffers remain resident between dictations.
final class Qwen3AsrRuntimeManager: Qwen3AsrTranscribing, @unchecked Sendable {
    static let runtimeVersion = "transcribe.cpp 0.1.3"

    var onProgress: ((FunAsrInstallProgress) -> Void)?

    private static let stagingMargin: Int64 = 64 * 1_024 * 1_024
    private let root: URL
    private let configuration: URLSessionConfiguration
    private let files = FileManager.default
    private let stateLock = NSLock()
    private let inferenceQueue = DispatchQueue(
        label: "com.gujiguji.qwen3-asr.inference", qos: .userInitiated)
    private let verificationQueue = DispatchQueue(
        label: "com.gujiguji.qwen3-asr.verification", qos: .utility)

    private var installing = false
    private var verified: VerifiedArtifact?
    // Access only on inferenceQueue (apart from deinit, after queued work ends).
    private var loadedModel: OpaquePointer?
    private var loadedPath = ""

    init(root: URL = AppPaths.qwen3Asr, session: URLSession = .shared) {
        self.root = root.standardizedFileURL
        configuration = session.configuration
    }

    deinit {
        if let loadedModel { transcribe_model_free(loadedModel) }
    }

    func state(for modelId: String) -> FunAsrInstallStage {
        hasInstalledFiles(modelId) ? .installed : .notInstalled
    }

    func hasInstalledFiles(_ modelId: String) -> Bool {
        guard let model = try? qwenModel(modelId), let artifact = model.artifacts.first,
              let url = try? artifactURL(artifact) else { return false }
        return fileSize(url) == artifact.size
    }

    func install(_ modelId: String) async throws {
        let model = try qwenModel(modelId)
        guard let artifact = model.artifacts.first else { throw Qwen3AsrError.notInstalled }
        try beginOperation()
        defer { endOperation() }

        do {
            if try await artifactMatches(artifact) {
                report(model.id, .testing, artifact: model.displayName,
                       downloaded: artifact.size, total: artifact.size)
                try await smokeTest(model.id)
                try Task.checkCancellation()
                report(model.id, .installed, artifact: model.displayName,
                       downloaded: artifact.size, total: artifact.size)
                return
            }

            let final = try artifactURL(artifact)
            let part = final.appendingPathExtension("part")
            try files.createDirectory(at: final.deletingLastPathComponent(),
                                      withIntermediateDirectories: true)
            if files.fileExists(atPath: final.path) { try files.removeItem(at: final) }

            var existing = fileSize(part) ?? 0
            if existing > artifact.size {
                try files.removeItem(at: part)
                existing = 0
            }
            try checkDiskSpace(bytes: artifact.size - existing)

            if existing < artifact.size {
                var request = URLRequest(url: artifact.url)
                if existing > 0 {
                    request.setValue("bytes=\(existing)-", forHTTPHeaderField: "Range")
                }
                report(model.id, .downloading, artifact: final.lastPathComponent,
                       downloaded: existing, total: artifact.size)
                let transfer = PersistentDownload(
                    configuration: configuration,
                    request: request,
                    destination: part,
                    existingBytes: existing,
                    pinnedSize: artifact.size
                ) { [weak self] downloaded in
                    self?.report(model.id, .downloading, artifact: final.lastPathComponent,
                                 downloaded: downloaded, total: artifact.size)
                }
                let downloaded = try await transfer.run()
                guard downloaded == artifact.size else {
                    throw Qwen3AsrError.invalidResponse("download ended before the pinned size")
                }
            }

            try Task.checkCancellation()
            report(model.id, .verifying, artifact: final.lastPathComponent,
                   downloaded: artifact.size, total: artifact.size)
            guard try await sha256(part) == artifact.sha256 else {
                try? files.removeItem(at: part)
                throw Qwen3AsrError.invalidHash(final.lastPathComponent)
            }
            if files.fileExists(atPath: final.path) {
                _ = try files.replaceItemAt(final, withItemAt: part)
            } else {
                try files.moveItem(at: part, to: final)
            }
            cacheVerified(final, sha256: artifact.sha256)

            report(model.id, .testing, artifact: model.displayName,
                   downloaded: artifact.size, total: artifact.size)
            try await smokeTest(model.id)
            try Task.checkCancellation()
            report(model.id, .installed, artifact: model.displayName,
                   downloaded: artifact.size, total: artifact.size)
        } catch is CancellationError {
            report(model.id, .notInstalled)
            throw CancellationError()
        } catch {
            report(model.id, .failed, error: error.localizedDescription)
            throw error
        }
    }

    func remove(_ modelId: String) async throws {
        let model = try qwenModel(modelId)
        try beginOperation()
        defer { endOperation() }

        try await enqueue {
            self.unloadModel()
            for artifact in model.artifacts {
                let final = try self.artifactURL(artifact)
                if self.files.fileExists(atPath: final.path) { try self.files.removeItem(at: final) }
                let part = final.appendingPathExtension("part")
                if self.files.fileExists(atPath: part.path) { try self.files.removeItem(at: part) }
            }
            self.stateLock.withLock { self.verified = nil }
        }
    }

    func prewarm(_ modelId: String) async throws {
        let model = try qwenModel(modelId)
        try await enqueue {
            let url = try self.resolve(model)
            _ = try self.loadModelIfNeeded(at: url)
        }
    }

    private func smokeTest(_ modelId: String) async throws {
        let model = try qwenModel(modelId)
        try await enqueue {
            let url = try self.resolve(model)
            _ = try self.loadModelIfNeeded(at: url)
            self.unloadModel()
        }
    }

    func transcribe(
        model: FunAsrModel,
        pcm16kMono: Data,
        cancellation: Qwen3AsrCancellation
    ) async throws -> String {
        guard model.runner == .qwen3Asr else { throw Qwen3AsrError.notInstalled }
        let samples = Self.floatPCM(from: pcm16kMono)
        guard !samples.isEmpty else { return "" }

        return try await withTaskCancellationHandler {
            try await enqueue {
                if cancellation.isCancelled { throw CancellationError() }
                let url = try self.resolve(model)
                let nativeModel = try self.loadModelIfNeeded(at: url)

                var nativeSession: OpaquePointer?
                let sessionStatus = transcribe_session_init(nativeModel, nil, &nativeSession)
                try Self.check(sessionStatus, operation: "creating a session")
                guard let nativeSession else {
                    throw Qwen3AsrError.native("the runtime returned an empty session")
                }
                defer { transcribe_session_free(nativeSession) }

                let context = Unmanaged.passUnretained(cancellation).toOpaque()
                transcribe_set_abort_callback(nativeSession, qwen3AsrAbortTrampoline, context)
                defer { transcribe_set_abort_callback(nativeSession, nil, nil) }

                let status = samples.withUnsafeBufferPointer {
                    transcribe_run(nativeSession, $0.baseAddress, Int32($0.count), nil)
                }
                if status == TRANSCRIBE_ERR_ABORTED || cancellation.isCancelled {
                    throw CancellationError()
                }
                try Self.check(status, operation: "transcribing audio")
                let text = Self.string(transcribe_full_text(nativeSession))
                    .trimmingCharacters(in: .whitespacesAndNewlines)
                let detected = Self.string(transcribe_detected_language(nativeSession))
                AppLog.write("Qwen3-ASR completed backend=\(self.backendName(nativeModel)) "
                             + "language=\(detected.isEmpty ? "auto" : detected) samples=\(samples.count)")
                return text
            }
        } onCancel: {
            cancellation.cancel()
        }
    }

    static func floatPCM(from pcm16kMono: Data) -> [Float] {
        let count = pcm16kMono.count / 2
        guard count > 0 else { return [] }
        return pcm16kMono.withUnsafeBytes { raw in
            let bytes = raw.bindMemory(to: UInt8.self)
            return (0..<count).map { index in
                let offset = index * 2
                let bits = UInt16(bytes[offset]) | UInt16(bytes[offset + 1]) << 8
                return Float(Int16(bitPattern: bits)) / 32_768
            }
        }
    }

    private func qwenModel(_ modelId: String) throws -> FunAsrModel {
        guard let model = try? FunAsrCatalog.model(modelId), model.runner == .qwen3Asr,
              model.artifacts.count == 1 else { throw Qwen3AsrError.notInstalled }
        return model
    }

    private func resolve(_ model: FunAsrModel) throws -> URL {
        guard let artifact = model.artifacts.first,
              try fileMatches(artifact, verifyHash: true) else {
            throw Qwen3AsrError.notInstalled
        }
        return try artifactURL(artifact)
    }

    private func loadModelIfNeeded(at url: URL) throws -> OpaquePointer {
        if loadedPath == url.path, let loadedModel { return loadedModel }
        unloadModel()

        var params = transcribe_model_load_params()
        transcribe_model_load_params_init(&params)
        params.backend = TRANSCRIBE_BACKEND_AUTO
        var model: OpaquePointer?
        let status = url.path.withCString {
            transcribe_model_load_file($0, &params, &model)
        }
        try Self.check(status, operation: "loading the Qwen3-ASR model")
        guard let model else { throw Qwen3AsrError.native("the runtime returned an empty model") }
        loadedModel = model
        loadedPath = url.path
        AppLog.write("Qwen3-ASR model loaded backend=\(backendName(model))")
        return model
    }

    private func unloadModel() {
        if let loadedModel { transcribe_model_free(loadedModel) }
        loadedModel = nil
        loadedPath = ""
    }

    private func backendName(_ model: OpaquePointer) -> String {
        let value = Self.string(transcribe_model_backend(model))
        return value.isEmpty ? "unknown" : value
    }

    private static func check(_ status: transcribe_status, operation: String) throws {
        guard status == TRANSCRIBE_OK else {
            let raw = Int32(bitPattern: status.rawValue)
            let detail = string(transcribe_status_string(raw))
            throw Qwen3AsrError.native("\(operation): \(detail)")
        }
    }

    private static func string(_ pointer: UnsafePointer<CChar>?) -> String {
        pointer.map(String.init(cString:)) ?? ""
    }

    private func enqueue<T: Sendable>(_ operation: @escaping @Sendable () throws -> T) async throws -> T {
        try await withCheckedThrowingContinuation { continuation in
            inferenceQueue.async {
                continuation.resume(with: Result { try operation() })
            }
        }
    }

    private func beginOperation() throws {
        try stateLock.withLock {
            guard !installing else { throw Qwen3AsrError.installationBusy }
            installing = true
        }
    }

    private func endOperation() {
        stateLock.withLock { installing = false }
    }

    private func artifactURL(_ artifact: FunAsrArtifact) throws -> URL {
        let candidate = root.appendingPathComponent(artifact.relativePath).standardizedFileURL
        let prefix = root.path.hasSuffix("/") ? root.path : root.path + "/"
        guard candidate.path.hasPrefix(prefix) else {
            throw Qwen3AsrError.invalidResponse("unsafe artifact path")
        }
        return candidate
    }

    private func fileMatches(_ artifact: FunAsrArtifact, verifyHash: Bool) throws -> Bool {
        let url = try artifactURL(artifact)
        guard fileSize(url) == artifact.size else { return false }
        guard verifyHash else { return true }
        if let attributes = try? files.attributesOfItem(atPath: url.path),
           let size = (attributes[.size] as? NSNumber)?.int64Value,
           let modified = attributes[.modificationDate] as? Date,
           let cached = stateLock.withLock({ verified }),
           cached.path == url.path, cached.size == size, cached.modified == modified,
           cached.sha256 == artifact.sha256 {
            return true
        }
        guard try FunAsrRuntimeManager.sha256(url) == artifact.sha256 else { return false }
        cacheVerified(url, sha256: artifact.sha256)
        return true
    }

    private func artifactMatches(_ artifact: FunAsrArtifact) async throws -> Bool {
        let url = try artifactURL(artifact)
        guard fileSize(url) == artifact.size else { return false }
        if let attributes = try? files.attributesOfItem(atPath: url.path),
           let size = (attributes[.size] as? NSNumber)?.int64Value,
           let modified = attributes[.modificationDate] as? Date,
           let cached = stateLock.withLock({ verified }),
           cached.path == url.path, cached.size == size, cached.modified == modified,
           cached.sha256 == artifact.sha256 {
            return true
        }
        guard try await sha256(url) == artifact.sha256 else { return false }
        cacheVerified(url, sha256: artifact.sha256)
        return true
    }

    private func sha256(_ url: URL) async throws -> String {
        let cancellation = Qwen3AsrCancellation()
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                verificationQueue.async {
                    do {
                        let handle = try FileHandle(forReadingFrom: url)
                        defer { try? handle.close() }
                        var hash = SHA256()
                        while let data = try handle.read(upToCount: 1_048_576), !data.isEmpty {
                            if cancellation.isCancelled { throw CancellationError() }
                            hash.update(data: data)
                        }
                        if cancellation.isCancelled { throw CancellationError() }
                        continuation.resume(returning: hash.finalize().map {
                            String(format: "%02x", $0)
                        }.joined())
                    } catch {
                        continuation.resume(throwing: error)
                    }
                }
            }
        } onCancel: {
            cancellation.cancel()
        }
    }

    private func cacheVerified(_ url: URL, sha256: String) {
        guard let attributes = try? files.attributesOfItem(atPath: url.path),
              let size = (attributes[.size] as? NSNumber)?.int64Value,
              let modified = attributes[.modificationDate] as? Date else { return }
        stateLock.withLock {
            verified = VerifiedArtifact(path: url.path, size: size, modified: modified, sha256: sha256)
        }
    }

    private func checkDiskSpace(bytes: Int64) throws {
        let values = try? root.deletingLastPathComponent().resourceValues(
            forKeys: [.volumeAvailableCapacityForImportantUsageKey])
        if let available = values?.volumeAvailableCapacityForImportantUsage,
           available < bytes + Self.stagingMargin {
            throw Qwen3AsrError.insufficientDiskSpace
        }
    }

    private func fileSize(_ url: URL) -> Int64? {
        (try? files.attributesOfItem(atPath: url.path)[.size] as? NSNumber)?.int64Value
    }

    private func report(
        _ modelId: String,
        _ stage: FunAsrInstallStage,
        artifact: String = "",
        downloaded: Int64 = 0,
        total: Int64? = nil,
        error: String? = nil
    ) {
        let progress = FunAsrInstallProgress(
            modelId: modelId, stage: stage, artifact: artifact,
            downloadedBytes: downloaded, totalBytes: total, error: error)
        let callback = stateLock.withLock { onProgress }
        callback?(progress)
    }
}

private struct VerifiedArtifact {
    let path: String
    let size: Int64
    let modified: Date
    let sha256: String
}

final class Qwen3AsrEngine: SpeechEngine, @unchecked Sendable {
    static let minimumPcmBytes = 16_000 // 0.5 seconds at 16 kHz mono PCM16.

    let needsAudioFeed = true
    let hasInterimResults = false
    let stopTimeout: TimeInterval = 60

    var onPartial: ((String) -> Void)?
    var onFinal: ((String) -> Void)?
    var onFault: ((SpeechFault) -> Void)?

    private let model: FunAsrModel
    private let recognizer: Qwen3AsrTranscribing
    private let lock = NSLock()
    private var pcm = Data()
    private var started = false
    private var cancelled = false
    private var generation = UUID()
    private var cancellation = Qwen3AsrCancellation()

    init(model: FunAsrModel, recognizer: Qwen3AsrTranscribing) {
        self.model = model
        self.recognizer = recognizer
    }

    func start(language: String) async throws {
        guard model.supports(language) else {
            throw SpeechFault(.service, "\(model.displayName) does not support \(language).")
        }
        lock.withLock {
            generation = UUID()
            pcm.removeAll(keepingCapacity: true)
            started = true
            cancelled = false
            cancellation = Qwen3AsrCancellation()
        }
    }

    func feed(_ pcm16kMono: Data) {
        lock.withLock {
            if started && !cancelled { pcm.append(pcm16kMono) }
        }
    }

    func stop() async {
        let snapshot: (recording: Data, generation: UUID, token: Qwen3AsrCancellation)? = lock.withLock {
            guard started else { return nil }
            started = false
            let value = (recording: pcm, generation: generation, token: cancellation)
            pcm.removeAll(keepingCapacity: true)
            return value
        }
        guard let snapshot else { return }
        guard snapshot.recording.count >= Self.minimumPcmBytes,
              PCMInspection.hasAudibleSignal(snapshot.recording) else { return }

        do {
            // Qwen3-ASR v0.1.3 intentionally receives no language hint: its
            // supported path is automatic language detection across 30 languages.
            let text = try await recognizer.transcribe(
                model: model, pcm16kMono: snapshot.recording, cancellation: snapshot.token)
            let stale = lock.withLock { cancelled || self.generation != snapshot.generation }
            if !stale, !text.isEmpty { onFinal?(text) }
        } catch is CancellationError {
            // Chord cancellation, pause, shutdown, and timeout are normal exits.
        } catch {
            let stale = lock.withLock { cancelled || self.generation != snapshot.generation }
            if !stale {
                onFault?(SpeechFault(
                    .service,
                    "Local Qwen3-ASR transcription failed. Check the model in Settings.",
                    detail: error.localizedDescription))
            }
        }
    }

    func cancel() {
        let token = lock.withLock {
            cancelled = true
            started = false
            generation = UUID()
            pcm.removeAll(keepingCapacity: true)
            return cancellation
        }
        token.cancel()
    }
}

private extension NSLock {
    func withLock<T>(_ body: () throws -> T) rethrows -> T {
        lock()
        defer { unlock() }
        return try body()
    }
}
