import Foundation
import XCTest
@testable import VoiceInputMac

final class Qwen3AsrTests: XCTestCase {
    func testPCM16LittleEndianConversion() {
        let pcm = Data([0x00, 0x00, 0xff, 0x7f, 0x00, 0x80, 0x00, 0x01])
        let values = Qwen3AsrRuntimeManager.floatPCM(from: pcm)

        XCTAssertEqual(values.count, 4)
        XCTAssertEqual(values[0], 0, accuracy: 0.000_01)
        XCTAssertEqual(values[1], Float(Int16.max) / 32_768, accuracy: 0.000_01)
        XCTAssertEqual(values[2], -1, accuracy: 0.000_01)
        XCTAssertEqual(values[3], 256 / 32_768, accuracy: 0.000_01)
    }

    func testEngineBuffersAudioAndEmitsOneFinalResult() async throws {
        for modelId in [FunAsrCatalog.qwen3AsrId, FunAsrCatalog.qwen3Asr17Id] {
            let recognizer = FakeQwenRecognizer(
                expectedModelId: modelId, result: "  自动识别完成  ")
            let model = try FunAsrCatalog.model(modelId)
            let engine = Qwen3AsrEngine(model: model, recognizer: recognizer)
            var final = ""
            var partialCount = 0
            engine.onFinal = { final = $0 }
            engine.onPartial = { _ in partialCount += 1 }

            try await engine.start(language: "zh-CN")
            let chunk = audiblePCM(sampleCount: 8_000)
            engine.feed(chunk.prefix(7_000))
            engine.feed(chunk.dropFirst(7_000))
            await engine.stop()

            XCTAssertEqual(recognizer.recordings, [chunk])
            XCTAssertEqual(final, "自动识别完成")
            XCTAssertEqual(partialCount, 0)
            XCTAssertFalse(engine.hasInterimResults)
        }
    }

    func testEngineSkipsShortAudio() async throws {
        let recognizer = FakeQwenRecognizer(result: "should not run")
        let model = try FunAsrCatalog.model(FunAsrCatalog.qwen3AsrId)
        let engine = Qwen3AsrEngine(model: model, recognizer: recognizer)

        try await engine.start(language: "en-US")
        engine.feed(audiblePCM(sampleCount: 100))
        await engine.stop()

        XCTAssertTrue(recognizer.recordings.isEmpty)
    }

    func testRemoveDeletesOnlySelectedManagedQwenArtifact() async throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("qwen-tests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let manager = Qwen3AsrRuntimeManager(root: root)
        for modelId in [FunAsrCatalog.qwen3AsrId, FunAsrCatalog.qwen3Asr17Id] {
            let model = try FunAsrCatalog.model(modelId)
            let artifact = try XCTUnwrap(model.artifacts.first)
            let file = root.appendingPathComponent(artifact.relativePath)
            try FileManager.default.createDirectory(
                at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
            XCTAssertTrue(FileManager.default.createFile(atPath: file.path, contents: nil))
            let handle = try FileHandle(forWritingTo: file)
            try handle.truncate(atOffset: UInt64(artifact.size))
            try handle.close()

            XCTAssertTrue(manager.hasInstalledFiles(model.id))
            try await manager.remove(model.id)
            XCTAssertFalse(FileManager.default.fileExists(atPath: file.path))
        }
    }

    private func audiblePCM(sampleCount: Int) -> Data {
        Data((0..<sampleCount).flatMap { index -> [UInt8] in
            let value = index.isMultiple(of: 2) ? Int16(12_000) : Int16(-12_000)
            let bits = UInt16(bitPattern: value)
            return [UInt8(bits & 0xff), UInt8(bits >> 8)]
        })
    }
}

private final class FakeQwenRecognizer: Qwen3AsrTranscribing, @unchecked Sendable {
    private(set) var recordings: [Data] = []
    private let expectedModelId: String
    private let result: String

    init(expectedModelId: String = FunAsrCatalog.qwen3AsrId, result: String) {
        self.expectedModelId = expectedModelId
        self.result = result
    }

    func transcribe(
        model: FunAsrModel,
        pcm16kMono: Data,
        cancellation: Qwen3AsrCancellation
    ) async throws -> String {
        XCTAssertEqual(model.id, expectedModelId)
        XCTAssertFalse(cancellation.isCancelled)
        recordings.append(pcm16kMono)
        return result.trimmingCharacters(in: .whitespacesAndNewlines)
    }
}
