import ApplicationServices
import CryptoKit
import Foundation
import Security

enum AppPaths {
    static let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("gujiguji", isDirectory: true)
    static let settings = support.appendingPathComponent("settings.json")
    static let corrections = support.appendingPathComponent("corrections.jsonl")
    static let funAsr = support.appendingPathComponent("FunASR", isDirectory: true)
    static let qwen3Asr = support.appendingPathComponent("Qwen3ASR", isDirectory: true)
    static let logs = FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("Logs/gujiguji", isDirectory: true)
    static let log = logs.appendingPathComponent("log.txt")
}

enum AppLog {
    private static let lock = NSLock()
    private static let maxBytes: UInt64 = 5 * 1024 * 1024

    static func write(_ message: String) {
        lock.lock()
        defer { lock.unlock() }
        do {
            try FileManager.default.createDirectory(at: AppPaths.logs, withIntermediateDirectories: true)
            if let size = try? AppPaths.log.resourceValues(forKeys: [.fileSizeKey]).fileSize,
               size > maxBytes {
                try? FileManager.default.removeItem(at: AppPaths.log)
            }
            let line = "\(ISO8601DateFormatter().string(from: Date())) \(message)\n"
            if !FileManager.default.fileExists(atPath: AppPaths.log.path) {
                try line.data(using: .utf8)!.write(to: AppPaths.log, options: .atomic)
            } else {
                let handle = try FileHandle(forWritingTo: AppPaths.log)
                try handle.seekToEnd()
                try handle.write(contentsOf: line.data(using: .utf8)!)
                try handle.close()
            }
        } catch { }
    }
}

enum KeychainStore {
    private static let service = "com.gujiguji.voiceinput"

    static func data(_ account: String) -> Data? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess else { return nil }
        return item as? Data
    }

    static func string(_ account: String) -> String {
        data(account).flatMap { String(data: $0, encoding: .utf8) } ?? ""
    }

    static func set(_ value: String, for account: String) throws {
        try set(Data(value.utf8), for: account)
    }

    static func set(_ value: Data, for account: String) throws {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        let attributes: [String: Any] = [
            kSecValueData as String: value,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
        ]
        let status: OSStatus
        if SecItemCopyMatching(query as CFDictionary, nil) == errSecSuccess {
            status = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
        } else {
            status = SecItemAdd(query.merging(attributes) { _, new in new } as CFDictionary, nil)
        }
        guard status == errSecSuccess else {
            throw NSError(domain: NSOSStatusErrorDomain, code: Int(status))
        }
    }

    static func remove(_ account: String) {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        SecItemDelete(query as CFDictionary)
    }
}

private struct PersistedSettings: Codable {
    var onboardingCompleted: Bool?
    var language: String?
    var profiles: [InputProfile]?
    var activeProfileId: String?
    var engine: SpeechEngineKind?
    var funAsrModelId: String?
    var azureRegion: String?
    var azureAuthMode: AzureAuthMode?
    var azureEndpoint: String?
    var azureTenantId: String?
    var azureResourceId: String?
    var transcribeEndpoint: String?
    var transcribeModel: String?
    var transcribeModelKind: TranscribeModelKind?
    var transcribeAuthMode: AzureAuthMode?
    var transcribeTenantId: String?
    var recognitionVocabulary: [String]?
    var llmEnabled: Bool?
    var llmBaseUrl: String?
    var llmModel: String?
    var llmPrompt: String?
    var llmLearnedRules: String?
    var learnFromEdits: Bool?
    var diagnosticLogging: Bool?
    var useContext: Bool?

