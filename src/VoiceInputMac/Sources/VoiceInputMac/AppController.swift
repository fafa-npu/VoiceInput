import AppKit
import AVFoundation
import Foundation
import Speech

@MainActor
final class AppController: NSObject {
    private let store = SettingsStore()
    private var settings: AppSettings
    private let keyboard: KeyboardMonitor
    private let audio = AudioCapture()
    private let overlay = OverlayWindowController()
    private let funAsr = FunAsrRuntimeManager()
    private let qwen3Asr = Qwen3AsrRuntimeManager()
    private let refiner = LlmRefiner()
    private let corrections = CorrectionTracker()
    private let updater = UpdateService()

    private var statusItem: NSStatusItem?
    private var settingsWindow: SettingsWindowController?
    private var onboardingWindow: OnboardingWindowController?
    private var restoreOnboardingAfterSettings = false
    private var engine: SpeechEngine?
    private var entraProviders: [String: EntraTokenProvider] = [:]
    private var installTask: Task<Void, Never>?
    private var qwenPrewarmTask: Task<Void, Never>?
    private var updateInstallTask: Task<Void, Never>?
    private var lifecycleTail: Task<Void, Never>?

    private var state = DictationState.idle
    private var generation = 0
    private var dictating = false
    private var paused = false
    private var inputTarget: InputTarget?
    private var finalFragments: [String] = []
    private var partial = ""
    private var peak: Float = 0
    private var speechFault: SpeechFault?
    private var pendingText: String?
    private var availableUpdate: AvailableUpdate?
    private var installingModelId: String?
    private var installProgress: FunAsrInstallProgress?

    override init() {
        let loaded = SettingsStore().load()
        settings = loaded
        keyboard = KeyboardMonitor(activationKey: loaded.activeProfile.activationKey)
        super.init()
        wirePlatformCallbacks()
    }

    func start() {
        NotificationCenter.default.addObserver(
            self, selector: #selector(applicationBecameActive),
            name: NSApplication.didBecomeActiveNotification, object: nil)
        PlatformNotification.requestAuthorization()
        buildStatusItem()
        let hasAccessibility = PlatformPermissions.hasAccessibility
        let hasInputMonitoring = PlatformPermissions.hasInputMonitoring
        ensureKeyboardMonitor(requestIfNeeded: false)
        AppLog.write("=== gujiguji macOS 0.2.16 started; profile=\(settings.activeProfile.name) "
                     + "engine=\(settings.engine.rawValue) language=\(settings.language) "
                     + "accessibility=\(hasAccessibility) inputMonitoring=\(hasInputMonitoring) ===")
        // A rebuilt or newly signed app can lose its TCC identity even when
        // onboarding was completed previously. Without the global event tap,
        // the activation key cannot display an overlay or explain the failure,
        // so surface the permission recovery UI immediately.
        if !settings.onboardingCompleted || !hasAccessibility || !hasInputMonitoring {
            openOnboarding()
        }
        Task { [weak self] in await self?.checkForUpdates(silent: true) }
        Task { [weak self] in await self?.prewarmEntraIfNeeded() }
        prewarmQwenIfNeeded()
    }

    func shutdown() {
        NotificationCenter.default.removeObserver(self)
        installTask?.cancel()
        qwenPrewarmTask?.cancel()
        updateInstallTask?.cancel()
        lifecycleTail?.cancel()
        engine?.cancel()
        engine = nil
        keyboard.stop()
        audio.release()
        overlay.window?.orderOut(nil)
        if let statusItem { NSStatusBar.system.removeStatusItem(statusItem) }
        statusItem = nil
    }

    private func wirePlatformCallbacks() {
        keyboard.onPressed = { [weak self] in
            Task { @MainActor [weak self] in self?.enqueueGesture(.pressed) }
        }
        keyboard.onReleased = { [weak self] in
            Task { @MainActor [weak self] in self?.enqueueGesture(.released) }
        }
        keyboard.onRecoveredRelease = { [weak self] in
            Task { @MainActor [weak self] in self?.enqueueGesture(.recoveredRelease) }
        }
        keyboard.onCancelled = { [weak self] in
            Task { @MainActor [weak self] in self?.enqueueGesture(.cancelled) }
        }
        keyboard.onSubmitted = { [weak self] in
            Task { @MainActor [weak self] in self?.corrections.captureSubmittedEdit() }
        }
        keyboard.onSwitchProfile = { [weak self] in
            Task { @MainActor [weak self] in self?.enqueueProfileSwitch() }
        }
        audio.onLevel = { [weak self] value in
            Task { @MainActor [weak self] in
                guard let self else { return }
                peak = max(peak, value)
                overlay.setLevel(Double(value))
            }
        }
        funAsr.onProgress = { [weak self] progress in
            Task { @MainActor [weak self] in self?.handleInstallProgress(progress) }
        }
        qwen3Asr.onProgress = { [weak self] progress in
            Task { @MainActor [weak self] in self?.handleInstallProgress(progress) }
        }
    }

    private func enqueueGesture(_ gesture: PttGesture) {
        enqueue { controller in await controller.performGesture(gesture) }
    }

    private func enqueueProfileSwitch() {
        enqueue { controller in
            // Option+Shift+G is also a chord cancellation when the PTT key is
            // held. Complete that cancellation before changing profiles.
            if controller.dictating || controller.state == .starting || controller.state == .listening {
                await controller.cancelDictation()
            }
            controller.switchToNextProfile()
        }
    }

    private func enqueue(_ operation: @escaping @MainActor (AppController) async -> Void) {
        let previous = lifecycleTail
        lifecycleTail = Task { @MainActor [weak self] in
            if let previous { await previous.value }
            guard let self, !Task.isCancelled else { return }
            await operation(self)
        }
    }

    private func performGesture(_ gesture: PttGesture) async {
        if paused { return }
        switch PttRouter.action(mode: settings.activeProfile.pttMode, gesture: gesture,
                                dictating: dictating, state: state) {
        case .start: await startDictation()
        case .stop: await stopDictation()
        case .cancel: await cancelDictation()
        case .busy: notify("Still processing", "Wait for the current dictation to finish.")
        case .none: break
        }
    }

