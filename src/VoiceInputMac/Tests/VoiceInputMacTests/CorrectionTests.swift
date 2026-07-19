import Foundation
import XCTest
@testable import VoiceInputMac

final class CorrectionTests: XCTestCase {
    func testLocatesInjectedUTF16RangeImmediatelyBeforeCaret() {
        let prefix = "已有内容："
        let injected = "你好 🎙️"
        let field = prefix + injected + " 后文"
        let caret = (prefix as NSString).length + (injected as NSString).length

        let snapshot = InjectedTextSnapshot.locate(
            fieldValue: field,
            selectedUTF16Range: NSRange(location: caret, length: 0),
            injected: injected)

        XCTAssertEqual(snapshot?.fieldValue, field)
        XCTAssertEqual(snapshot?.injectedUTF16Range, NSRange(
            location: (prefix as NSString).length,
            length: (injected as NSString).length))
    }

    func testLocatesInjectedRangeWhenControlSelectsInsertedText() {
        let field = "prefix dictated suffix"
        let selected = NSRange(location: 7, length: 8)

        let snapshot = InjectedTextSnapshot.locate(
            fieldValue: field,
            selectedUTF16Range: selected,
            injected: "dictated")

        XCTAssertEqual(snapshot?.injectedUTF16Range, selected)
    }

    func testUneditedInjectionInNonemptyFieldIsNotCorrection() {
        let snapshot = InjectedTextSnapshot(
            fieldValue: "pre-existing dictated text",
            injectedUTF16Range: NSRange(location: 13, length: 8))

        XCTAssertNil(CorrectionEditExtractor.editedInjection(
            from: snapshot,
            currentValue: "pre-existing dictated text",
            injected: "dictated"))
    }

    func testExtractsOnlyEditedInjectedTextBetweenStableAnchors() {
        let snapshot = InjectedTextSnapshot(
            fieldValue: "before Voice Input after",
            injectedUTF16Range: NSRange(location: 7, length: 11))

        XCTAssertEqual(CorrectionEditExtractor.editedInjection(
            from: snapshot,
            currentValue: "before VoiceInput after",
            injected: "Voice Input"), "VoiceInput")
    }

    func testRejectsEditOutsideInjectedRange() {
        let snapshot = InjectedTextSnapshot(
            fieldValue: "before dictated after",
            injectedUTF16Range: NSRange(location: 7, length: 8))

        XCTAssertNil(CorrectionEditExtractor.editedInjection(
            from: snapshot,
            currentValue: "BEFORE dictated after",
            injected: "dictated"))
        XCTAssertNil(CorrectionEditExtractor.editedInjection(
            from: snapshot,
            currentValue: "before dictated AFTER",
            injected: "dictated"))
    }

    func testRejectsSnapshotWithoutInjectedTextAtRecordedRange() {
        let snapshot = InjectedTextSnapshot(
            fieldValue: "before other after",
            injectedUTF16Range: NSRange(location: 7, length: 5))

        XCTAssertNil(CorrectionEditExtractor.editedInjection(
            from: snapshot,
            currentValue: "before edited after",
            injected: "dictated"))
        XCTAssertNil(InjectedTextSnapshot.locate(
            fieldValue: "before partial",
            selectedUTF16Range: NSRange(location: 14, length: 0),
            injected: "dictated"))
    }
}