    init(_ settings: AppSettings) {
        onboardingCompleted = settings.onboardingCompleted
        language = settings.language
        profiles = settings.profiles
        activeProfileId = settings.activeProfileId
        engine = settings.engine
        funAsrModelId = settings.funAsrModelId
        azureRegion = settings.azureRegion
        azureAuthMode = settings.azureAuthMode
        azureEndpoint = settings.azureEndpoint
        azureTenantId = settings.azureTenantId
        azureResourceId = settings.azureResourceId
        transcribeEndpoint = settings.transcribeEndpoint
        transcribeModel = settings.transcribeModel
        transcribeModelKind = settings.transcribeModelKind
        transcribeAuthMode = settings.transcribeAuthMode
        transcribeTenantId = settings.transcribeTenantId
        recognitionVocabulary = settings.recognitionVocabulary
        llmEnabled = settings.llmEnabled
        llmBaseUrl = settings.llmBaseUrl
        llmModel = settings.llmModel
        llmPrompt = settings.llmPrompt
        llmLearnedRules = settings.llmLearnedRules
        learnFromEdits = settings.learnFromEdits
        diagnosticLogging = settings.diagnosticLogging
        useContext = settings.useContext
    }

    func materialize() -> AppSettings {
        var value = AppSettings()
        value.onboardingCompleted = onboardingCompleted ?? value.onboardingCompleted
        value.language = language ?? value.language
        value.profiles = profiles ?? value.profiles
        value.activeProfileId = activeProfileId ?? value.activeProfileId
        value.engine = engine ?? value.engine
        value.funAsrModelId = funAsrModelId ?? value.funAsrModelId
        value.azureRegion = azureRegion ?? value.azureRegion
        value.azureAuthMode = azureAuthMode ?? value.azureAuthMode
        value.azureEndpoint = azureEndpoint ?? value.azureEndpoint
        value.azureTenantId = azureTenantId ?? value.azureTenantId
        value.azureResourceId = azureResourceId ?? value.azureResourceId
        value.transcribeEndpoint = transcribeEndpoint ?? value.transcribeEndpoint
        value.transcribeModel = transcribeModel ?? value.transcribeModel
        value.transcribeModelKind = transcribeModelKind ?? value.transcribeModelKind
        value.transcribeAuthMode = transcribeAuthMode ?? value.transcribeAuthMode
        value.transcribeTenantId = transcribeTenantId ?? value.transcribeTenantId
        value.recognitionVocabulary = recognitionVocabulary ?? value.recognitionVocabulary
        value.llmEnabled = llmEnabled ?? value.llmEnabled
        value.llmBaseUrl = llmBaseUrl ?? value.llmBaseUrl
        value.llmModel = llmModel ?? value.llmModel
        value.llmPrompt = llmPrompt ?? value.llmPrompt
        value.llmLearnedRules = llmLearnedRules ?? value.llmLearnedRules
        value.learnFromEdits = learnFromEdits ?? value.learnFromEdits
        value.diagnosticLogging = diagnosticLogging ?? value.diagnosticLogging
        value.useContext = useContext ?? value.useContext
        value.azureKey = KeychainStore.string("azure-key")
        value.transcribeApiKey = KeychainStore.string("transcribe-key")
        value.llmApiKey = KeychainStore.string("llm-key")
        value.normalize()
        return value
    }
}

struct SettingsStore {
    func load() -> AppSettings {
        do {
            let data = try Data(contentsOf: AppPaths.settings)
            return try JSONDecoder().decode(PersistedSettings.self, from: data).materialize()
        } catch {
            return AppSettings()
        }
    }

    func save(_ settings: AppSettings) throws {
        var normalized = settings
        normalized.normalize()
        try FileManager.default.createDirectory(at: AppPaths.support, withIntermediateDirectories: true)
        try KeychainStore.set(normalized.azureKey, for: "azure-key")
        try KeychainStore.set(normalized.transcribeApiKey, for: "transcribe-key")
        try KeychainStore.set(normalized.llmApiKey, for: "llm-key")
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let data = try encoder.encode(PersistedSettings(normalized))
        let temporary = AppPaths.settings.appendingPathExtension("tmp")
        try data.write(to: temporary, options: .atomic)
        if FileManager.default.fileExists(atPath: AppPaths.settings.path) {
            _ = try FileManager.default.replaceItemAt(AppPaths.settings, withItemAt: temporary)
        } else {
            try FileManager.default.moveItem(at: temporary, to: AppPaths.settings)
        }
    }
}

