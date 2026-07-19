import Foundation
import CoreGraphics

enum SpeechEngineKind: String, CaseIterable, Codable, Identifiable {
    case funAsr
    case macOS
    case azure
    case gptTranscribe

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .funAsr: "Local models"
        case .macOS: "macOS Speech"
        case .azure: "Azure Speech"
        case .gptTranscribe: "gpt-4o-transcribe"
        }
    }
}

enum AzureAuthMode: String, CaseIterable, Codable, Identifiable {
    case key
    case entraId
    var id: String { rawValue }
}

enum PttMode: String, CaseIterable, Codable, Identifiable {
    case hold
    case toggle
    var id: String { rawValue }
}

enum OverlayPosition: String, CaseIterable, Codable, Identifiable {
    case top
    case bottom
    var id: String { rawValue }
}

enum ActivationKey: String, CaseIterable, Codable, Identifiable {
    case rightControl
    case leftControl
    case capsLock
    case rightOption
    case rightShift
    case fn

    var id: String { rawValue }

    var displayName: String {
        switch self {
        case .rightControl: "Right Control"
        case .leftControl: "Left Control"
        case .capsLock: "Caps Lock"
        case .rightOption: "Right Option"
        case .rightShift: "Right Shift"
        case .fn: "Fn / Globe"
        }
    }

    var keyCode: CGKeyCode {
        switch self {
        case .rightControl: 62
        case .leftControl: 59
        case .capsLock: 57
        case .rightOption: 61
        case .rightShift: 60
        case .fn: 63
        }
    }

    var modifierFlag: CGEventFlags {
        switch self {
        case .rightControl, .leftControl: .maskControl
        case .capsLock: .maskAlphaShift
        case .rightOption: .maskAlternate
        case .rightShift: .maskShift
        case .fn: .maskSecondaryFn
        }
    }

    var siblingKeyCodes: [CGKeyCode] {
        switch self {
        case .rightControl: [ActivationKey.leftControl.keyCode]
        case .leftControl: [ActivationKey.rightControl.keyCode]
        case .rightOption: [58] // Left Option
        case .rightShift: [56] // Left Shift
        case .capsLock, .fn: []
        }
    }
}

struct InputProfile: Codable, Equatable, Identifiable {
    static let profile1Id = "profile-1"
    static let profile2Id = "profile-2"
    static let maxNameLength = 24

    var id: String
    var name: String
    var activationKey: ActivationKey
    var pttMode: PttMode
    var overlayPosition: OverlayPosition

    static let defaults = [
        InputProfile(id: profile1Id, name: "Desktop", activationKey: .rightControl,
                     pttMode: .hold, overlayPosition: .bottom),
        InputProfile(id: profile2Id, name: "Mobile", activationKey: .leftControl,
                     pttMode: .toggle, overlayPosition: .top),
    ]

    mutating func normalize(fallback: InputProfile) {
        id = fallback.id
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        name = trimmed.isEmpty ? fallback.name : String(trimmed.prefix(Self.maxNameLength))
    }

    static func normalize(_ supplied: [InputProfile]) -> [InputProfile] {
        var result = defaults.map { fallback in
            var value = supplied.first(where: { $0.id == fallback.id }) ?? fallback
            value.normalize(fallback: fallback)
            return value
        }
        if result[0].name.caseInsensitiveCompare(result[1].name) == .orderedSame {
            result[1].name = result[0].name.caseInsensitiveCompare("Mobile") == .orderedSame
                ? "Profile 2" : "Mobile"
        }
        return result
    }
}

enum TranscribeModelKind: String, CaseIterable, Codable, Identifiable {
    case gpt4oTranscribe
    case gpt4oMiniTranscribe
    case gpt4oTranscribeDiarize
    case unknown
    var id: String { rawValue }
}

struct AppSettings: Equatable {
    var onboardingCompleted = false
    var language = "zh-CN"
    var profiles = InputProfile.defaults
    var activeProfileId = InputProfile.profile1Id
    var engine = SpeechEngineKind.funAsr
    var funAsrModelId = FunAsrCatalog.defaultId

    var azureKey = ""
    var azureRegion = "eastasia"
    var azureAuthMode = AzureAuthMode.key
    var azureEndpoint = ""
    var azureTenantId = ""
    var azureResourceId = ""

