import ApplicationServices
import XCTest
@testable import VoiceInputMac

final class CoreTests: XCTestCase {
    func testAccessibilityInjectionHopsToMainActor() async {
        let system = AXUIElementCreateSystemWide()
        let target = InputTarget(
            processIdentifier: 0,
            application: system,
            window: nil,
            element: system)

        let result = await Task.detached(priority: .userInitiated) {
            await AccessibilityReader.inject("", into: target)
        }.value

        XCTAssertTrue(result.success)
        XCTAssertEqual(result.utf16UnitsInserted, 0)
        XCTAssertEqual(result.utf16UnitsSubmitted, 0)
        XCTAssertTrue(result.verified)
    }

    func testAccessibilityTextEntryClassificationHandlesChromiumProxiesSafely() {
        XCTAssertTrue(AccessibilityReader.classifiesAsTextEntry(AccessibilityElementTraits(
            enabled: true, editable: false, role: kAXTextAreaRole, roleDescription: nil,
            valueSettable: false, selectedTextSettable: false)))
        XCTAssertTrue(AccessibilityReader.classifiesAsTextEntry(AccessibilityElementTraits(
            enabled: true, editable: true, role: kAXGroupRole, roleDescription: nil,
            valueSettable: false, selectedTextSettable: false)))
        XCTAssertFalse(AccessibilityReader.classifiesAsTextEntry(AccessibilityElementTraits(
            enabled: true, editable: false, role: kAXSliderRole, roleDescription: nil,
            valueSettable: true, selectedTextSettable: false)))
        XCTAssertFalse(AccessibilityReader.classifiesAsTextEntry(AccessibilityElementTraits(
            enabled: true, editable: false, role: kAXComboBoxRole, roleDescription: nil,
            valueSettable: true, selectedTextSettable: false)))
        XCTAssertFalse(AccessibilityReader.classifiesAsTextEntry(AccessibilityElementTraits(
            enabled: false, editable: true, role: kAXTextFieldRole, roleDescription: nil,
            valueSettable: true, selectedTextSettable: true)))
        XCTAssertTrue(AccessibilityReader.classifiesAsEditorProxy(AccessibilityElementTraits(
            enabled: true, editable: nil, role: kAXGroupRole, roleDescription: "editor",
            valueSettable: true, selectedTextSettable: false)))
        XCTAssertFalse(AccessibilityReader.classifiesAsEditorProxy(AccessibilityElementTraits(
            enabled: true, editable: nil, role: kAXSliderRole, roleDescription: "slider",
            valueSettable: true, selectedTextSettable: false)))
    }

    func testUnicodeKeyboardEventsStayWithinChromiumScalarCapacity() {
        let text = "A中😀e\u{301}"
        let events = AccessibilityReader.unicodeEventUnits(text)
        XCTAssertEqual(events.map(\.count), [1, 1, 2, 1, 1])
        XCTAssertTrue(events.allSatisfy { (1...2).contains($0.count) })
        XCTAssertEqual(String(decoding: events.flatMap { $0 }, as: UTF16.self), text)
    }

    func testDeliveryObservationDistinguishesVerifiedUnchangedAndAmbiguousValues() {
        XCTAssertEqual(
            AccessibilityReader.classifyDelivery(
                before: "hello", expected: "hello world", current: "hello world"),
            .expected)
        XCTAssertEqual(
            AccessibilityReader.classifyDelivery(
                before: "hello", expected: "hello world", current: "hello"),
            .unchanged)
        XCTAssertEqual(
            AccessibilityReader.classifyDelivery(
                before: "hello", expected: "hello world", current: "hello  world"),
            .ambiguous)
        XCTAssertEqual(
            AccessibilityReader.classifyDelivery(
                before: "hello", expected: "hello world", current: nil),
            .ambiguous)
    }

    func testKeyboardDeliveryNormalizesControlKeysWithoutSubmittingChat() {
        XCTAssertEqual(
            AppController.normalizedTextForKeyboardDelivery("first\r\nsecond\tthird"),
            "first  second third")
        XCTAssertEqual(AppController.normalizedTextForKeyboardDelivery("中文😀"), "中文😀")
    }