    private func startDictation() async {
        guard !dictating, !state.isProcessing else { return }
        guard PlatformPermissions.hasAccessibility else {
            rejectDictationStart(
                "Accessibility permission is required to protect and update the focused text field.",
                overlayMessage: "Allow Accessibility in System Settings")
            return
        }
        guard let target = AccessibilityReader.captureTarget(logFailure: true) else {
            rejectDictationStart(
                "Click an editable text field before starting voice input.",
                overlayMessage: "Click an editable text field first")
            return
        }
        generation += 1
        let sessionGeneration = generation
        dictating = true
        state = .starting
        inputTarget = target
        finalFragments.removeAll(keepingCapacity: true)
        partial = ""
        peak = 0
        speechFault = nil
        overlay.show(.starting, position: settings.activeProfile.overlayPosition,
                     screen: AccessibilityReader.screen(for: target))
        onboardingWindow?.updatePractice(status: "正在准备…", detail: "麦克风启动后再开始说话。", listening: true)
        var sessionEngine: SpeechEngine?

        do {
            let engine = try makeEngine()
            sessionEngine = engine
            self.engine = engine
            engine.onPartial = { [weak self] text in
                Task { @MainActor [weak self] in self?.acceptPartial(text, generation: sessionGeneration) }
            }
            engine.onFinal = { [weak self] text in
                Task { @MainActor [weak self] in self?.acceptFinal(text, generation: sessionGeneration) }
            }
            engine.onFault = { [weak self] fault in
                Task { @MainActor [weak self] in self?.acceptFault(fault, generation: sessionGeneration) }
            }

            try await withTimeout(seconds: 8) { try await engine.start(language: self.settings.language) }
            guard generation == sessionGeneration, dictating, !paused, !Task.isCancelled else {
                throw CancellationError()
            }
            audio.onChunk = { [weak engine] data in engine?.feed(data) }
            try await audio.beginSession()
            guard generation == sessionGeneration, dictating, !paused, !Task.isCancelled else {
                throw CancellationError()
            }
            state = .listening
            let listening = settings.language.hasPrefix("zh") ? "聆听中…" : "Listening…"
            overlay.update(.listening(listening))
            onboardingWindow?.updatePractice(status: "正在聆听", detail: stopInstruction, listening: true)
            AppLog.write("dictation started engine=\(settings.engine.rawValue)")
        } catch is CancellationError {
            await abortSession(notifyError: nil, expectedGeneration: sessionGeneration,
                               expectedEngine: sessionEngine)
        } catch {
            let message = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
            AppLog.write("dictation start failed: \(message)")
            await abortSession(notifyError: message, expectedGeneration: sessionGeneration,
                               expectedEngine: sessionEngine)
        }
    }

    private func rejectDictationStart(_ message: String, overlayMessage: String) {
        AppLog.write("dictation start failed: \(message)")
        overlay.show(.failure(overlayMessage), position: settings.activeProfile.overlayPosition)
        onboardingWindow?.updatePractice(status: "无法开始", detail: message, listening: false)
        notify("Recognition failed", message)
    }

    private func stopDictation() async {
        guard dictating else { return }
        let sessionGeneration = generation
        let sessionEngine = engine
        dictating = false
        state = .transcribing
        onboardingWindow?.updatePractice(status: "正在处理", detail: "识别完成后会写入文本框。", listening: false)
        if engine?.hasInterimResults == false { overlay.update(.transcribing) }

        await audio.endSession()
        guard generation == sessionGeneration, !paused, !Task.isCancelled else {
            sessionEngine?.cancel()
            return
        }
        audio.onChunk = nil
        var stopTimeoutFault: SpeechFault?
        if let sessionEngine {
            do { try await withTimeout(seconds: sessionEngine.stopTimeout) { await sessionEngine.stop() } }
            catch {
                sessionEngine.cancel()
                stopTimeoutFault = SpeechFault(.timeout, "The speech engine did not finish in time.")
                AppLog.write("speech engine stop timed out after \(sessionEngine.stopTimeout)s")
            }
        }
        await Task.yield()
        await Task.yield()
        guard generation == sessionGeneration, !paused, !Task.isCancelled else { return }
        if let sessionEngine, engine !== sessionEngine { return }
        if sessionEngine == nil || engine === sessionEngine { engine = nil }
        if let stopTimeoutFault { speechFault = stopTimeoutFault }

        var text = Self.normalizedTextForKeyboardDelivery(composedTranscript)
        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            if let speechFault { notify("Transcription failed", speechFault.message) }
            else if peak < 0.02 { notify("No microphone audio", "gujiguji did not capture audible speech.") }
            finishSession()
            return
        }
        if settings.diagnosticLogging { AppLog.write("transcript: \(diagnosticText(text))") }
        let raw = text
        guard let target = inputTarget, AccessibilityReader.isCurrent(target) else {
            preserve(text, reason: "The focused app or input control changed while gujiguji was processing.")
            finishSession(failed: true)
            return
        }

        if settings.llmEnabled, LlmRefiner.isConfigured(settings) {
            state = .refining
            overlay.update(.refining)
            let context = settings.useContext
                ? await readContext(target, timeout: 1.5)
                : nil
            guard generation == sessionGeneration, !paused, !Task.isCancelled else { return }
            let refined = await refiner.refine(text, settings: settings, context: context)
            guard generation == sessionGeneration, !paused, !Task.isCancelled else { return }
            if settings.diagnosticLogging {
                AppLog.write("refinement input: \(diagnosticText(text))")
                AppLog.write("refinement output: \(diagnosticText(refined))")
            }
            text = Self.normalizedTextForKeyboardDelivery(refined)
            guard AccessibilityReader.isCurrent(target) else {
                preserve(text, reason: "The focused input control changed during refinement.")
                finishSession(failed: true)
                return
            }
        }