struct CorrectionPair: Codable, Equatable {
    let raw: String
    let refined: String
    let edited: String
}

final class CorrectionStore {
    private let maxEntries = 100
    private let maxTextLength = 1_000

    func load() -> [CorrectionPair] {
        guard let text = try? String(contentsOf: AppPaths.corrections, encoding: .utf8),
              let key = encryptionKey() else { return [] }
        return text.split(separator: "\n").compactMap { line in
            guard let combined = Data(base64Encoded: String(line)),
                  let box = try? AES.GCM.SealedBox(combined: combined),
                  let clear = try? AES.GCM.open(box, using: key) else { return nil }
            return try? JSONDecoder().decode(CorrectionPair.self, from: clear)
        }
    }

    func append(raw: String, refined: String, edited: String) {
        var pairs = load()
        pairs.append(CorrectionPair(raw: limit(raw), refined: limit(refined), edited: limit(edited)))
        save(Array(pairs.suffix(maxEntries)))
    }

    func clear() { try? FileManager.default.removeItem(at: AppPaths.corrections) }

    private func save(_ pairs: [CorrectionPair]) {
        guard let key = encryptionKey() else { return }
        do {
            try FileManager.default.createDirectory(at: AppPaths.support, withIntermediateDirectories: true)
            let lines = try pairs.map { pair -> String in
                let clear = try JSONEncoder().encode(pair)
                let sealed = try AES.GCM.seal(clear, using: key)
                return sealed.combined!.base64EncodedString()
            }
            try (lines.joined(separator: "\n") + "\n").write(
                to: AppPaths.corrections, atomically: true, encoding: .utf8)
        } catch { AppLog.write("correction save failed: \(error.localizedDescription)") }
    }

    private func encryptionKey() -> SymmetricKey? {
        if let data = KeychainStore.data("corrections-key"), data.count == 32 {
            return SymmetricKey(data: data)
        }
        let key = SymmetricKey(size: .bits256)
        let data = key.withUnsafeBytes { Data($0) }
        do { try KeychainStore.set(data, for: "corrections-key"); return key }
        catch { return nil }
    }

    private func limit(_ text: String) -> String { String(text.prefix(maxTextLength)) }
}

@MainActor
final class CorrectionTracker {
    private struct PendingCorrection {
        let id: UUID
        let raw: String
        let injected: String
        let target: InputTarget
        let expires: Date
        var snapshot: InjectedTextSnapshot?
    }

    private let store: CorrectionStore
    private var armed: PendingCorrection?

    init(store: CorrectionStore = CorrectionStore()) { self.store = store }

    func arm(raw: String, injected: String, target: InputTarget) {
        let id = UUID()
        armed = PendingCorrection(
            id: id,
            raw: raw,
            injected: injected,
            target: target,
            expires: Date().addingTimeInterval(120),
            snapshot: nil)

        // CGEvent.post is asynchronous. Waiting for the receiving app to apply all
        // Unicode chunks avoids recording a partially injected value as the baseline.
        Task { @MainActor [weak self] in
            for delay in [25, 50, 100, 200] {
                try? await Task.sleep(for: .milliseconds(delay))
                guard let self, self.armed?.id == id else { return }
                if let snapshot = AccessibilityReader.injectedTextSnapshot(
                    of: target, injected: injected)
                {
                    self.armed?.snapshot = snapshot
                    return
                }
            }
        }
    }