    func testDefaultsNormalizeAndWaveHeader() {
        var settings = AppSettings()
        XCTAssertEqual(settings.funAsrModelId, FunAsrCatalog.qwen3AsrId)
        settings.profiles[1].name = " desktop "
        settings.normalize()
        XCTAssertEqual(settings.profiles.count, 2)
        XCTAssertNotEqual(settings.profiles[0].name.lowercased(), settings.profiles[1].name.lowercased())

        let wave = PcmWave.wrap(Data([0, 1, 2, 3]))
        XCTAssertEqual(String(data: wave.prefix(4), encoding: .ascii), "RIFF")
        XCTAssertEqual(wave.count, 48)
    }

    func testLegacySettingsKeepHistoricalSenseVoiceModel() {
        XCTAssertEqual(
            SettingsStore.restoredLocalModelId(nil),
            FunAsrCatalog.senseVoiceId)
        XCTAssertEqual(
            SettingsStore.restoredLocalModelId(FunAsrCatalog.senseVoiceId),
            FunAsrCatalog.senseVoiceId)
        XCTAssertEqual(
            SettingsStore.restoredLocalModelId(FunAsrCatalog.qwen3AsrId),
            FunAsrCatalog.qwen3AsrId)
    }

    func testSettingsStoreDistinguishesFreshAndLegacyModelDefaults() throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("settings-defaults-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let settingsURL = directory.appendingPathComponent("settings.json")
        let store = SettingsStore(settingsURL: settingsURL)

        XCTAssertEqual(store.load().funAsrModelId, FunAsrCatalog.qwen3AsrId)

        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try Data("{}".utf8).write(to: settingsURL)
        XCTAssertEqual(store.load().funAsrModelId, FunAsrCatalog.senseVoiceId)

        try Data("{\"funAsrModelId\":\"sensevoice-small-q8\"}".utf8).write(to: settingsURL)
        XCTAssertEqual(store.load().funAsrModelId, FunAsrCatalog.senseVoiceId)
    }

    func testPttRoutingMatchesHoldAndToggleContracts() {
        XCTAssertEqual(PttRouter.action(mode: .hold, gesture: .pressed, dictating: false, state: .idle), .start)
        XCTAssertEqual(PttRouter.action(mode: .hold, gesture: .cancelled, dictating: true, state: .listening), .cancel)
        XCTAssertEqual(PttRouter.action(mode: .toggle, gesture: .pressed, dictating: false, state: .idle), .none)
        XCTAssertEqual(PttRouter.action(mode: .toggle, gesture: .released, dictating: false, state: .idle), .start)
        XCTAssertEqual(PttRouter.action(mode: .toggle, gesture: .released, dictating: true, state: .listening), .stop)
        XCTAssertEqual(PttRouter.action(mode: .toggle, gesture: .recoveredRelease,
                                        dictating: false, state: .idle), .none)
        XCTAssertEqual(PttRouter.action(mode: .toggle, gesture: .recoveredRelease,
                                        dictating: true, state: .listening), .none)
        XCTAssertEqual(PttRouter.action(mode: .toggle, gesture: .released, dictating: false, state: .refining), .busy)
    }

    func testWindowsAppSuppressesOnlyNewLocalVoiceActivation() {
        for bundleIdentifier in ["com.microsoft.rdc.macos", "com.microsoft.rdc.macos.beta"] {
            XCTAssertTrue(VoiceActivationPolicy.shouldSuppress(
                action: .start, frontmostBundleIdentifier: bundleIdentifier))
            for action in [PttAction.stop, .cancel, .none, .busy] {
                XCTAssertFalse(VoiceActivationPolicy.shouldSuppress(
                    action: action, frontmostBundleIdentifier: bundleIdentifier))
            }
        }

        XCTAssertFalse(VoiceActivationPolicy.shouldSuppress(
            action: .start, frontmostBundleIdentifier: "com.microsoft.VSCode"))
        XCTAssertFalse(VoiceActivationPolicy.shouldSuppress(
            action: .start, frontmostBundleIdentifier: nil))
    }