        guard generation == sessionGeneration, !paused, !Task.isCancelled else { return }
        state = .injecting
        overlay.hideAnimated()
        let result = await AccessibilityReader.inject(text, into: target)
        guard generation == sessionGeneration, !paused, !Task.isCancelled else { return }
        if result.success {
            if result.verified, settings.learnFromEdits, LlmRefiner.isConfigured(settings) {
                corrections.arm(raw: raw, injected: text, target: target)
            }
            state = .idle
            if result.verified {
                onboardingWindow?.updatePractice(
                    status: "已输入", detail: "文字已写入原来的文本框。", listening: false)
                AppLog.write(
                    "dictation delivery verified submittedUtf16=\(result.utf16UnitsSubmitted) "
                    + "confirmedUtf16=\(result.utf16UnitsInserted) mode=\(target.focusMode.rawValue)")
            } else {
                onboardingWindow?.updatePractice(
                    status: "已发送", detail: "已通过键盘输入事件发送文字。", listening: false)
                AppLog.write(
                    "dictation delivery dispatched submittedUtf16=\(result.utf16UnitsSubmitted) "
                    + "confirmedUtf16=0 mode=\(target.focusMode.rawValue)")
            }
        } else {
            AppLog.write(
                "dictation delivery failed submittedUtf16=\(result.utf16UnitsSubmitted) "
                + "confirmedUtf16=\(result.utf16UnitsInserted) mode=\(target.focusMode.rawValue) "
                + "reason=\(result.error ?? "unknown")")
            preserve(remainingText(text, afterUTF16Units: result.utf16UnitsInserted),
                     reason: result.error ?? "macOS rejected text injection.")
            state = .failed
        }
        inputTarget = nil
        rebuildMenu()
    }

    private func cancelDictation() async {
        guard dictating || state == .starting || state == .listening else { return }
        await abortSession(notifyError: nil)
        AppLog.write("dictation cancelled because the activation key became a chord")
    }

    private func abortSession(
        notifyError: String?,
        expectedGeneration: Int? = nil,
        expectedEngine: SpeechEngine? = nil
    ) async {
        if let expectedGeneration, generation != expectedGeneration {
            expectedEngine?.cancel()
            return
        }
        if let expectedEngine, engine !== expectedEngine {
            expectedEngine.cancel()
            return
        }
        dictating = false
        generation += 1
        state = .cancelled
        audio.onChunk = nil
        engine?.cancel()
        engine = nil
        audio.cancelSession()
        finalFragments.removeAll(keepingCapacity: true)
        partial = ""
        inputTarget = nil
        overlay.hideAnimated()
        onboardingWindow?.updatePractice(status: "已取消", detail: "点击文本框后可以重试。", listening: false)
        if let notifyError { notify("Recognition failed", notifyError) }
        state = .idle
    }

    private func finishSession(failed: Bool = false) {
        overlay.hideAnimated()
        inputTarget = nil
        state = failed ? .failed : .idle
        if failed { state = .idle }
        rebuildMenu()
    }

    private func acceptPartial(_ text: String, generation: Int) {
        guard self.generation == generation else { return }
        partial = text
        overlay.update(.partial(composedTranscript))
    }

    private func acceptFinal(_ text: String, generation: Int) {
        guard self.generation == generation else { return }
        if !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { finalFragments.append(text) }
        partial = ""
        overlay.update(.partial(composedTranscript))
    }

    private func acceptFault(_ fault: SpeechFault, generation: Int) {
        guard self.generation == generation else { return }
        speechFault = fault
        AppLog.write("speech fault \(fault.kind): \(fault.detail ?? fault.message)")
    }

    private var composedTranscript: String {
        var fragments = finalFragments
        if !partial.isEmpty { fragments.append(partial) }
        return TranscriptJoiner.join(fragments)
    }

    private func makeEngine() throws -> SpeechEngine {
        let vocabulary = settings.recognitionVocabulary
        AppLog.write("recognition vocabulary count=\(vocabulary.count) engine=\(settings.engine.rawValue)")
        if settings.diagnosticLogging, !vocabulary.isEmpty {
            AppLog.write("recognition vocabulary: \(diagnosticText(vocabulary.joined(separator: ", ")))")
        }
        switch settings.engine {
        case .macOS:
            return AppleSpeechEngine(vocabulary: vocabulary)
        case .azure where settings.azureAuthMode == .key:
            return try AzureSpeechEngine.forKey(
                key: settings.azureKey, region: settings.azureRegion, vocabulary: vocabulary)
        case .azure:
            let provider = entraProvider(tenant: settings.azureTenantId)
            return try AzureSpeechEngine.forBearer(
                endpoint: settings.azureEndpoint,
                resourceId: settings.azureResourceId,
                region: settings.azureRegion,
                tokenProvider: { try await provider.accessToken(interactive: false) },
                vocabulary: vocabulary)
        case .gptTranscribe:
            let transcribeVocabulary = settings.transcribeModelKind == .gpt4oTranscribe
                || settings.transcribeModelKind == .gpt4oMiniTranscribe
                ? vocabulary : []
            if settings.transcribeAuthMode == .key {
                return try OpenAITranscribeEngine(
                    endpoint: settings.transcribeEndpoint,
                    deployment: settings.transcribeModel,
                    model: settings.transcribeModel,
                    apiKey: settings.transcribeApiKey,
                    vocabulary: transcribeVocabulary)
            }
            let provider = entraProvider(tenant: settings.transcribeTenantId)
            return try OpenAITranscribeEngine(
                endpoint: settings.transcribeEndpoint,
                deployment: settings.transcribeModel,
                model: settings.transcribeModel,
                bearerTokenProvider: { try await provider.accessToken(interactive: false) },
                vocabulary: transcribeVocabulary)
        case .funAsr:
            let model = try FunAsrCatalog.model(settings.funAsrModelId)
            guard model.supports(settings.language) else {
                throw SpeechFault(.service, "\(model.displayName) does not support \(settings.language).")
            }
            if model.runner == .qwen3Asr {
                guard qwen3Asr.hasInstalledFiles(model.id) else { throw Qwen3AsrError.notInstalled }
                return Qwen3AsrEngine(model: model, recognizer: qwen3Asr)
            }
            return FunAsrEngine(resolved: try funAsr.resolve(model.id))
        }
    }

    private func entraProvider(tenant: String) -> EntraTokenProvider {
        let key = tenant.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        if let existing = entraProviders[key] { return existing }
        let provider = EntraTokenProvider(tenantId: tenant)
        entraProviders[key] = provider
        return provider
    }

    private func prewarmEntraIfNeeded(interactive: Bool = false) async {
        let tenant: String?
        if settings.engine == .azure, settings.azureAuthMode == .entraId { tenant = settings.azureTenantId }
        else if settings.engine == .gptTranscribe, settings.transcribeAuthMode == .entraId {
            tenant = settings.transcribeTenantId
        } else { tenant = nil }
        guard let tenant else { return }
        do { try await entraProvider(tenant: tenant).prewarm(interactive: interactive) }
        catch { notify("Azure sign-in needed", "Open Settings and retry Azure sign-in.") }
    }

    // MARK: - Menu bar

    private func buildStatusItem() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.image = statusImage()
        item.button?.imagePosition = .imageOnly
        statusItem = item
        rebuildMenu()
    }

    private func rebuildMenu() {
        guard let item = statusItem else { return }
        item.button?.toolTip = tooltip
        let menu = NSMenu()
        if !settings.onboardingCompleted {
            menu.addItem(NSMenuItem(title: "完成快速设置…", action: #selector(openOnboardingAction), keyEquivalent: ""))
            menu.addItem(.separator())
        }
        if pendingText != nil {
            menu.addItem(NSMenuItem(title: "Retry pending text", action: #selector(retryPendingAction), keyEquivalent: ""))
            menu.addItem(NSMenuItem(title: "Copy pending text", action: #selector(copyPendingAction), keyEquivalent: ""))
            menu.addItem(.separator())
        }
        menu.addItem(NSMenuItem(title: paused ? "Resume listening" : "Pause listening",
                                action: #selector(togglePauseAction), keyEquivalent: ""))

        let profileItem = NSMenuItem(title: "Input profile", action: nil, keyEquivalent: "")
        let profileMenu = NSMenu()
        for profile in settings.profiles {
            let choice = NSMenuItem(title: profile.name, action: #selector(selectProfileAction(_:)), keyEquivalent: "")
            choice.representedObject = profile.id
            choice.state = profile.id == settings.activeProfileId ? .on : .off
            profileMenu.addItem(choice)
        }
        profileItem.submenu = profileMenu
        menu.addItem(profileItem)
        menu.addItem(.separator())

        let learn = NSMenuItem(title: "Learn from corrections…", action: #selector(learnCorrectionsAction), keyEquivalent: "")
        learn.isEnabled = LlmRefiner.isConfigured(settings)
        menu.addItem(learn)
        menu.addItem(NSMenuItem(title: "Settings…", action: #selector(openSettingsAction), keyEquivalent: ","))
        if let availableUpdate {
            let update = NSMenuItem(title: "Update to \(availableUpdate.tag)…", action: #selector(installUpdateAction), keyEquivalent: "")
            update.isEnabled = updateInstallTask == nil
            menu.addItem(update)
        }
        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "Quit", action: #selector(quitAction), keyEquivalent: "q"))
        for menuItem in menu.items where menuItem.action != nil { menuItem.target = self }
        for menuItem in profileMenu.items { menuItem.target = self }
        item.menu = menu
    }

    private var tooltip: String {
        let status = paused ? "paused" : state == .idle
            ? (settings.activeProfile.pttMode == .hold
               ? "hold \(settings.activeProfile.activationKey.displayName)"
               : "\(settings.activeProfile.activationKey.displayName) start/stop")
            : state.rawValue
        return "gujiguji · \(settings.activeProfile.name) · \(status)"
    }

    private func statusImage() -> NSImage? {
        if let url = Bundle.main.url(forResource: "gujiguji", withExtension: "icns"),
           let image = NSImage(contentsOf: url) {
            image.size = NSSize(width: 18, height: 18)
            return image
        }
        let image = NSImage(systemSymbolName: "waveform", accessibilityDescription: "gujiguji")
        image?.isTemplate = true
        return image
    }

    @objc private func openOnboardingAction() { openOnboarding() }
    @objc private func openSettingsAction() { openSettings() }
    @objc private func togglePauseAction() { togglePause() }
    @objc private func retryPendingAction() { retryPendingText() }
    @objc private func copyPendingAction() { copyPendingText() }
    @objc private func learnCorrectionsAction() { learnFromCorrections() }
    @objc private func installUpdateAction() { installAvailableUpdate() }
    @objc private func quitAction() { NSApp.terminate(nil) }

    @objc private func applicationBecameActive() {
        ensureKeyboardMonitor(requestIfNeeded: false)
        onboardingWindow?.updateState(onboardingState, profile: settings.activeProfile)
    }

    @objc private func selectProfileAction(_ sender: NSMenuItem) {
        guard let id = sender.representedObject as? String else { return }
        activateProfile(id, showOverlay: true)
    }

    private func togglePause() {
        paused.toggle()
        if paused { cancelImmediatelyForPause() }
        notify(paused ? "Listening paused" : "Listening resumed",
               paused ? "The activation key is disabled until you resume." : "Voice activation is ready.")
        rebuildMenu()
    }

    private func cancelImmediatelyForPause() {
        lifecycleTail?.cancel()
        lifecycleTail = nil
        generation += 1
        dictating = false
        state = .cancelled
        audio.onChunk = nil
        engine?.cancel()
        engine = nil
        audio.release()
        finalFragments.removeAll(keepingCapacity: true)
        partial = ""
        inputTarget = nil
        overlay.hideAnimated()
        onboardingWindow?.updatePractice(status: "已暂停", detail: "恢复聆听后可以重试。", listening: false)
        state = .idle
        AppLog.write("active dictation cancelled because listening was paused")
    }

    private func switchToNextProfile() {
        let next = settings.activeProfileId == InputProfile.profile2Id
            ? InputProfile.profile1Id : InputProfile.profile2Id
        activateProfile(next, showOverlay: true)
    }

    private func activateProfile(_ id: String, showOverlay: Bool) {
        guard !dictating, !state.isProcessing,
              settings.profiles.contains(where: { $0.id == id }), id != settings.activeProfileId else {
            if dictating || state.isProcessing {
                notify("Profile not switched", "Finish the current dictation before switching profiles.")
            }
            return
        }
        let previousProfile = settings.activeProfile
        settings.activeProfileId = id
        persistSettings()
        keyboard.update(activationKey: settings.activeProfile.activationKey)
        AppLog.write(
            "profile switched from=\(previousProfile.name) to=\(settings.activeProfile.name) "
            + "activation=\(settings.activeProfile.activationKey.rawValue) "
            + "mode=\(settings.activeProfile.pttMode.rawValue)")
        onboardingWindow?.updateState(onboardingState, profile: settings.activeProfile)
        if showOverlay {
            let behavior = settings.activeProfile.pttMode == .hold ? "hold to talk" : "press to start / stop"
            overlay.show(.profile(name: settings.activeProfile.name,
                                  activation: "\(settings.activeProfile.activationKey.displayName) · \(behavior)"),
                         position: settings.activeProfile.overlayPosition)
        }
        rebuildMenu()
    }

    // MARK: - Settings and onboarding

    private func openSettings() {
        if onboardingWindow?.window?.isVisible == true {
            restoreOnboardingAfterSettings = true
            onboardingWindow?.window?.orderOut(nil)
        }
        if let settingsWindow, settingsWindow.window?.isVisible == true {
            NSApp.activate(ignoringOtherApps: true)
            settingsWindow.window?.makeKeyAndOrderFront(nil)
            return
        }
        let actions = SettingsViewActions(
            save: { [weak self] submitted, baseline in
                guard let self else { return "Settings window is no longer available." }
                return self.saveSettings(submitted, opened: baseline)
            },
            installLocalModel: { [weak self] id in self?.installLocalModel(id) },
            removeLocalModel: { [weak self] id in self?.removeLocalModel(id) },
            cancelLocalModelInstall: { [weak self] in self?.cancelLocalModelInstall() },
            suggestVocabulary: { [weak self] draft in self?.suggestVocabulary(settings: draft) },
            testLLM: { [weak self] draft in self?.testLLM(draft) },
            setStartAtLogin: { [weak self] enabled in self?.setStartAtLogin(enabled) },
            checkForUpdates: { [weak self] in Task { await self?.checkForUpdates(silent: false) } },
            installUpdate: { [weak self] in self?.installAvailableUpdate() },
            openLog: { [weak self] in self?.openLog() }
        )
        let controller = SettingsWindowController(settings: settings, runtime: settingsRuntime, actions: actions)
        settingsWindow = controller
        controller.window?.delegate = self
        NSApp.activate(ignoringOtherApps: true)
        controller.showWindow(nil)
        controller.window?.makeKeyAndOrderFront(nil)
    }

    private func saveSettings(_ submitted: AppSettings, opened: AppSettings) -> String? {
        var candidate = submitted
        if submitted.activeProfileId == opened.activeProfileId { candidate.activeProfileId = settings.activeProfileId }
        for index in candidate.profiles.indices where index < opened.profiles.count {
            if submitted.profiles[index].pttMode == opened.profiles[index].pttMode,
               let live = settings.profiles.first(where: { $0.id == candidate.profiles[index].id }) {
                candidate.profiles[index].pttMode = live.pttMode
            }
        }
        if let error = SettingsValidation.error(in: candidate) {
            return error
        }
        candidate.normalize()
        if candidate.engine == .funAsr {
            guard let model = try? FunAsrCatalog.model(candidate.funAsrModelId), model.supports(candidate.language),
                  localModelInstalled(model) else {
                return "Install a local model compatible with the selected language first."
            }
        }
        guard confirmSensitiveChanges(from: settings, to: candidate) else { return "Changes were not saved." }
        do {
            try store.save(candidate)
            settings = candidate
            keyboard.update(activationKey: settings.activeProfile.activationKey)
            settingsWindow?.updateRuntime(settingsRuntime)
            onboardingWindow?.updateState(onboardingState, profile: settings.activeProfile)
            rebuildMenu()
            Task { [weak self] in await self?.prewarmEntraIfNeeded(interactive: true) }
            prewarmQwenIfNeeded()
            return nil
        } catch {
            return error.localizedDescription
        }
    }

    private func confirmSensitiveChanges(from old: AppSettings, to new: AppSettings) -> Bool {
        let changes: [(Bool, String)] = [
            (!old.useContext && new.useContext, "Surrounding text from the active app may be sent to your configured LLM."),
            (!old.learnFromEdits && new.learnFromEdits, "Your edits will be stored locally in encrypted form for correction learning."),
            (!old.diagnosticLogging && new.diagnosticLogging, "Diagnostic logs may contain transcripts, vocabulary, and LLM output."),
        ]
        for (enabled, message) in changes where enabled {
            let alert = NSAlert()
            alert.messageText = "Enable privacy-sensitive feature?"
            alert.informativeText = message
            alert.alertStyle = .warning
            alert.addButton(withTitle: "Enable")
            alert.addButton(withTitle: "Cancel")
            if alert.runModal() != .alertFirstButtonReturn { return false }
        }
        return true
    }

    private func openOnboarding() {
        if let onboardingWindow, onboardingWindow.window?.isVisible == true {
            NSApp.activate(ignoringOtherApps: true)
            onboardingWindow.window?.makeKeyAndOrderFront(nil)
            return
        }
        let actions = OnboardingViewActions(
            requestPermission: { [weak self] kind in self?.requestPermission(kind) },
            installLocalModel: { [weak self] in self?.installLocalModel(FunAsrCatalog.defaultId) },
            cancelLocalModelInstall: { [weak self] in self?.cancelLocalModelInstall() },
            setPTTMode: { [weak self] mode in self?.setOnboardingPTTMode(mode) },
            complete: { [weak self] choice, mode in self?.completeOnboarding(choice, mode: mode) ?? false },
            openSettings: { [weak self] in self?.openSettings() }
        )
        let controller = OnboardingWindowController(profile: settings.activeProfile,
                                                    state: onboardingState, actions: actions)
        onboardingWindow = controller
        controller.window?.delegate = self
        NSApp.activate(ignoringOtherApps: true)
        controller.showWindow(nil)
        controller.window?.makeKeyAndOrderFront(nil)
    }

    private var onboardingState: OnboardingViewState {
        let selected = FunAsrCatalog.normalizedId(settings.funAsrModelId)
        let usesConfigured = settings.engine != .funAsr || selected != FunAsrCatalog.defaultId
        let ready = settings.engine != .funAsr
            || (try? FunAsrCatalog.model(selected)).map(localModelInstalled) == true
        let summary: String
        switch settings.engine {
        case .funAsr:
            let fallback = (try? FunAsrCatalog.model(FunAsrCatalog.defaultId).displayName)
                ?? "Qwen3-ASR 0.6B"
            summary = "本地识别 · \((try? FunAsrCatalog.model(selected).displayName) ?? fallback)"
        case .macOS: summary = "macOS Speech · 系统听写"
        case .azure: summary = "Azure Speech 已配置"
        case .gptTranscribe: summary = "GPT Transcribe · \(settings.transcribeModel)"
        }
        return OnboardingViewState(
            microphone: microphonePermission,
            accessibility: PlatformPermissions.hasAccessibility && PlatformPermissions.hasInputMonitoring ? .granted : .denied,
            recognitionReady: ready,
            usesConfiguredRecognition: usesConfigured,
            recognitionSummary: summary,
            installingLocalModel: installTask != nil,
            installationProgress: progressFraction,
            installationStatus: installProgressStatus
                ?? "下载 Qwen3-ASR 0.6B（约 850 MB），使用 Metal/CPU 本地识别，语音不会上传。")
    }

    private var microphonePermission: OnboardingPermissionState {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized: .granted
        case .denied, .restricted: .denied
        case .notDetermined: .unknown
        @unknown default: .unknown
        }
    }

    private func requestPermission(_ kind: OnboardingPermissionKind) {
        switch kind {
        case .microphone:
            Task { [weak self] in
                _ = await PlatformPermissions.requestMicrophone()
                self?.onboardingWindow?.updateState(self?.onboardingState ?? OnboardingViewState())
            }
        case .accessibility:
            _ = PlatformPermissions.requestAccessibility()
            _ = PlatformPermissions.requestInputMonitoring()
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { [weak self] in
                guard let self else { return }
                ensureKeyboardMonitor(requestIfNeeded: false)
                onboardingWindow?.updateState(onboardingState)
                if !PlatformPermissions.hasAccessibility || !PlatformPermissions.hasInputMonitoring {
                    openPrivacySettings()
                }
            }
        }
    }

    private func setOnboardingPTTMode(_ mode: PttMode) {
        var profile = settings.activeProfile
        profile.pttMode = mode
        settings.activeProfile = profile
        persistSettings()
        rebuildMenu()
    }

    private func completeOnboarding(_ choice: OnboardingCompletionChoice, mode: PttMode) -> Bool {
        guard microphonePermission == .granted,
              PlatformPermissions.hasAccessibility,
              PlatformPermissions.hasInputMonitoring else {
            showAlert(title: "Permissions required",
                      message: "Allow Microphone, Accessibility, and Input Monitoring before finishing setup.")
            return false
        }
        switch choice {
        case .defaultLocal:
            guard let model = try? FunAsrCatalog.model(FunAsrCatalog.defaultId),
                  localModelInstalled(model) else {
                showAlert(
                    title: "Local model not ready",
                    message: "Finish the Qwen3-ASR 0.6B download before continuing.")
                return false
            }
            settings.engine = .funAsr
            settings.funAsrModelId = FunAsrCatalog.defaultId
        case .macOSFallback:
            let alert = NSAlert()
            alert.messageText = "Use macOS Speech instead?"
            alert.informativeText = "Accuracy can be lower for Chinese, accents, and technical vocabulary. You can install a local model later in Settings."
            alert.alertStyle = .warning
            alert.addButton(withTitle: "Use macOS Speech")
            alert.addButton(withTitle: "Go back")
            guard alert.runModal() == .alertFirstButtonReturn else { return false }
            settings.engine = .macOS
        case .configured:
            break
        }
        if settings.engine == .macOS, !ensureMacSpeechPermission() { return false }
        var profile = settings.activeProfile
        profile.pttMode = mode
        settings.activeProfile = profile
        settings.onboardingCompleted = true
        persistSettings()
        keyboard.update(activationKey: profile.activationKey)
        ensureKeyboardMonitor(requestIfNeeded: true)
        rebuildMenu()
        return true
    }

    // MARK: - Model, LLM, update actions

    private func localModelInstalled(_ model: FunAsrModel) -> Bool {
        model.runner == .qwen3Asr
            ? qwen3Asr.hasInstalledFiles(model.id)
            : funAsr.hasInstalledFiles(model.id)
    }

    private func prewarmQwenIfNeeded() {
        guard qwenPrewarmTask == nil, settings.engine == .funAsr,
              let model = try? FunAsrCatalog.model(settings.funAsrModelId),
              model.runner == .qwen3Asr, qwen3Asr.hasInstalledFiles(model.id) else { return }
        qwenPrewarmTask = Task { [weak self] in
            guard let self else { return }
            do {
                try await qwen3Asr.prewarm(model.id)
            } catch is CancellationError {
                // App shutdown or a superseding configuration change.
            } catch {
                AppLog.write("Qwen3-ASR prewarm failed: \(error.localizedDescription)")
            }
            qwenPrewarmTask = nil
        }
    }

    private var settingsRuntime: SettingsRuntimeState {
        let funAsrRuntime = funAsr.isRuntimeInstalled(verifyHashes: false)
            ? "FunASR \(FunAsrCatalog.runtimeVersion)"
            : "FunASR runtime not installed"
        return SettingsRuntimeState(
            appVersion: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.2.16",
            localRuntimeSummary: "\(funAsrRuntime) · \(Qwen3AsrRuntimeManager.runtimeVersion) · Metal/CPU",
            localModels: FunAsrCatalog.models.map {
                LocalModelViewState(id: $0.id, name: $0.displayName, detail: $0.description,
                                    downloadSize: ByteCountFormatter.string(fromByteCount: $0.downloadSize,
                                                                           countStyle: .file),
                                    isInstalled: localModelInstalled($0),
                                    isRecommended: $0.id == FunAsrCatalog.defaultId)
            },
            installingModelId: installingModelId,
            installationProgress: progressFraction,
            installationStatus: installProgressStatus ?? "",
            statusMessage: installProgress?.error ?? "Settings saved",
            startAtLogin: LoginItemService.enabled,
            updateAvailable: availableUpdate != nil && updateInstallTask == nil)
    }

    private var progressFraction: Double? {
        guard let progress = installProgress, let total = progress.totalBytes, total > 0 else { return nil }
        return min(1, max(0, Double(progress.downloadedBytes) / Double(total)))
    }

    private var installProgressStatus: String? {
        guard let progress = installProgress else { return nil }
        if let error = progress.error { return error }
        guard progress.stage == .downloading else { return progress.artifact }
        let downloaded = NumberFormatter.localizedString(
            from: NSNumber(value: progress.downloadedBytes), number: .decimal)
        if let total = progress.totalBytes {
            let totalText = NumberFormatter.localizedString(from: NSNumber(value: total), number: .decimal)
            return "\(progress.artifact) · \(downloaded) / \(totalText) bytes"
        }
        return "\(progress.artifact) · \(downloaded) bytes"
    }

    private func installLocalModel(_ id: String) {
        guard installTask == nil else { return }
        guard let model = try? FunAsrCatalog.model(id) else {
            notify("Model installation failed", "Unknown local model '\(id)'.")
            return
        }
        installingModelId = id
        installTask = Task { [weak self] in
            guard let self else { return }
            do {
                if model.runner == .qwen3Asr {
                    try await qwen3Asr.install(id)
                } else {
                    try await funAsr.install(id)
                }
                installTask = nil
                installingModelId = nil
                prewarmQwenIfNeeded()
                settingsWindow?.updateRuntime(settingsRuntime)
                onboardingWindow?.updateState(onboardingState)
                notify("Local model ready", "\((try? FunAsrCatalog.model(id).displayName) ?? id) is installed.")
            } catch is CancellationError {
                installTask = nil
                installingModelId = nil
                settingsWindow?.updateRuntime(settingsRuntime)
                onboardingWindow?.updateState(onboardingState)
            } catch {
                installTask = nil
                installingModelId = nil
                installProgress = FunAsrInstallProgress(modelId: id, stage: .failed, artifact: "",
                                                        downloadedBytes: 0, totalBytes: nil,
                                                        error: error.localizedDescription)
                settingsWindow?.updateRuntime(settingsRuntime)
                onboardingWindow?.updateState(onboardingState)
                notify("Model installation failed", error.localizedDescription)
            }
        }
        settingsWindow?.updateRuntime(settingsRuntime)
        onboardingWindow?.updateState(onboardingState)
    }

    private func cancelLocalModelInstall() {
        installTask?.cancel()
    }

    private func removeLocalModel(_ id: String) {
        guard id != settings.funAsrModelId || settings.engine != .funAsr else {
            showAlert(title: "Model in use", message: "Select another speech engine or model before removing it.")
            return
        }
        guard let model = try? FunAsrCatalog.model(id) else {
            showAlert(title: "Could not remove model", message: "Unknown local model '\(id)'.")
            return
        }
        let alert = NSAlert()
        alert.messageText = "Remove local model?"
        alert.informativeText = "The downloaded model files will be deleted. You can download them again later."
        alert.alertStyle = .warning
        alert.addButton(withTitle: "Remove")
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        if model.runner == .qwen3Asr {
            Task { [weak self] in
                guard let self else { return }
                do {
                    try await qwen3Asr.remove(id)
                    settingsWindow?.updateRuntime(settingsRuntime)
                } catch {
                    showAlert(title: "Could not remove model", message: error.localizedDescription)
                }
            }
            return
        }
        do {
            try funAsr.remove(id)
            settingsWindow?.updateRuntime(settingsRuntime)
        } catch { showAlert(title: "Could not remove model", message: error.localizedDescription) }
    }

    private func handleInstallProgress(_ progress: FunAsrInstallProgress) {
        installProgress = progress
        settingsWindow?.updateRuntime(settingsRuntime)
        onboardingWindow?.updateState(onboardingState)
    }

    private func testLLM(_ draft: AppSettings) {
        Task { [weak self] in
            guard let self else { return }
            let result = await refiner.test(settings: draft)
            settingsWindow?.setLLMTestStatus(result.message, success: result.ok)
        }
    }

    private func suggestVocabulary(settings draft: AppSettings) {
        let pairs = corrections.pairs()
        Task { [weak self] in
            guard let self else { return }
            do {
                let terms = try await refiner.extractVocabulary(pairs, settings: draft)
                settingsWindow?.applyVocabularySuggestions(terms)
            } catch { settingsWindow?.setVocabularySuggestionStatus(error.localizedDescription) }
        }
    }

    private func learnFromCorrections() {
        guard LlmRefiner.isConfigured(settings) else {
            notify("LLM refinement required", "Configure and enable LLM refinement first.")
            return
        }
        let pairs = corrections.pairs()
        guard pairs.count >= 3 else {
            notify("Not enough edits yet", "At least three captured corrections are required.")
            return
        }
        notify("Learning…", "Summarizing \(pairs.count) local correction samples.")
        Task { [weak self] in
            guard let self else { return }
            do {
                let rules = try await refiner.summarizeCorrections(pairs, settings: settings)
                guard !rules.isEmpty else { notify("Nothing learned", "No recurring correction pattern was found."); return }
                let alert = NSAlert()
                alert.messageText = "Apply learned correction rules?"
                alert.informativeText = rules
                alert.addButton(withTitle: "Apply")
                alert.addButton(withTitle: "Cancel")
                if alert.runModal() == .alertFirstButtonReturn {
                    settings.llmLearnedRules = rules
                    persistSettings()
                    corrections.clear()
                    notify("Applied", "Learned rules were added to the refinement prompt.")
                }
            } catch { notify("Learn failed", error.localizedDescription) }
        }
    }

    private func setStartAtLogin(_ enabled: Bool) {
        do { try LoginItemService.setEnabled(enabled) }
        catch { showAlert(title: "Start at login failed", message: error.localizedDescription) }
        settingsWindow?.updateRuntime(settingsRuntime)
    }

    private func openLog() {
        if !FileManager.default.fileExists(atPath: AppPaths.log.path) {
            AppLog.write("log opened")
        }
        NSWorkspace.shared.open(AppPaths.log)
    }

    private func checkForUpdates(silent: Bool) async {
        let result = await updater.check()
        switch result {
        case .upToDate:
            if !silent { notify("Up to date", "You already have the latest version.") }
        case .available(let update):
            availableUpdate = update
            notify("Update available", "\(update.tag) is ready to install from the menu bar.")
        case .failed(let message):
            if !silent { notify("Update check failed", message) }
        }
        settingsWindow?.updateRuntime(settingsRuntime)
        rebuildMenu()
    }

    private func installAvailableUpdate() {
        guard updateInstallTask == nil else { return }
        guard let update = availableUpdate else {
            Task { [weak self] in await self?.checkForUpdates(silent: false) }
            return
        }
        let alert = NSAlert()
        alert.messageText = "Install \(update.tag)?"
        alert.informativeText = "The download will be SHA-256 checked and must match gujiguji's signing identity. The app will restart."
        alert.addButton(withTitle: "Install")
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        updateInstallTask = Task { @MainActor [weak self] in
            guard let self else { return }
            defer {
                updateInstallTask = nil
                settingsWindow?.updateRuntime(settingsRuntime)
                rebuildMenu()
            }
            do {
                if try await updater.stageAndApply(update) { NSApp.terminate(nil) }
            } catch { showAlert(title: "Update failed", message: error.localizedDescription) }
        }
        settingsWindow?.updateRuntime(settingsRuntime)
        rebuildMenu()
    }

    // MARK: - Recovery and helpers

    private func preserve(_ text: String, reason: String) {
        guard !text.isEmpty else { return }
        pendingText = text
        copyPendingText()
        notify("Text was not inserted", "\(reason) The remaining text was copied and can be retried from the menu bar.")
        rebuildMenu()
    }

    private func retryPendingText() {
        guard let text = pendingText, let target = AccessibilityReader.captureTarget() else { return }
        Task { @MainActor [weak self] in
            guard let self else { return }
            let result = await AccessibilityReader.inject(text, into: target)
            guard pendingText == text else { return }
            if result.success {
                pendingText = nil
                notify("Text inserted", "The preserved text was inserted into the current control.")
            } else {
                pendingText = remainingText(text, afterUTF16Units: result.utf16UnitsInserted)
                notify("Text still pending", result.error ?? "macOS rejected text injection.")
            }
            rebuildMenu()
        }
    }

    private func copyPendingText() {
        guard let pendingText else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(pendingText, forType: .string)
    }

    private func ensureKeyboardMonitor(requestIfNeeded: Bool) {
        if requestIfNeeded, !PlatformPermissions.hasInputMonitoring {
            _ = PlatformPermissions.requestInputMonitoring()
        }
        do { try keyboard.start() }
        catch { AppLog.write("keyboard monitor unavailable: \(error.localizedDescription)") }
    }

    private func openPrivacySettings() {
        let pane = PlatformPermissions.hasAccessibility ? "Privacy_ListenEvent" : "Privacy_Accessibility"
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(pane)") {
            NSWorkspace.shared.open(url)
        }
        notify("Permissions required", "Allow gujiguji in Accessibility and Input Monitoring, then return to setup.")
    }

    private func ensureMacSpeechPermission() -> Bool {
        switch SFSpeechRecognizer.authorizationStatus() {
        case .authorized:
            return true
        case .notDetermined:
            SFSpeechRecognizer.requestAuthorization { status in
                Task { @MainActor [weak self] in
                    if status != .authorized {
                        self?.showAlert(title: "Speech Recognition permission required",
                                        message: "Allow Speech Recognition to use the macOS Speech fallback.")
                    }
                }
            }
            showAlert(title: "Allow Speech Recognition",
                      message: "Approve the macOS permission prompt, then finish setup again.")
            return false
        case .denied, .restricted:
            if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_SpeechRecognition") {
                NSWorkspace.shared.open(url)
            }
            showAlert(title: "Speech Recognition permission required",
                      message: "Enable gujiguji under Privacy & Security → Speech Recognition.")
            return false
        @unknown default:
            return false
        }
    }

    private func persistSettings() {
        do { try store.save(settings) }
        catch { notify("Settings could not be saved", error.localizedDescription) }
    }

    private func notify(_ title: String, _ body: String) {
        PlatformNotification.show(title: title, body: body)
    }

    private func showAlert(title: String, message: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        alert.alertStyle = .warning
        alert.runModal()
    }

    private var stopInstruction: String {
        settings.activeProfile.pttMode == .hold ? "松开说话键后输入。" : "再按一次说话键后输入。"
    }

    private func remainingText(_ text: String, afterUTF16Units count: Int) -> String {
        let units = Array(text.utf16)
        return String(decoding: units.dropFirst(min(max(0, count), units.count)), as: UTF16.self)
    }

    nonisolated static func normalizedTextForKeyboardDelivery(_ text: String) -> String {
        String(text.unicodeScalars.map { scalar in
            switch scalar.value {
            case 0x09, 0x0A, 0x0D:
                return " "
            default:
                return String(scalar)
            }
        }.joined())
    }

    private func diagnosticText(_ text: String) -> String {
        String(text.prefix(2_000)).replacingOccurrences(of: "\n", with: "\\n")
    }
}

extension AppController: NSWindowDelegate {
    func windowWillClose(_ notification: Notification) {
        if notification.object as AnyObject? === settingsWindow?.window {
            settingsWindow = nil
            if restoreOnboardingAfterSettings, !settings.onboardingCompleted, let onboardingWindow {
                restoreOnboardingAfterSettings = false
                onboardingWindow.updateState(onboardingState, profile: settings.activeProfile)
                onboardingWindow.showWindow(nil)
                onboardingWindow.window?.makeKeyAndOrderFront(nil)
            }
        }
        if notification.object as AnyObject? === onboardingWindow?.window {
            onboardingWindow = nil
            restoreOnboardingAfterSettings = false
        }
    }
}

private func withTimeout(
    seconds: TimeInterval,
    operation: @escaping @Sendable () async throws -> Void
) async throws {
    try await withCheckedThrowingContinuation { continuation in
        let gate = TimeoutGate(continuation)
        let operationTask = Task {
            do { try await operation(); gate.resume(with: .success(())) }
            catch { gate.resume(with: .failure(error)) }
        }
        Task {
            try? await Task.sleep(for: .seconds(seconds))
            if gate.resume(with: .failure(SpeechFault(.timeout, "Speech engine startup timed out."))) {
                operationTask.cancel()
            }
        }
    }
}

private final class TimeoutGate: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<Void, Error>?

    init(_ continuation: CheckedContinuation<Void, Error>) { self.continuation = continuation }

    @discardableResult
    func resume(with result: Result<Void, Error>) -> Bool {
        lock.lock()
        guard let continuation else { lock.unlock(); return false }
        self.continuation = nil
        lock.unlock()
        continuation.resume(with: result)
        return true
    }
}

private func readContext(_ target: InputTarget, timeout: TimeInterval) async -> String? {
    await withCheckedContinuation { continuation in
        let gate = OptionalStringGate(continuation)
        let readTask = Task.detached(priority: .userInitiated) {
            gate.resume(AccessibilityReader.surroundingText(for: target))
        }
        Task {
            try? await Task.sleep(for: .seconds(timeout))
            if gate.resume(nil) { readTask.cancel() }
        }
    }
}

private final class OptionalStringGate: @unchecked Sendable {
    private let lock = NSLock()
    private var continuation: CheckedContinuation<String?, Never>?

    init(_ continuation: CheckedContinuation<String?, Never>) { self.continuation = continuation }

    @discardableResult
    func resume(_ value: String?) -> Bool {
        lock.lock()
        guard let continuation else { lock.unlock(); return false }
        self.continuation = nil
        lock.unlock()
        continuation.resume(returning: value)
        return true
    }
}
