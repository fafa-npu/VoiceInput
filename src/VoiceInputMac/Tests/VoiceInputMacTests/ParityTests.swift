import AppKit
import XCTest
@testable import VoiceInputMac

final class ParityTests: XCTestCase {
    func testTranscriptJoinerHandlesLatinCJKAndPunctuation() {
        XCTAssertEqual(TranscriptJoiner.join(["hello", "world", "."]), "hello world.")
        XCTAssertEqual(TranscriptJoiner.join(["你好", "世界", "。"]), "你好世界。")
        XCTAssertEqual(TranscriptJoiner.join(["用", "VoiceInput", "输入"]), "用VoiceInput输入")
    }

    func testFunAsrCatalogIsPinnedAndLanguageAware() throws {
        XCTAssertEqual(FunAsrCatalog.defaultId, "qwen3-asr-0.6b-q8")
        XCTAssertNotEqual(FunAsrCatalog.defaultId, FunAsrCatalog.qwen3Asr17Id)
        XCTAssertEqual(FunAsrCatalog.runtimeVersion, "v0.1.4")
        XCTAssertEqual(FunAsrCatalog.runtime.size, 6_816_662)
        XCTAssertEqual(FunAsrCatalog.runtime.sha256,
                       "010416baa6932c7ce67fda50eb421a65e8ae6fd248f06f8d2f7ec17d15ef2cba")
        XCTAssertEqual(FunAsrCatalog.models.map(\.id), [
            "sensevoice-small-q8", "paraformer-zh-q8", "fun-asr-nano-q4",
            "qwen3-asr-0.6b-q8", "qwen3-asr-1.7b-q5km",
        ])
        XCTAssertEqual(LocalModelViewState.defaults.map(\.id), FunAsrCatalog.models.map(\.id))
        XCTAssertTrue(try FunAsrCatalog.model(FunAsrCatalog.defaultId).supports("ko-KR"))
        XCTAssertTrue(try FunAsrCatalog.model(FunAsrCatalog.defaultId).supports("vi-VN"))
        XCTAssertFalse(try FunAsrCatalog.model(FunAsrCatalog.senseVoiceId).supports("vi-VN"))
        XCTAssertEqual(
            LocalModelViewState.defaults.filter(\.isRecommended).map(\.id),
            [FunAsrCatalog.qwen3AsrId])
        let qwen = try FunAsrCatalog.model(FunAsrCatalog.qwen3AsrId)
        XCTAssertEqual(qwen.runner, .qwen3Asr)
        XCTAssertTrue(qwen.supports("vi-VN"))
        XCTAssertEqual(qwen.artifacts.count, 1)
        XCTAssertEqual(qwen.artifacts.first?.size, 850_423_456)
        XCTAssertEqual(qwen.artifacts.first?.sha256,
                       "f081b2d5e23bd669d92cc331d722a8a0681943b8e6f34b48996fd5c319b5acd8")
        let qwen17 = try FunAsrCatalog.model(FunAsrCatalog.qwen3Asr17Id)
        XCTAssertEqual(qwen17.runner, .qwen3Asr)
        XCTAssertTrue(qwen17.supports("vi-VN"))
        XCTAssertEqual(qwen17.artifacts.count, 1)
        XCTAssertEqual(qwen17.artifacts.first?.size, 1_517_290_464)
        XCTAssertEqual(qwen17.artifacts.first?.sha256,
                       "034c557fe92ff8fcd9a9c041cbdaad347be0a86a58d3a348f63cf3f0180879d0")
        XCTAssertEqual(qwen17.artifacts.first?.url.absoluteString,
                       "https://huggingface.co/handy-computer/Qwen3-ASR-1.7B-gguf/resolve/92282af1610a2db19d66f2bef1e260f5deca782d/Qwen3-ASR-1.7B-Q5_K_M.gguf")
        for artifact in [FunAsrCatalog.runtime, FunAsrCatalog.vad]
            + FunAsrCatalog.models.flatMap(\.artifacts) {
            XCTAssertTrue(artifact.url.absoluteString.hasPrefix("https://"))
            XCTAssertEqual(artifact.sha256.count, 64)
            XCTAssertGreaterThan(artifact.size, 0)
        }
    }

