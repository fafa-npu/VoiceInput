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
        settings.profiles[1].name = " desktop "
        settings.normalize()
        XCTAssertEqual(settings.profiles.count, 2)
        XCTAssertNotEqual(settings.profiles[0].name.lowercased(), settings.profiles[1].name.lowercased())

        let wave = PcmWave.wrap(Data([0, 1, 2, 3]))
        XCTAssertEqual(String(data: wave.prefix(4), encoding: .ascii), "RIFF")
        XCTAssertEqual(wave.count, 48)
    }

    func testPttRoutingMatchesHoldAndToggleContracts() {
        XCTAssertEqual(PttRouter.action(mode: .hold, gesture: .pressed, dictating: false, state: .idle), .start)
        XCTAssertEqual(PttRouter.action(mode: .hold, gesture: .cancelled, dictating: true, state: .listening), .cancel)
        XCTAssertEqual(PttRouter.action(mode: .toggle, gesture: .pressed, dictating: false, state: .idle), .none)
        XCTAssertEqual(PttRouter.action(mode: .toggle, gesture: .released, dictating: true, state: .listening), .stop)
        XCTAssertEqual(PttRouter.action(mode: .toggle, gesture: .recoveredRelease,
                                        dictating: false, state: .idle), .none)
        XCTAssertEqual(PttRouter.action(mode: .toggle, gesture: .recoveredRelease,
                                        dictating: true, state: .listening), .none)
        XCTAssertEqual(PttRouter.action(mode: .toggle, gesture: .released, dictating: false, state: .refining), .busy)
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

    func testEndpointValidation() {
        XCTAssertTrue(SettingsValidation.isHTTPS("https://example.test/path"))
        XCTAssertFalse(SettingsValidation.isHTTPS("http://example.test"))
        XCTAssertTrue(SettingsValidation.isHTTPSOrLoopbackHTTP("http://127.0.0.1:11434/v1"))
        XCTAssertFalse(SettingsValidation.isHTTPSOrLoopbackHTTP("http://192.168.1.8/v1"))
    }
}
