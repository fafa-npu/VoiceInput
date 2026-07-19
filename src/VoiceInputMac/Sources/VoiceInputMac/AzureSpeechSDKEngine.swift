import Foundation
import MicrosoftCognitiveServicesSpeech

/// Azure Speech SDK streaming recognition, matching the Windows push-stream behavior.
final class AzureSpeechEngine: SpeechEngine, @unchecked Sendable {
    static let maxVocabularyPhrases = 500

    static func entraAuthorizationToken(resourceId: String, accessToken: String) -> String {
        "aad#\(resourceId.trimmingCharacters(in: .whitespacesAndNewlines))#\(accessToken)"
    }

    var onPartial: ((String) -> Void)?
    var onFinal: ((String) -> Void)?
    var onFault: ((SpeechFault) -> Void)?
    var stopTimeout: TimeInterval { 5 }

    private enum Authentication {
        case key(String, String)
        case entra(resourceId: String, region: String, tokenProvider: BearerTokenProvider)
    }

    private let authentication: Authentication
    private let vocabulary: [String]
    /// SPX objects are not safe to start/stop/close from competing threads. All
    /// native operations, including late timeout cleanup, stay on this queue.
    private let sdkQueue = DispatchQueue(label: "gujiguji.azure-speech-sdk")
    private let stateLock = NSLock()
    private var stream: SPXPushAudioInputStream?
    private var audioConfiguration: SPXAudioConfiguration?
    private var recognizer: SPXSpeechRecognizer?
    private var closed = true
    private var cancelRequested = false

    private init(authentication: Authentication, vocabulary: [String]) {
        self.authentication = authentication
        self.vocabulary = Array(RecognitionVocabulary.normalize(vocabulary).prefix(Self.maxVocabularyPhrases))
    }

    static func forKey(key: String, region: String, vocabulary: [String] = []) throws -> AzureSpeechEngine {
        let key = key.trimmingCharacters(in: .whitespacesAndNewlines)
        let region = region.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !key.isEmpty, !region.isEmpty else {
            throw SpeechFault(.authentication, "Azure Speech requires both Key and Region.")
        }
        return AzureSpeechEngine(authentication: .key(key, region), vocabulary: vocabulary)
    }

    static func forBearer(
        endpoint: String,
        resourceId: String,
        region: String,
        tokenProvider: @escaping BearerTokenProvider,
        vocabulary: [String] = []
    ) throws -> AzureSpeechEngine {
        guard SettingsValidation.isHTTPS(endpoint) else {
            throw SpeechFault(.service, "Azure Speech requires a valid HTTPS endpoint.")
        }
        let resourceId = resourceId.trimmingCharacters(in: .whitespacesAndNewlines)
        let region = region.trimmingCharacters(in: .whitespacesAndNewlines)
        guard SettingsValidation.isAzureResourceId(resourceId), !region.isEmpty else {
            throw SpeechFault(.authentication, "Azure Speech Entra authentication requires its full Resource ID and region.")
        }
        return AzureSpeechEngine(
            authentication: .entra(resourceId: resourceId, region: region, tokenProvider: tokenProvider),
            vocabulary: vocabulary)
    }