    func testActivationCycleEmitsOnlyCleanReleaseForControlShortcut() {
        var clean = ActivationCycle()
        XCTAssertEqual(clean.consume(.down(startsChorded: false)), .pressed)
        XCTAssertEqual(clean.consume(.up), .released)

        var shortcut = ActivationCycle()
        XCTAssertEqual(shortcut.consume(.down(startsChorded: false)), .pressed)
        XCTAssertEqual(shortcut.consume(.otherKey), .cancelled)
        XCTAssertNil(shortcut.consume(.otherKey))
        XCTAssertNil(shortcut.consume(.up))
        XCTAssertFalse(shortcut.isDown)
        XCTAssertFalse(shortcut.isChorded)

        var heldBeforeActivation = ActivationCycle()
        XCTAssertEqual(heldBeforeActivation.consume(.down(startsChorded: true)), .cancelled)
        XCTAssertNil(heldBeforeActivation.consume(.up))
    }

    func testActivationCycleRecoveryIsFailClosedAfterChordAndIgnoresLateRelease() {
        var cleanMissedRelease = ActivationCycle()
        XCTAssertEqual(cleanMissedRelease.consume(.down(startsChorded: false)), .pressed)
        XCTAssertEqual(cleanMissedRelease.consume(.recoveredRelease), .recoveredRelease)
        XCTAssertNil(cleanMissedRelease.consume(.up))

        var chordedMissedRelease = ActivationCycle()
        XCTAssertEqual(chordedMissedRelease.consume(.down(startsChorded: false)), .pressed)
        XCTAssertEqual(chordedMissedRelease.consume(.otherKey), .cancelled)
        XCTAssertNil(chordedMissedRelease.consume(.recoveredRelease))
        XCTAssertNil(chordedMissedRelease.consume(.up))

        var duplicateDown = ActivationCycle()
        XCTAssertEqual(duplicateDown.consume(.down(startsChorded: false)), .pressed)
        XCTAssertNil(duplicateDown.consume(.down(startsChorded: false)))
        XCTAssertEqual(duplicateDown.consume(.up), .released)
    }

    func testRawModifierClassificationHandlesCapsLockAndFn() {
        XCTAssertTrue(KeyboardMonitor.capsLockEventIsPress(
            previousFlagState: false, eventFlagState: true))
        XCTAssertTrue(KeyboardMonitor.capsLockEventIsPress(
            previousFlagState: true, eventFlagState: false))
        XCTAssertFalse(KeyboardMonitor.capsLockEventIsPress(
            previousFlagState: true, eventFlagState: true))

        XCTAssertFalse(KeyboardMonitor.containsUnexpectedModifier(
            [.maskControl], activationKey: .leftControl))
        XCTAssertTrue(KeyboardMonitor.containsUnexpectedModifier(
            [.maskControl, .maskSecondaryFn], activationKey: .leftControl))
        XCTAssertFalse(KeyboardMonitor.containsUnexpectedModifier(
            [.maskSecondaryFn], activationKey: .fn))
    }

    func testProfileShortcutLatchRecoversMissedKeyUp() {
        var latch = KeyPressLatch()
        XCTAssertTrue(latch.begin())
        XCTAssertFalse(latch.begin())
        latch.reconcile(physicalKeyDown: true)
        XCTAssertTrue(latch.isDown)
        latch.reconcile(physicalKeyDown: false)
        XCTAssertFalse(latch.isDown)
        XCTAssertTrue(latch.begin())
        latch.end()
        XCTAssertFalse(latch.isDown)
    }