    var transcribeEndpoint = ""
    var transcribeModel = "gpt-4o-transcribe"
    var transcribeModelKind = TranscribeModelKind.gpt4oTranscribe
    var transcribeAuthMode = AzureAuthMode.entraId
    var transcribeApiKey = ""
    var transcribeTenantId = ""
    var recognitionVocabulary: [String] = []

    var llmEnabled = false
    var llmBaseUrl = "https://api.openai.com/v1"
    var llmApiKey = ""
    var llmModel = "gpt-4.1-mini"
    var llmPrompt = ""
    var llmLearnedRules = ""

    var learnFromEdits = false
    var diagnosticLogging = false
    var useContext = false

    var activeProfile: InputProfile {
        get { profiles.first(where: { $0.id == activeProfileId }) ?? profiles[0] }
        set {
            guard let index = profiles.firstIndex(where: { $0.id == newValue.id }) else { return }
            profiles[index] = newValue
        }
    }

    mutating func normalize() {
        profiles = InputProfile.normalize(profiles)
        if !profiles.contains(where: { $0.id == activeProfileId }) {
            activeProfileId = InputProfile.profile1Id
        }
        recognitionVocabulary = RecognitionVocabulary.normalize(recognitionVocabulary)
        if !Self.supportedLanguages.contains(where: { $0.code == language }) { language = "zh-CN" }
    }

    static let supportedLanguages: [(code: String, display: String)] = [
        ("en-US", "English"),
        ("zh-CN", "简体中文"),
        ("zh-TW", "繁體中文"),
        ("ja-JP", "日本語"),
        ("ko-KR", "한국어"),
        ("vi-VN", "Tiếng Việt"),
    ]
}

enum RecognitionVocabulary {
    static let separators = CharacterSet(charactersIn: "\r\n,，;；")

    static func parse(_ text: String) -> [String] {
        normalize(text.components(separatedBy: separators))
    }

    static func normalize<S: Sequence>(_ values: S) -> [String] where S.Element == String {
        var seen = Set<String>()
        var result: [String] = []
        for value in values {
            let term = value.trimmingCharacters(in: .whitespacesAndNewlines)
            let key = term.lowercased()
            if !term.isEmpty, seen.insert(key).inserted { result.append(term) }
        }
        return result
    }

    static func prompt(_ entries: [String]) -> String {
        (["Vocabulary and spelling hints:"] + entries).joined(separator: "\n")
    }
}

enum RefinementGuard {
    static func isSafe(original: String, refined: String) -> Bool {
        guard !refined.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return false }
        guard refined.count <= max(original.count * 2, original.count + 80) else { return false }
        guard !refined.unicodeScalars.contains(where: {
            CharacterSet.controlCharacters.contains($0) && $0 != "\r" && $0 != "\n" && $0 != "\t"
        }) else { return false }

        let sourceCJK = Set(original.filter(isCJK))
        if original.filter(isCJK).count >= 4 {
            let outputCJK = Set(refined.filter(isCJK))
            return sourceCJK.filter(outputCJK.contains).count >= max(1, (sourceCJK.count + 3) / 4)
        }

        let source = terms(original)
        let output = terms(refined)
        if source.count < 3 || output.isEmpty { return true }
        return source.filter(output.contains).count >= max(1, source.count / 4)
    }

    private static func isCJK(_ character: Character) -> Bool {
        character.unicodeScalars.contains { scalar in
            (0x3400...0x4DBF).contains(scalar.value)
                || (0x4E00...0x9FFF).contains(scalar.value)
                || (0xF900...0xFAFF).contains(scalar.value)
        }
    }

    private static func terms(_ text: String) -> Set<String> {
        Set(text.lowercased().components(separatedBy: CharacterSet.alphanumerics.inverted)
            .filter { $0.count > 1 })
    }
}

enum PcmWave {
    static func wrap(_ pcm: Data, sampleRate: Int = 16_000) -> Data {
        var data = Data()
        func ascii(_ string: String) { data.append(string.data(using: .ascii)!) }
        func little<T: FixedWidthInteger>(_ value: T) {
            var value = value.littleEndian
            withUnsafeBytes(of: &value) { data.append(contentsOf: $0) }
        }
        ascii("RIFF")
        little(UInt32(36 + pcm.count))
        ascii("WAVEfmt ")
        little(UInt32(16))
        little(UInt16(1))
        little(UInt16(1))
        little(UInt32(sampleRate))
        little(UInt32(sampleRate * 2))
        little(UInt16(2))
        little(UInt16(16))
        ascii("data")
        little(UInt32(pcm.count))
        data.append(pcm)
        return data
    }
}