    func testActivationKeysAndProfilesRemainDistinct() {
        XCTAssertEqual(Set(ActivationKey.allCases.map(\.keyCode)).count, ActivationKey.allCases.count)
        XCTAssertEqual(InputProfile.defaults.count, 2)
        XCTAssertEqual(InputProfile.defaults[0].pttMode, .hold)
        XCTAssertEqual(InputProfile.defaults[0].overlayPosition, .bottom)
        XCTAssertEqual(InputProfile.defaults[1].pttMode, .toggle)
        XCTAssertEqual(InputProfile.defaults[1].overlayPosition, .top)
    }

    func testWaveHeaderContainsExpectedPCMFormat() {
        let wave = PcmWave.wrap(Data(repeating: 0, count: 32))
        XCTAssertEqual(String(data: wave[8..<12], encoding: .ascii), "WAVE")
        XCTAssertEqual(readUInt16(wave, at: 20), 1)
        XCTAssertEqual(readUInt16(wave, at: 22), 1)
        XCTAssertEqual(readUInt32(wave, at: 24), 16_000)
        XCTAssertEqual(readUInt16(wave, at: 34), 16)
        XCTAssertEqual(readUInt32(wave, at: 40), 32)
    }

    func testSettingsValidationCoversCloudAndProfiles() {
        var settings = AppSettings()
        settings.engine = .azure
        settings.azureAuthMode = .key
        settings.azureKey = ""
        XCTAssertNotNil(SettingsValidation.error(in: settings))
        settings.azureKey = "secret"
        settings.azureRegion = "eastasia"
        XCTAssertNil(SettingsValidation.error(in: settings))
        settings.azureAuthMode = .entraId
        settings.azureEndpoint = "https://example.cognitiveservices.azure.com/"
        XCTAssertNotNil(SettingsValidation.error(in: settings))
        settings.azureResourceId = "/subscriptions/000/resourceGroups/voice/providers/Microsoft.CognitiveServices/accounts/speech"
        XCTAssertNil(SettingsValidation.error(in: settings))
        settings.profiles[1].name = settings.profiles[0].name
        XCTAssertNotNil(SettingsValidation.error(in: settings))
        settings.profiles[1].name = String(repeating: "x", count: 25)
        XCTAssertEqual(SettingsValidation.error(in: settings), "Profile names cannot exceed 24 characters.")
    }

    func testAzurePhraseListLimitMatchesWindowsContract() {
        XCTAssertEqual(AzureSpeechEngine.maxVocabularyPhrases, 500)
        XCTAssertEqual(
            AzureSpeechEngine.entraAuthorizationToken(resourceId: " /subscriptions/example ", accessToken: "token"),
            "aad#/subscriptions/example#token")
    }

    private func readUInt16(_ data: Data, at offset: Int) -> UInt16 {
        UInt16(data[offset]) | UInt16(data[offset + 1]) << 8
    }

    private func readUInt32(_ data: Data, at offset: Int) -> UInt32 {
        UInt32(data[offset]) | UInt32(data[offset + 1]) << 8
            | UInt32(data[offset + 2]) << 16 | UInt32(data[offset + 3]) << 24
    }
}