    func testMouseButtonCycleTracksButtonsWithoutTreatingMovementOrScrollAsClicks() {
        var mouse = MouseButtonCycle()

        XCTAssertFalse(mouse.consume(type: .mouseMoved, buttonNumber: 0))
        XCTAssertFalse(mouse.consume(type: .scrollWheel, buttonNumber: 0))
        XCTAssertFalse(mouse.hasPressedButton)

        XCTAssertTrue(mouse.consume(type: .leftMouseDown, buttonNumber: 0))
        XCTAssertTrue(mouse.hasPressedButton)
        XCTAssertTrue(mouse.consume(type: .otherMouseDown, buttonNumber: 3))
        XCTAssertFalse(mouse.consume(type: .leftMouseUp, buttonNumber: 0))
        XCTAssertTrue(mouse.hasPressedButton)
        XCTAssertFalse(mouse.consume(type: .otherMouseUp, buttonNumber: 3))
        XCTAssertFalse(mouse.hasPressedButton)
    }

    func testMouseClickChordsHeldActivationOnceAndSuppressesRelease() {
        var activation = ActivationCycle()
        XCTAssertEqual(activation.consume(.down(startsChorded: false)), .pressed)

        var mouse = MouseButtonCycle()
        XCTAssertTrue(mouse.consume(type: .leftMouseDown, buttonNumber: 0))
        XCTAssertEqual(activation.consume(.otherKey), .cancelled)
        XCTAssertTrue(mouse.consume(type: .rightMouseDown, buttonNumber: 1))
        XCTAssertNil(activation.consume(.otherKey))
        XCTAssertNil(activation.consume(.up))
    }

    func testMouseButtonHeldBeforeActivationStartsChorded() {
        var mouse = MouseButtonCycle()
        mouse.reconcile(pressedButtonMask: 1 << 2)

        var activation = ActivationCycle()
        XCTAssertEqual(
            activation.consume(.down(startsChorded: mouse.hasPressedButton)), .cancelled)
        XCTAssertNil(activation.consume(.up))
    }

    func testEscapeKeyCycleEmitsOnceUntilReleaseAndRecoversMissedKeyUp() {
        var escape = EscapeKeyCycle()
        let idleActivation = ActivationCycle()

        XCTAssertEqual(escape.keyDown(activationCycle: idleActivation), .cancelSession)
        XCTAssertNil(escape.keyDown(activationCycle: idleActivation))
        escape.reconcile(physicalKeyDown: true)
        XCTAssertTrue(escape.isDown)
        escape.reconcile(physicalKeyDown: false)
        XCTAssertFalse(escape.isDown)
        XCTAssertEqual(escape.keyDown(activationCycle: idleActivation), .cancelSession)
        escape.keyUp()
        XCTAssertFalse(escape.isDown)
    }

    func testEscapeReplacesHeldActivationChordCancellationWithoutDoubleEmission() {
        var activation = ActivationCycle()
        XCTAssertEqual(activation.consume(.down(startsChorded: false)), .pressed)

        var escape = EscapeKeyCycle()
        XCTAssertEqual(
            escape.keyDown(activationCycle: activation), .replaceChordCancellation)
        XCTAssertEqual(activation.consume(.otherKey), .cancelled)
        XCTAssertNil(escape.keyDown(activationCycle: activation))
        XCTAssertNil(activation.consume(.otherKey))

        var alreadyChordedEscape = EscapeKeyCycle()
        XCTAssertEqual(
            alreadyChordedEscape.keyDown(activationCycle: activation), .cancelSession)
    }

    func testEscapeCancelsCaptureButNotProcessingOrIdle() {
        for state in [DictationState.starting, .listening] {
            XCTAssertTrue(state.canCancelWithEscape, "Expected Escape to cancel \(state.rawValue)")
        }
        for state in [
            DictationState.idle, .transcribing, .refining, .injecting, .failed, .cancelled,
        ] {
            XCTAssertFalse(state.canCancelWithEscape, "Expected Escape to ignore \(state.rawValue)")
        }
    }

    func testVocabularyAndRefinementGuard() {
        XCTAssertEqual(RecognitionVocabulary.parse("Codex，codex; FunASR\n 语音 "), ["Codex", "FunASR", "语音"])
        XCTAssertTrue(RefinementGuard.isSafe(original: "please open voice input settings",
                                             refined: "Please open VoiceInput settings."))
        XCTAssertFalse(RefinementGuard.isSafe(original: "短句", refined: String(repeating: "扩", count: 100)))
    }