struct SpeechFault: Error, LocalizedError {
    enum Kind { case authentication, quota, network, timeout, service, unknown }
    let kind: Kind
    let message: String
    let detail: String?
    var errorDescription: String? { message }

    init(_ kind: Kind, _ message: String, detail: String? = nil) {
        self.kind = kind
        self.message = message
        self.detail = detail
    }
}

enum DictationState: String {
    case idle, starting, listening, transcribing, refining, injecting, failed, cancelled

    var isProcessing: Bool {
        self == .transcribing || self == .refining || self == .injecting
    }
}

enum PttGesture: Equatable { case pressed, released, cancelled, recoveredRelease }
enum PttAction: Equatable { case none, start, stop, cancel, busy }

enum PttRouter {
    static func action(mode: PttMode, gesture: PttGesture, dictating: Bool,
                       state: DictationState) -> PttAction {
        if mode == .toggle {
            // A toggle changes state only after a complete down/up cycle observed
            // by the event tap. A watchdog recovery is deliberately fail-closed:
            // it may be the tail of a shortcut whose earlier event was missed.
            guard gesture == .released else { return .none }
            if state.isProcessing { return .busy }
            return dictating ? .stop : .start
        }
        switch gesture {
        case .pressed: return state.isProcessing ? .busy : .start
        case .released, .recoveredRelease: return .stop
        case .cancelled: return .cancel
        }
    }
}

enum SettingsValidation {
    static func error(in settings: AppSettings) -> String? {
        let names = settings.profiles.map { $0.name.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() }
        if names.count != 2 || names.contains(where: { $0.isEmpty }) || names[0] == names[1] {
            return "The two profile names must be non-empty and unique."
        }
        if settings.profiles.contains(where: { $0.name.count > 24 }) {
            return "Profile names cannot exceed 24 characters."
        }
        switch settings.engine {
        case .azure where settings.azureAuthMode == .key:
            if settings.azureKey.isEmpty || settings.azureRegion.trimmingCharacters(in: .whitespaces).isEmpty {
                return "Azure Speech key authentication requires both Key and Region."
            }
        case .azure:
            if !isHTTPS(settings.azureEndpoint) { return "Azure Speech requires an HTTPS endpoint." }
            if settings.azureRegion.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                return "Azure Speech Entra authentication requires the resource region."
            }
            if !isAzureResourceId(settings.azureResourceId) {
                return "Azure Speech Entra authentication requires the full Azure Resource ID."
            }
        case .gptTranscribe:
            if !isHTTPS(settings.transcribeEndpoint) { return "Transcription requires an HTTPS endpoint." }
            if settings.transcribeModel.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                return "Transcription requires a deployment or model name."
            }
            if settings.transcribeAuthMode == .key, settings.transcribeApiKey.isEmpty {
                return "Transcription key authentication requires an API key."
            }
        case .funAsr, .macOS:
            break
        }
        if settings.llmEnabled {
            if !isHTTPSOrLoopbackHTTP(settings.llmBaseUrl) {
                return "LLM refinement requires HTTPS, except for a loopback HTTP server."
            }
            if settings.llmModel.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                return "LLM refinement requires a model."
            }
        }
        return nil
    }

    static func isHTTPS(_ value: String) -> Bool {
        guard let url = URL(string: value), url.scheme?.lowercased() == "https" else { return false }
        return !(url.host ?? "").isEmpty
    }

    static func isAzureResourceId(_ value: String) -> Bool {
        let value = value.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        return value.hasPrefix("/subscriptions/")
            && value.contains("/resourcegroups/")
            && value.contains("/providers/microsoft.cognitiveservices/accounts/")
    }

    static func isHTTPSOrLoopbackHTTP(_ value: String) -> Bool {
        guard let url = URL(string: value), let scheme = url.scheme?.lowercased(),
              let host = url.host?.lowercased() else { return false }
        if scheme == "https" { return true }
        return scheme == "http" && (host == "localhost" || host == "127.0.0.1" || host == "::1")
    }
}