final class UIContractTests: XCTestCase {
    func testSettingsLayoutAtDefaultAndMinimumWindowSizes() async {
        await MainActor.run {
            _ = NSApplication.shared
            let longStatus = "Installing Fun-ASR Nano · 100%. Verifying downloaded runtime and selected model package"
            let controller = SettingsWindowController(
                settings: AppSettings(),
                runtime: SettingsRuntimeState(statusMessage: longStatus),
                actions: SettingsViewActions(save: { _, _ in nil }))
            guard let window = controller.window, let root = window.contentView else {
                XCTFail("Settings window should have a content view.")
                return
            }

            let pageHost = settingsPageHost(in: root)
            let navigation = settingsNavigationButtons(in: root)
            XCTAssertNotNil(pageHost)
            XCTAssertEqual(navigation.count, 6)

            let sizes: [(name: String, frame: NSSize)] = [
                ("default", window.frame.size),
                ("minimum", window.minSize),
            ]
            for size in sizes {
                window.setFrame(NSRect(origin: window.frame.origin, size: size.frame), display: false)
                root.layoutSubtreeIfNeeded()

                for button in navigation {
                    button.performClick(nil)
                    root.layoutSubtreeIfNeeded()
                    guard let host = pageHost,
                          let page = visibleSettingsPage(in: host),
                          let document = page.documentView,
                          let content = document.subviews.first as? NSStackView else {
                        XCTFail("\(size.name): \(button.title) should expose a scroll document and content stack.")
                        continue
                    }

                    XCTAssertFalse(
                        document.hasAmbiguousLayout,
                        "\(size.name): \(button.title) document layout is ambiguous.")
                    XCTAssertFalse(
                        content.hasAmbiguousLayout,
                        "\(size.name): \(button.title) content layout is ambiguous.")
                    assertMajorSettingsRowsFillWidth(
                        in: content,
                        pageName: "\(size.name): \(button.title)")

                    if button.tag == 0, let first = content.arrangedSubviews.first {
                        let pageRect = page.convert(page.bounds, to: root)
                        let firstRect = first.convert(first.bounds, to: root)
                        XCTAssertEqual(
                            firstRect.maxY,
                            pageRect.maxY,
                            accuracy: 12,
                            "\(size.name): Overview should begin at the top of its viewport.")
                        assertOverviewCardHeights(in: root, sizeName: size.name)
                        XCTAssertLessThanOrEqual(
                            content.frame.height,
                            360,
                            "\(size.name): Overview content should keep its intrinsic height instead of filling the viewport.")
                    }
                }

                guard let status = settingsStatusLabel(in: root),
                      let firstFooterButton = settingsFooterButtons(in: root).min(by: {
                          $0.convert($0.bounds, to: root).minX < $1.convert($1.bounds, to: root).minX
                      }) else {
                    XCTFail("\(size.name): Settings footer controls should exist.")
                    continue
                }
                let statusRect = status.convert(status.bounds, to: root)
                let buttonRect = firstFooterButton.convert(firstFooterButton.bounds, to: root)
                XCTAssertLessThanOrEqual(
                    statusRect.maxX,
                    buttonRect.minX - 6,
                    "\(size.name): Footer status must not overlap the action buttons.")
            }

            controller.close()
        }
    }

    func testWindowDimensionsAndOverlayStates() async {
        await MainActor.run {
            _ = NSApplication.shared
            let settings = SettingsWindowController(
                settings: AppSettings(),
                actions: SettingsViewActions(save: { _, _ in nil }))
            XCTAssertEqual(settings.window?.contentView?.bounds.size, NSSize(width: 900, height: 650))
            XCTAssertEqual(settings.window?.minSize, NSSize(width: 720, height: 520))

            let onboarding = OnboardingWindowController(
                profile: InputProfile.defaults[0],
                actions: OnboardingViewActions(complete: { _, _ in true }))
            XCTAssertEqual(onboarding.window?.contentView?.bounds.size, NSSize(width: 900, height: 650))
            XCTAssertEqual(onboarding.window?.minSize, NSSize(width: 720, height: 560))

            var onboardingViews: [String: NSView] = [:]
            var pendingViews = onboarding.window?.contentView.map { [$0] } ?? []
            while let view = pendingViews.popLast() {
                if let identifier = view.identifier?.rawValue {
                    onboardingViews[identifier] = view
                }
                pendingViews.append(contentsOf: view.subviews)
            }

            XCTAssertNotNil(onboardingViews["onboarding.practice.region"])
            XCTAssertNotNil(onboardingViews["onboarding.practice.steps"])
            XCTAssertNotNil(onboardingViews["onboarding.practice.notepad"])
            XCTAssertNotNil(onboardingViews["onboarding.practice.notepad.editor"])
            XCTAssertNotNil(onboardingViews["onboarding.practice.keycap"])
            XCTAssertNotNil(onboardingViews["onboarding.practice.status"])
            XCTAssertNotNil(onboardingViews["onboarding.practice.bars"])
            let onboardingText = onboarding.window?.contentView.map {
                allViews(in: $0).compactMap { ($0 as? NSTextField)?.stringValue }
            } ?? []
            XCTAssertTrue(onboardingText.contains("使用本地 Qwen3-ASR（推荐）"))
            XCTAssertTrue(onboardingText.contains {
                $0.contains("下载 Qwen3-ASR 0.6B")
            })
            XCTAssertEqual(onboardingViews["onboarding.practice.step.1"]?.toolTip, "步骤 1，当前步骤")
            XCTAssertEqual(onboardingViews["onboarding.practice.step.2"]?.toolTip, "步骤 2，未开始")

            onboarding.updatePractice(status: "正在聆听", detail: "说完后松开。", listening: true)
            XCTAssertEqual(onboardingViews["onboarding.practice.step.1"]?.toolTip, "步骤 1，已完成")
            XCTAssertEqual(onboardingViews["onboarding.practice.step.2"]?.toolTip, "步骤 2，当前步骤")
            XCTAssertTrue(onboardingViews["onboarding.practice.keycap"]?.toolTip?.contains("正在按住") == true)
            XCTAssertFalse(onboardingViews["onboarding.practice.bars"]?.isHidden ?? true)

            onboarding.updatePractice(status: "正在处理", detail: "正在识别。", listening: false)
            XCTAssertEqual(onboardingViews["onboarding.practice.step.2"]?.toolTip, "步骤 2，已完成")
            XCTAssertEqual(onboardingViews["onboarding.practice.step.3"]?.toolTip, "步骤 3，当前步骤")
            XCTAssertTrue(onboardingViews["onboarding.practice.bars"]?.isHidden ?? false)

            var toggleProfile = InputProfile.defaults[1]
            toggleProfile.activationKey = .leftControl
            onboarding.updateState(OnboardingViewState(), profile: toggleProfile)
            XCTAssertTrue(onboardingViews["onboarding.practice.keycap"]?.toolTip?.contains("Left Control") == true)
            let toggleTitle = onboardingViews["onboarding.practice.step.2.title"] as? NSTextField
            XCTAssertEqual(toggleTitle?.stringValue, "按一下 Left Control 开始")

            let overlay = OverlayWindowController()
            XCTAssertEqual(overlay.window?.frame.size, NSSize(width: 820, height: 150))
            overlay.show(.listening("Listening…"), position: .bottom)
            overlay.update(.partial("你好，这是一个语音识别测试。"))
            overlay.update(.transcribing)
            overlay.update(.refining)
            overlay.show(.failure("Click an editable text field first"), position: .bottom)
            overlay.show(.profile(name: "Desktop", activation: "Right Control · hold to talk"),
                         position: .top)
            overlay.hideAnimated()
            settings.close()
            onboarding.close()
            overlay.close()
        }
    }