    func testUpdateVersionAndDigestParsing() {
        XCTAssertEqual(UpdateService.parseVersion("v0.2.14"), [0, 2, 14])
        XCTAssertEqual(UpdateService.compare([1, 2], [1, 2, 0]), .orderedSame)
        XCTAssertEqual(UpdateService.compare([1, 3], [1, 2, 9]), .orderedDescending)
        XCTAssertNil(UpdateService.parseDigest("md5:abcd"))
        XCTAssertEqual(UpdateService.parseDigest("sha256:" + String(repeating: "A", count: 64)),
                       String(repeating: "a", count: 64))
    }

    func testUpdateViewStateReportsProgressAndLifecycleCopy() {
        let downloading = AppUpdateViewState.downloading(
            version: "v0.3.0",
            receivedBytes: 25,
            totalBytes: 100)
        XCTAssertEqual(downloading.progressFraction, 0.25)
        XCTAssertTrue(downloading.statusText.contains("25%"))
        XCTAssertTrue(downloading.isBusy)

        XCTAssertTrue(AppUpdateViewState.verifying(version: "v0.3.0").statusText.contains("Verifying"))
        XCTAssertTrue(AppUpdateViewState.preparing(version: "v0.3.0").statusText.contains("Preparing"))
        XCTAssertTrue(AppUpdateViewState.restarting(version: "v0.3.0").statusText.contains("restart"))

        let failed = AppUpdateViewState.failed(
            message: "The connection was lost.",
            retryVersion: "v0.3.0")
        XCTAssertTrue(failed.isFailure)
        XCTAssertFalse(failed.isBusy)
        XCTAssertEqual(failed.retryVersion, "v0.3.0")
        XCTAssertTrue(failed.statusText.contains("The connection was lost."))
    }

    @MainActor
    func testUpdateDownloadMovesTemporaryFileAndReportsLifecycle() async {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [UpdateStubURLProtocol.self]
        let session = URLSession(configuration: configuration)
        defer { session.invalidateAndCancel() }
        let service = UpdateService(session: session)
        let update = AvailableUpdate(
            tag: "v99.0.0",
            version: [99, 0, 0],
            assetURL: URL(string: "https://updates.example/gujiguji-mac.zip")!,
            sha256: String(repeating: "0", count: 64))
        var progress: [UpdateInstallProgress] = []

        do {
            _ = try await service.stageAndApply(update) { progress.append($0) }
            XCTFail("A deliberately incorrect digest should reject the fixture.")
        } catch {
            XCTAssertTrue(error.localizedDescription.contains("SHA-256"))
        }

        XCTAssertTrue(progress.contains { state in
            guard case .downloading(let receivedBytes, _) = state else { return false }
            return receivedBytes == Int64(UpdateStubURLProtocol.payload.count)
        })
        XCTAssertTrue(progress.contains(.verifying))
        XCTAssertFalse(progress.contains(.preparing))
    }

    func testEndpointValidation() {
        XCTAssertTrue(SettingsValidation.isHTTPS("https://example.test/path"))
        XCTAssertFalse(SettingsValidation.isHTTPS("http://example.test"))
        XCTAssertTrue(SettingsValidation.isHTTPSOrLoopbackHTTP("http://127.0.0.1:11434/v1"))
        XCTAssertFalse(SettingsValidation.isHTTPSOrLoopbackHTTP("http://192.168.1.8/v1"))
    }
}

private final class UpdateStubURLProtocol: URLProtocol {
    static let payload = Data(repeating: 0x5a, count: 2_100_000)

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        guard let url = request.url,
              let response = HTTPURLResponse(
                  url: url,
                  statusCode: 200,
                  httpVersion: "HTTP/1.1",
                  headerFields: ["Content-Length": String(Self.payload.count)]) else {
            client?.urlProtocol(self, didFailWithError: URLError(.badServerResponse))
            return
        }
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Self.payload)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}