    func start(language: String) async throws {
        guard !isCancellationRequested else { throw CancellationError() }
        let bearerToken: String?
        switch authentication {
        case .key:
            bearerToken = nil
        case let .entra(_, _, provider):
            bearerToken = try await provider()
        }
        guard !isCancellationRequested, !Task.isCancelled else { throw CancellationError() }

        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            sdkQueue.async { [self] in
                guard !isCancellationRequested else {
                    continuation.resume(throwing: CancellationError())
                    return
                }
                do {
                    let configuration: SPXSpeechConfiguration
                    switch authentication {
                    case let .key(key, region):
                        configuration = try SPXSpeechConfiguration(subscription: key, region: region)
                    case let .entra(resourceId, region, _):
                        let authorizationToken = Self.entraAuthorizationToken(
                            resourceId: resourceId,
                            accessToken: bearerToken ?? "")
                        configuration = try SPXSpeechConfiguration(
                            authorizationToken: authorizationToken,
                            region: region)
                    }
                    configuration.speechRecognitionLanguage = language

                    let stream = SPXPushAudioInputStream()
                    guard let audio = SPXAudioConfiguration(streamInput: stream) else {
                        throw SpeechFault(.service, "Azure Speech could not create its audio stream.")
                    }
                    let recognizer = try SPXSpeechRecognizer(
                        speechConfiguration: configuration,
                        audioConfiguration: audio)
                    installHandlers(on: recognizer)
                    if !vocabulary.isEmpty, let grammar = SPXPhraseListGrammar(recognizer: recognizer) {
                        for phrase in vocabulary { grammar.addPhrase(phrase) }
                    }

                    self.stream = stream
                    audioConfiguration = audio
                    self.recognizer = recognizer
                    stateLock.withLock { closed = false }
                    try recognizer.startContinuousRecognition()

                    if isCancellationRequested {
                        stopOnSDKQueue()
                        continuation.resume(throwing: CancellationError())
                    } else {
                        continuation.resume(returning: ())
                    }
                } catch is CancellationError {
                    stopOnSDKQueue()
                    continuation.resume(throwing: CancellationError())
                } catch {
                    stopOnSDKQueue()
                    continuation.resume(throwing: SpeechFault(
                        .service,
                        "Azure Speech could not start streaming recognition.",
                        detail: error.localizedDescription))
                }
            }
        }
    }

    func feed(_ pcm16kMono: Data) {
        sdkQueue.async { [weak self] in
            guard let self, !self.stateLock.withLock({ self.closed }) else { return }
            self.stream?.write(pcm16kMono)
        }
    }

    func stop() async {
        await withCheckedContinuation { continuation in
            sdkQueue.async { [self] in
                stopOnSDKQueue()
                continuation.resume()
            }
        }
    }

    func cancel() {
        stateLock.withLock {
            cancelRequested = true
            closed = true
        }
        sdkQueue.async { [self] in stopOnSDKQueue() }
    }

    private var isCancellationRequested: Bool {
        stateLock.withLock { cancelRequested }
    }

    private func installHandlers(on recognizer: SPXSpeechRecognizer) {
        recognizer.addRecognizingEventHandler { [weak self] _, event in
            guard let text = event.result.text?.trimmingCharacters(in: .whitespacesAndNewlines),
                  !text.isEmpty else { return }
            self?.onPartial?(text)
        }
        recognizer.addRecognizedEventHandler { [weak self] _, event in
            guard let text = event.result.text?.trimmingCharacters(in: .whitespacesAndNewlines),
                  !text.isEmpty else { return }
            self?.onFinal?(text)
        }
        recognizer.addCanceledEventHandler { [weak self] _, event in
            guard let self, !self.stateLock.withLock({ self.closed }) else { return }
            let detail = event.errorDetails ?? "errorCode=\(event.errorCode.rawValue)"
            self.onFault?(Self.mapFault(detail))
        }
    }

    /// Must only be called from sdkQueue.
    private func stopOnSDKQueue() {
        stateLock.withLock { closed = true }
        stream?.close()
        try? recognizer?.stopContinuousRecognition()
        stream = nil
        audioConfiguration = nil
        recognizer = nil
    }

    private static func mapFault(_ detail: String) -> SpeechFault {
        let value = detail.lowercased()
        if value.contains("auth") || value.contains("401") || value.contains("403") {
            return SpeechFault(.authentication, "Azure Speech authentication failed.", detail: detail)
        }
        if value.contains("quota") || value.contains("429") || value.contains("too many") {
            return SpeechFault(.quota, "Azure Speech is rate-limited or out of quota.", detail: detail)
        }
        if value.contains("connection") || value.contains("network") || value.contains("timeout") {
            return SpeechFault(.network, "Azure Speech could not be reached.", detail: detail)
        }
        return SpeechFault(.service, "Azure Speech could not transcribe this recording.", detail: detail)
    }
}