    func captureSubmittedEdit() {
        guard let value = armed else { return }
        armed = nil
        guard value.expires > Date(), AccessibilityReader.isCurrent(value.target),
              let snapshot = value.snapshot,
              let current = AccessibilityReader.focusedValue(of: value.target),
              let edited = CorrectionEditExtractor.editedInjection(
                from: snapshot, currentValue: current, injected: value.injected)
        else { return }
        store.append(raw: value.raw, refined: value.injected, edited: edited)
    }

    func pairs() -> [CorrectionPair] { store.load() }
    func clear() { store.clear() }
}

struct InjectedTextSnapshot: Equatable {
    let fieldValue: String
    let injectedUTF16Range: NSRange

    static func locate(fieldValue: String, selectedUTF16Range: NSRange,
                       injected: String) -> InjectedTextSnapshot? {
        let field = fieldValue as NSString
        let injectedLength = (injected as NSString).length
        guard injectedLength > 0,
              selectedUTF16Range.location >= 0,
              selectedUTF16Range.length >= 0,
              NSMaxRange(selectedUTF16Range) <= field.length else { return nil }

        var candidates: [NSRange] = []
        if selectedUTF16Range.length == injectedLength {
            candidates.append(selectedUTF16Range)
        }
        if selectedUTF16Range.length == 0,
           selectedUTF16Range.location >= injectedLength {
            candidates.append(NSRange(
                location: selectedUTF16Range.location - injectedLength,
                length: injectedLength))
        }

        guard let injectedRange = candidates.first(where: {
            field.substring(with: $0) == injected
        }) else { return nil }
        return InjectedTextSnapshot(
            fieldValue: fieldValue,
            injectedUTF16Range: injectedRange)
    }
}

enum CorrectionEditExtractor {
    static func editedInjection(from snapshot: InjectedTextSnapshot,
                                currentValue: String, injected: String) -> String? {
        let baseline = snapshot.fieldValue as NSString
        let injectedRange = snapshot.injectedUTF16Range
        guard injectedRange.location >= 0,
              injectedRange.length >= 0,
              NSMaxRange(injectedRange) <= baseline.length,
              baseline.substring(with: injectedRange) == injected else { return nil }

        let prefix = baseline.substring(
            with: NSRange(location: 0, length: injectedRange.location))
        let suffixStart = NSMaxRange(injectedRange)
        let suffix = baseline.substring(
            with: NSRange(location: suffixStart, length: baseline.length - suffixStart))
        let current = currentValue as NSString
        let prefixLength = (prefix as NSString).length
        let suffixLength = (suffix as NSString).length

        guard current.length >= prefixLength + suffixLength,
              currentValue.hasPrefix(prefix),
              currentValue.hasSuffix(suffix) else { return nil }

        let candidate = current.substring(with: NSRange(
            location: prefixLength,
            length: current.length - prefixLength - suffixLength))
        let edited = candidate.trimmingCharacters(in: .whitespacesAndNewlines)
        let original = injected.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !edited.isEmpty, edited != original else { return nil }
        return edited
    }
}

extension AccessibilityReader {
    static func injectedTextSnapshot(of target: InputTarget,
                                     injected: String) -> InjectedTextSnapshot? {
        guard isCurrent(target),
              let fieldValue = focusedValue(of: target),
              let selectedRange = selectedTextRange(of: target) else { return nil }
        return InjectedTextSnapshot.locate(
            fieldValue: fieldValue,
            selectedUTF16Range: selectedRange,
            injected: injected)
    }

    static func selectedTextRange(of target: InputTarget) -> NSRange? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(
            target.element, kAXSelectedTextRangeAttribute as CFString, &value) == .success,
            let value,
            CFGetTypeID(value) == AXValueGetTypeID() else { return nil }
        let axValue = unsafeBitCast(value, to: AXValue.self)
        guard AXValueGetType(axValue) == .cfRange else { return nil }
        var range = CFRange()
        guard AXValueGetValue(axValue, .cfRange, &range),
              range.location >= 0, range.length >= 0 else { return nil }
        return NSRange(location: range.location, length: range.length)
    }
}