    func testLocalModelActionsRespectActiveAndInstallingState() async {
        await MainActor.run {
            var models = LocalModelViewState.defaults
            models[0].isInstalled = true
            models[1].isInstalled = true
            let installingRuntime = SettingsRuntimeState(
                localModels: models,
                installingModelId: FunAsrCatalog.qwen3AsrId,
                installationStatus: "Downloading")
            let installingController = SettingsWindowController(
                settings: AppSettings(), runtime: installingRuntime,
                actions: SettingsViewActions(save: { _, _ in nil }))
            let installingButtons = installingController.window?.contentView
                .map { allViews(in: $0).compactMap { $0 as? NSButton } } ?? []
            let modelIds = Set(LocalModelViewState.defaults.map(\.id))
            let otherModelActions = installingButtons.filter {
                guard let id = $0.identifier?.rawValue, modelIds.contains(id) else { return false }
                return ["Download", "Use after Save", "Remove"].contains($0.title)
            }
            XCTAssertFalse(otherModelActions.isEmpty)
            XCTAssertTrue(otherModelActions.allSatisfy { !$0.isEnabled })
            XCTAssertTrue(installingButtons.contains { $0.title == "Cancel" && $0.isEnabled })
            XCTAssertFalse(installingButtons.contains {
                $0.title == "Remove" && $0.identifier?.rawValue == FunAsrCatalog.defaultId
            })
            installingController.close()

            var qwenSettings = AppSettings()
            qwenSettings.engine = .funAsr
            qwenSettings.funAsrModelId = FunAsrCatalog.qwen3AsrId
            var qwenModels = LocalModelViewState.defaults
            guard let qwenIndex = qwenModels.firstIndex(where: {
                $0.id == FunAsrCatalog.qwen3AsrId
            }) else {
                return XCTFail("The Qwen3-ASR 0.6B preview row is missing")
            }
            qwenModels[qwenIndex].isInstalled = true
            let qwenController = SettingsWindowController(
                settings: qwenSettings,
                runtime: SettingsRuntimeState(localModels: qwenModels),
                actions: SettingsViewActions(save: { _, _ in nil }))
            let qwenButtons = qwenController.window?.contentView
                .map { allViews(in: $0).compactMap { $0 as? NSButton } } ?? []
            XCTAssertFalse(qwenButtons.contains {
                $0.title == "Remove" && $0.identifier?.rawValue == FunAsrCatalog.qwen3AsrId
            })
            XCTAssertTrue(qwenButtons.contains { $0.title == "Active" && !$0.isEnabled })
            qwenController.close()
        }
    }

    func testQwen17UsesQwenFamilyPresentation() async {
        await MainActor.run {
            var settings = AppSettings()
            settings.engine = .funAsr
            settings.funAsrModelId = FunAsrCatalog.qwen3Asr17Id
            var models = LocalModelViewState.defaults
            guard let modelIndex = models.firstIndex(where: {
                $0.id == FunAsrCatalog.qwen3Asr17Id
            }) else {
                return XCTFail("The Qwen3-ASR 1.7B preview row is missing")
            }
            models[modelIndex].isInstalled = true

            let controller = SettingsWindowController(
                settings: settings,
                runtime: SettingsRuntimeState(localModels: models),
                actions: SettingsViewActions(save: { _, _ in nil }))
            let labels = controller.window?.contentView.map {
                allViews(in: $0).compactMap { ($0 as? NSTextField)?.stringValue }
            } ?? []

            XCTAssertTrue(labels.contains("Qwen3-ASR (local)"))
            XCTAssertTrue(labels.contains {
                $0.contains("Qwen3-ASR auto-detects language")
            })
            controller.close()
        }
    }

    @MainActor
    private func settingsPageHost(in root: NSView) -> NSView? {
        allViews(in: root).first { $0.identifier?.rawValue == "settings.page-host" }
    }

    @MainActor
    private func settingsNavigationButtons(in root: NSView) -> [NSButton] {
        allViews(in: root)
            .compactMap { $0 as? NSButton }
            .filter { $0.identifier?.rawValue.hasPrefix("settings.navigation.") == true }
            .sorted { $0.tag < $1.tag }
    }

    @MainActor
    private func visibleSettingsPage(in host: NSView) -> NSScrollView? {
        host.subviews.compactMap { $0 as? NSScrollView }.first { !$0.isHidden }
    }

    @MainActor
    private func assertMajorSettingsRowsFillWidth(
        in content: NSStackView,
        pageName: String,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        let majorRows = content.arrangedSubviews.filter {
            $0 is NSStackView || $0 is NSScrollView || $0 is NSBox || isSettingsCard($0)
        }
        XCTAssertFalse(majorRows.isEmpty, "\(pageName) should contain a major layout row.", file: file, line: line)
        for row in majorRows {
            XCTAssertFalse(
                row.hasAmbiguousLayout,
                "\(pageName): \(type(of: row)) layout is ambiguous.",
                file: file,
                line: line)
            XCTAssertEqual(
                row.frame.width,
                content.bounds.width,
                accuracy: 1,
                "\(pageName): \(type(of: row)) should fill the page width.",
                file: file,
                line: line)
        }
    }

    @MainActor
    private func assertOverviewCardHeights(
        in root: NSView,
        sizeName: String,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        let expectedHeights: [(identifier: String, range: ClosedRange<CGFloat>)] = [
            ("settings.overview.banner", 48...76),
            ("settings.overview.engine", 45...72),
            ("settings.overview.summary", 45...72),
            ("settings.overview.input", 45...72),
        ]
        for expected in expectedHeights {
            guard let card = allViews(in: root).first(where: {
                $0.identifier?.rawValue == expected.identifier
            }) else {
                XCTFail("\(sizeName): Missing \(expected.identifier).", file: file, line: line)
                continue
            }
            XCTAssertFalse(
                card.hasAmbiguousLayout,
                "\(sizeName): \(expected.identifier) layout is ambiguous.",
                file: file,
                line: line)
            XCTAssertTrue(
                expected.range.contains(card.frame.height),
                "\(sizeName): \(expected.identifier) height \(card.frame.height) is outside \(expected.range).",
                file: file,
                line: line)
        }
    }

    @MainActor
    private func isSettingsCard(_ view: NSView) -> Bool {
        view.layer?.borderWidth == 1 && view.layer?.cornerRadius == 6
    }

    @MainActor
    private func settingsStatusLabel(in root: NSView) -> NSTextField? {
        allViews(in: root)
            .compactMap { $0 as? NSTextField }
            .first { $0.identifier?.rawValue == "settings.footer.status" }
    }

    @MainActor
    private func settingsFooterButtons(in root: NSView) -> [NSButton] {
        allViews(in: root)
            .compactMap { $0 as? NSButton }
            .filter { ["settings.footer.close", "settings.footer.save"].contains($0.identifier?.rawValue) }
    }

    @MainActor
    private func allViews(in root: NSView) -> [NSView] {
        [root] + root.subviews.flatMap { allViews(in: $0) }
    }
}
