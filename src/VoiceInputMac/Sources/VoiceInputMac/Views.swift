import AppKit

// MARK: - Settings public surface

struct LocalModelViewState: Equatable, Identifiable {
    var id: String
    var name: String
    var detail: String
    var downloadSize: String
    var isInstalled: Bool
    var isRecommended = false

    static let defaults = [
        LocalModelViewState(
            id: "sensevoice-small-q8",
            name: "SenseVoiceSmall",
            detail: "Balanced local recognition for Chinese, English, Japanese, and Korean.",
            downloadSize: "about 263 MB",
            isInstalled: false
        ),
        LocalModelViewState(
            id: "paraformer-zh-q8",
            name: "Paraformer Chinese",
            detail: "Fast Chinese and English recognition.",
            downloadSize: "about 246 MB",
            isInstalled: false
        ),
        LocalModelViewState(
            id: "fun-asr-nano-q4",
            name: "Fun-ASR Nano",
            detail: "Higher-quality recognition for difficult vocabulary and accents.",
            downloadSize: "about 963 MB",
            isInstalled: false
        ),
        LocalModelViewState(
            id: "qwen3-asr-0.6b-q8",
            name: "Qwen3-ASR 0.6B",
            detail: "High-accuracy multilingual recognition with Metal acceleration and automatic language detection.",
            downloadSize: "about 850 MB",
            isInstalled: false,
            isRecommended: true
        ),
        LocalModelViewState(
            id: "qwen3-asr-1.7b-q5km",
            name: "Qwen3-ASR 1.7B",
            detail: "Larger multilingual recognition model with Metal acceleration and automatic language detection.",
            downloadSize: "about 1.52 GB",
            isInstalled: false
        ),
    ]
}

struct SettingsRuntimeState: Equatable {
    var appVersion = "0.2.14"
    var localRuntimeSummary = "Local runtimes · Apple silicon"
    var localModels = LocalModelViewState.defaults
    var installingModelId: String?
    var installationProgress: Double?
    var installationStatus = ""
    var statusMessage = "Settings saved"
    var startAtLogin = false
    var updateAvailable = false
}

struct SettingsViewActions {
    var save: (AppSettings, AppSettings) -> String?
    var discard: () -> Void
    var installLocalModel: (String) -> Void
    var removeLocalModel: (String) -> Void
    var cancelLocalModelInstall: () -> Void
    var suggestVocabulary: (AppSettings) -> Void
    var testLLM: (AppSettings) -> Void
    var setStartAtLogin: (Bool) -> Void
    var checkForUpdates: () -> Void
    var installUpdate: () -> Void
    var openLog: () -> Void

    init(
        save: @escaping (AppSettings, AppSettings) -> String?,
        discard: @escaping () -> Void = {},
        installLocalModel: @escaping (String) -> Void = { _ in },
        removeLocalModel: @escaping (String) -> Void = { _ in },
        cancelLocalModelInstall: @escaping () -> Void = {},
        suggestVocabulary: @escaping (AppSettings) -> Void = { _ in },
        testLLM: @escaping (AppSettings) -> Void = { _ in },
        setStartAtLogin: @escaping (Bool) -> Void = { _ in },
        checkForUpdates: @escaping () -> Void = {},
        installUpdate: @escaping () -> Void = {},
        openLog: @escaping () -> Void = {}
    ) {
        self.save = save
        self.discard = discard
        self.installLocalModel = installLocalModel
        self.removeLocalModel = removeLocalModel
        self.cancelLocalModelInstall = cancelLocalModelInstall
        self.suggestVocabulary = suggestVocabulary
        self.testLLM = testLLM
        self.setStartAtLogin = setStartAtLogin
        self.checkForUpdates = checkForUpdates
        self.installUpdate = installUpdate
        self.openLog = openLog
    }
}

private enum SettingsPage: Int, CaseIterable {
    case overview, models, profiles, vocabulary, refinement, app

    var title: String {
        switch self {
        case .overview: "Overview"
        case .models: "Model Selection"
        case .profiles: "Profiles"
        case .vocabulary: "Vocabulary"
        case .refinement: "Refinement"
        case .app: "App"
        }
    }

    var symbolName: String {
        switch self {
        case .overview: "house"
        case .models: "waveform.circle"
        case .profiles: "person.2"
        case .vocabulary: "textformat"
        case .refinement: "sparkles"
        case .app: "gearshape"
        }
    }
}

@MainActor
final class SettingsWindowController: NSWindowController, NSTextFieldDelegate, NSTextViewDelegate {
    private var original: AppSettings
    private var draft: AppSettings
    private var runtime: SettingsRuntimeState
    private let actions: SettingsViewActions

    private let pageHost = NSView()
    private var pages: [SettingsPage: NSView] = [:]
    private var navigation: [SettingsPage: NSButton] = [:]
    private let statusLabel = NSTextField(labelWithString: "Settings saved")
    private let discardButton = NSButton(title: "Close", target: nil, action: nil)
    private let saveButton = NSButton(title: "Save changes", target: nil, action: nil)

    private let overviewBanner = CardView()
    private let overviewReadinessTitle = NSTextField(labelWithString: "")
    private let overviewReadinessText = NSTextField(wrappingLabelWithString: "")
    private let overviewEngine = NSTextField(labelWithString: "")
    private let overviewModel = NSTextField(labelWithString: "")
    private let overviewLLM = NSTextField(labelWithString: "")
    private let overviewLocalStatus = NSTextField(labelWithString: "")
    private let overviewPTT = NSTextField(labelWithString: "")
    private let overviewLanguage = NSTextField(labelWithString: "")

    private let modelStatus = NSTextField(wrappingLabelWithString: "")
    private let enginePopup = NSPopUpButton()
    private let macFields = NSView()
    private let azureFields = NSView()
    private let transcribeFields = NSView()
    private let localFields = NSView()
    private let azureAuth = NSPopUpButton()
    private let azureKey = NSSecureTextField()
    private let azureRegion = NSTextField()
    private let azureEndpoint = NSTextField()
    private let azureTenant = NSTextField()
    private let azureResourceId = NSTextField()
    private let transcribeAuth = NSPopUpButton()
    private let transcribeEndpoint = NSTextField()
    private let transcribeKind = NSPopUpButton()
    private let transcribeDeployment = NSTextField()
    private let transcribeKey = NSSecureTextField()
    private let transcribeTenant = NSTextField()
    private let localModelsStack = VerticalFillStackView()
    private let localRuntimeLabel = NSTextField(wrappingLabelWithString: "")
    private var azureKeyRows: [NSView] = []
    private var azureEntraRows: [NSView] = []
    private var transcribeKeyRows: [NSView] = []
    private var transcribeEntraRows: [NSView] = []

    private struct ProfileFields {
        let active: NSButton
        let name: NSTextField
        let key: NSPopUpButton
        let mode: NSPopUpButton
        let overlay: NSPopUpButton
    }
    private var profileFields: [ProfileFields] = []

    private let vocabularyEngine = NSTextField(labelWithString: "")
    private let vocabularyMode = NSTextField(wrappingLabelWithString: "")
    private let vocabularyText = NSTextView()
    private let vocabularyCount = NSTextField(labelWithString: "")
    private let vocabularySuggestionStatus = NSTextField(wrappingLabelWithString: "")

    private let llmEnabled = NSButton(checkboxWithTitle: "Enable LLM refinement", target: nil, action: nil)
    private let llmBaseURL = NSTextField()
    private let llmKey = NSSecureTextField()
    private let llmModel = NSTextField()
    private let llmPrompt = NSTextView()
    private let llmStatus = NSTextField(wrappingLabelWithString: "")

    private let language = NSPopUpButton()
    private let useContext = NSButton(checkboxWithTitle: "Use surrounding app context for LLM refinement", target: nil, action: nil)
    private let learnEdits = NSButton(checkboxWithTitle: "Learn correction rules from my edits", target: nil, action: nil)
    private let diagnostic = NSButton(checkboxWithTitle: "Log transcript, vocabulary, and LLM output locally for diagnostics", target: nil, action: nil)
    private let startAtLogin = NSButton(checkboxWithTitle: "Start gujiguji when I sign in", target: nil, action: nil)
    private let updateButton = NSButton(title: "Update now", target: nil, action: nil)

    init(settings: AppSettings, runtime: SettingsRuntimeState = SettingsRuntimeState(), actions: SettingsViewActions) {
        var normalized = settings
        normalized.normalize()
        original = normalized
        draft = normalized
        self.runtime = runtime
        self.actions = actions

        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 900, height: 650),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.title = "gujiguji Setup"
        window.minSize = NSSize(width: 720, height: 520)
        window.isReleasedWhenClosed = false
        super.init(window: window)
        buildWindow()
        loadDraftIntoControls()
        selectPage(.overview)
        window.center()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func updateRuntime(_ state: SettingsRuntimeState) {
        runtime = state
        statusLabel.stringValue = state.statusMessage
        startAtLogin.state = state.startAtLogin ? .on : .off
        updateButton.isHidden = !state.updateAvailable
        rebuildLocalModels()
        refreshComputedUI()
    }

    func setVocabularySuggestionStatus(_ message: String, success: Bool = false) {
        vocabularySuggestionStatus.stringValue = message
        vocabularySuggestionStatus.textColor = success ? ViewPalette.success : ViewPalette.muted
    }

    func applyVocabularySuggestions(_ suggestions: [String]) {
        pullDraftFromControls()
        let existing = Set(draft.recognitionVocabulary.map { $0.lowercased() })
        let normalizedSuggestions = RecognitionVocabulary.normalize(suggestions)
        let added = normalizedSuggestions.filter { !existing.contains($0.lowercased()) }
        draft.recognitionVocabulary = RecognitionVocabulary.normalize(draft.recognitionVocabulary + added)
        vocabularyText.string = draft.recognitionVocabulary.joined(separator: "\n")
        setVocabularySuggestionStatus(
            added.isEmpty ? "No new recurring terms found." : "Added \(added.count) suggested terms.",
            success: !added.isEmpty)
        refreshComputedUI()
    }

    func setLLMTestStatus(_ message: String, success: Bool = false) {
        llmStatus.stringValue = message
        llmStatus.textColor = success ? ViewPalette.success : ViewPalette.muted
    }

    private func buildWindow() {
        guard let root = window?.contentView else { return }
        root.wantsLayer = true
        root.layer?.backgroundColor = ViewPalette.background.cgColor
        root.identifier = NSUserInterfaceItemIdentifier("settings.root")

        let sidebar = buildSidebar()
        let footer = buildFooter()
        pageHost.translatesAutoresizingMaskIntoConstraints = false
        pageHost.identifier = NSUserInterfaceItemIdentifier("settings.page-host")
        root.addSubview(sidebar)
        root.addSubview(pageHost)
        root.addSubview(footer)

        NSLayoutConstraint.activate([
            sidebar.leadingAnchor.constraint(equalTo: root.leadingAnchor),
            sidebar.topAnchor.constraint(equalTo: root.topAnchor),
            sidebar.bottomAnchor.constraint(equalTo: root.bottomAnchor),
            sidebar.widthAnchor.constraint(equalToConstant: 184),

            pageHost.leadingAnchor.constraint(equalTo: sidebar.trailingAnchor, constant: 28),
            pageHost.trailingAnchor.constraint(equalTo: root.trailingAnchor, constant: -28),
            pageHost.topAnchor.constraint(equalTo: root.topAnchor, constant: 21),
            pageHost.bottomAnchor.constraint(equalTo: footer.topAnchor, constant: -16),

            footer.leadingAnchor.constraint(equalTo: sidebar.trailingAnchor),
            footer.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            footer.bottomAnchor.constraint(equalTo: root.bottomAnchor),
            footer.heightAnchor.constraint(equalToConstant: 64),
        ])

        pages[.overview] = buildOverviewPage()
        pages[.models] = buildModelsPage()
        pages[.profiles] = buildProfilesPage()
        pages[.vocabulary] = buildVocabularyPage()
        pages[.refinement] = buildRefinementPage()
        pages[.app] = buildAppPage()
        for (pageKind, page) in pages {
            page.identifier = NSUserInterfaceItemIdentifier("settings.page.\(pageKind.rawValue)")
            page.translatesAutoresizingMaskIntoConstraints = false
            pageHost.addSubview(page)
            NSLayoutConstraint.activate([
                page.leadingAnchor.constraint(equalTo: pageHost.leadingAnchor),
                page.trailingAnchor.constraint(equalTo: pageHost.trailingAnchor),
                page.topAnchor.constraint(equalTo: pageHost.topAnchor),
                page.bottomAnchor.constraint(equalTo: pageHost.bottomAnchor),
            ])
        }
    }

    private func buildSidebar() -> NSView {
        let sidebar = NSView()
        sidebar.translatesAutoresizingMaskIntoConstraints = false
        sidebar.identifier = NSUserInterfaceItemIdentifier("settings.sidebar")
        sidebar.wantsLayer = true
        sidebar.layer?.backgroundColor = ViewPalette.sidebar.cgColor

        let brand = smallLabel("gujiguji", color: .secondaryLabelColor, weight: .semibold)
        let stack = VerticalFillStackView()
        stack.spacing = 3
        stack.translatesAutoresizingMaskIntoConstraints = false
        sidebar.addSubview(stack)
        stack.addArrangedSubview(brand)
        stack.setCustomSpacing(12, after: brand)

        for page in SettingsPage.allCases {
            let button = NSButton(title: page.title, target: self, action: #selector(navigate(_:)))
            button.tag = page.rawValue
            button.identifier = NSUserInterfaceItemIdentifier("settings.navigation.\(page.rawValue)")
            button.isBordered = false
            button.alignment = .left
            button.font = .systemFont(ofSize: 13)
            button.contentTintColor = ViewPalette.text
            if let image = navigationSymbolImage(page.symbolName, description: page.title) {
                button.image = image
                button.imagePosition = .imageLeading
                button.imageHugsTitle = true
            }
            button.heightAnchor.constraint(equalToConstant: 40).isActive = true
            stack.addArrangedSubview(button)
            navigation[page] = button
        }

        let local = smallLabel("Settings are stored locally.", color: .secondaryLabelColor)
        let version = smallLabel("Version \(runtime.appVersion)", color: .secondaryLabelColor)
        let bottom = vStack([local, version], spacing: 3)
        bottom.translatesAutoresizingMaskIntoConstraints = false
        sidebar.addSubview(bottom)

        let separator = NSBox()
        separator.boxType = .separator
        separator.translatesAutoresizingMaskIntoConstraints = false
        sidebar.addSubview(separator)

        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: sidebar.leadingAnchor, constant: 11),
            stack.trailingAnchor.constraint(equalTo: sidebar.trailingAnchor, constant: -11),
            stack.topAnchor.constraint(equalTo: sidebar.topAnchor, constant: 19),
            bottom.leadingAnchor.constraint(equalTo: sidebar.leadingAnchor, constant: 21),
            bottom.trailingAnchor.constraint(lessThanOrEqualTo: sidebar.trailingAnchor, constant: -12),
            bottom.bottomAnchor.constraint(equalTo: sidebar.bottomAnchor, constant: -14),
            separator.trailingAnchor.constraint(equalTo: sidebar.trailingAnchor),
            separator.topAnchor.constraint(equalTo: sidebar.topAnchor),
            separator.bottomAnchor.constraint(equalTo: sidebar.bottomAnchor),
        ])
        return sidebar
    }

    private func buildFooter() -> NSView {
        let footer = NSView()
        footer.translatesAutoresizingMaskIntoConstraints = false
        footer.identifier = NSUserInterfaceItemIdentifier("settings.footer")
        footer.wantsLayer = true
        footer.layer?.backgroundColor = ViewPalette.footer.cgColor

        statusLabel.textColor = ViewPalette.muted
        statusLabel.font = .systemFont(ofSize: 12)
        statusLabel.lineBreakMode = .byTruncatingTail
        statusLabel.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        statusLabel.identifier = NSUserInterfaceItemIdentifier("settings.footer.status")
        statusLabel.translatesAutoresizingMaskIntoConstraints = false
        footer.addSubview(statusLabel)

        discardButton.target = self
        discardButton.action = #selector(discardPressed)
        discardButton.bezelStyle = .rounded
        discardButton.identifier = NSUserInterfaceItemIdentifier("settings.footer.close")
        saveButton.target = self
        saveButton.action = #selector(savePressed)
        saveButton.bezelStyle = .rounded
        saveButton.keyEquivalent = "\r"
        saveButton.contentTintColor = ViewPalette.accent
        saveButton.identifier = NSUserInterfaceItemIdentifier("settings.footer.save")
        let buttons = hStack([discardButton, saveButton], spacing: 8)
        buttons.setContentHuggingPriority(.required, for: .horizontal)
        buttons.setContentCompressionResistancePriority(.required, for: .horizontal)
        buttons.translatesAutoresizingMaskIntoConstraints = false
        footer.addSubview(buttons)

        let separator = NSBox()
        separator.boxType = .separator
        separator.translatesAutoresizingMaskIntoConstraints = false
        footer.addSubview(separator)

        NSLayoutConstraint.activate([
            statusLabel.leadingAnchor.constraint(equalTo: footer.leadingAnchor, constant: 28),
            statusLabel.centerYAnchor.constraint(equalTo: footer.centerYAnchor),
            statusLabel.trailingAnchor.constraint(lessThanOrEqualTo: buttons.leadingAnchor, constant: -12),
            buttons.trailingAnchor.constraint(equalTo: footer.trailingAnchor, constant: -28),
            buttons.centerYAnchor.constraint(equalTo: footer.centerYAnchor),
            discardButton.widthAnchor.constraint(greaterThanOrEqualToConstant: 86),
            saveButton.widthAnchor.constraint(greaterThanOrEqualToConstant: 112),
            separator.leadingAnchor.constraint(equalTo: footer.leadingAnchor),
            separator.trailingAnchor.constraint(equalTo: footer.trailingAnchor),
            separator.topAnchor.constraint(equalTo: footer.topAnchor),
        ])
        return footer
    }

    private func buildOverviewPage() -> NSView {
        overviewReadinessTitle.font = .systemFont(ofSize: 13, weight: .semibold)
        overviewReadinessText.textColor = ViewPalette.muted
        overviewReadinessText.font = .systemFont(ofSize: 12)
        overviewReadinessText.setContentHuggingPriority(.defaultLow, for: .horizontal)
        overviewReadinessText.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        let choose = NSButton(title: "Choose a model", target: self, action: #selector(chooseModel))
        choose.setContentHuggingPriority(.required, for: .horizontal)
        choose.setContentCompressionResistancePriority(.required, for: .horizontal)
        let readinessText = vStack([overviewReadinessTitle, overviewReadinessText], spacing: 3)
        readinessText.setContentHuggingPriority(.defaultLow, for: .horizontal)
        readinessText.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        let readiness = hStack([
            readinessText, choose,
        ], spacing: 14)
        overviewBanner.setContent(readiness, fill: ViewPalette.successSoft, border: ViewPalette.successBorder, leadingAccent: ViewPalette.accent)
        overviewBanner.identifier = NSUserInterfaceItemIdentifier("settings.overview.banner")

        let active = valueCard(title: "Active speech engine", subtitle: "Used for new dictation sessions", value: overviewEngine)
        active.identifier = NSUserInterfaceItemIdentifier("settings.overview.engine")
        let summary = CardView(content: equalHStack([
            metric("Local model", overviewModel),
            metric("LLM refinement", overviewLLM),
            metric("Local readiness", overviewLocalStatus),
        ], spacing: 18))
        summary.identifier = NSUserInterfaceItemIdentifier("settings.overview.summary")
        let input = CardView(content: equalHStack([
            metric("Voice activation", overviewPTT),
            metric("Recognition language", overviewLanguage),
        ], spacing: 18))
        input.identifier = NSUserInterfaceItemIdentifier("settings.overview.input")

        return makePage(title: "Overview", subtitle: "Speech and refinement status", views: [
            overviewBanner, active, summary, input,
        ])
    }

    private func buildModelsPage() -> NSView {
        enginePopup.addItems(withTitles: [
            "macOS Speech (built in)", "Azure Speech (cloud)",
            "GPT-4o Transcribe (cloud)", "Local models (on device)",
        ])
        observe(enginePopup)
        let engineRow = fieldRow("Speech engine", enginePopup)

        modelStatus.font = .systemFont(ofSize: 12)
        let status = CardView(content: modelStatus, fill: ViewPalette.successSoft, border: ViewPalette.successBorder, leadingAccent: ViewPalette.accent)

        let macNotice = NSTextField(wrappingLabelWithString: "No setup required. Recognition runs through the built-in Apple Speech service.")
        macNotice.textColor = ViewPalette.amber
        installContent(vStack([sectionTitle("macOS Speech"), CardView(content: macNotice, fill: ViewPalette.amberSoft, border: ViewPalette.amberBorder)], spacing: 9), in: macFields)

        azureAuth.addItems(withTitles: ["Key (account key)", "Microsoft Entra ID"])
        [azureKey, azureRegion, azureEndpoint, azureTenant, azureResourceId].forEach(observe)
        observe(azureAuth)
        azureResourceId.placeholderString = "/subscriptions/…/resourceGroups/…/providers/Microsoft.CognitiveServices/accounts/…"
        let azureKeyRow = fieldRow("Account key", azureKey)
        let azureRegionRow = fieldRow("Region", azureRegion)
        let azureEndpointRow = fieldRow("Endpoint", azureEndpoint)
        let azureTenantRow = fieldRow("Tenant ID", azureTenant)
        let azureResourceRow = fieldRow("Azure Resource ID", azureResourceId)
        azureKeyRows = [azureKeyRow, azureRegionRow]
        azureEntraRows = [azureEndpointRow, azureTenantRow, azureResourceRow]
        installContent(vStack([
            sectionTitle("Azure Speech"), fieldRow("Authentication", azureAuth),
            azureKeyRow, azureRegionRow,
            azureEndpointRow, azureTenantRow, azureResourceRow,
        ], spacing: 8), in: azureFields)

        transcribeAuth.addItems(withTitles: ["Key (account key)", "Microsoft Entra ID"])
        transcribeKind.addItems(withTitles: [
            "GPT-4o Transcribe", "GPT-4o Mini Transcribe",
            "GPT-4o Transcribe Diarize (vocabulary unavailable)", "Other / unknown",
        ])
        [transcribeEndpoint, transcribeDeployment, transcribeKey, transcribeTenant].forEach(observe)
        observe(transcribeAuth)
        observe(transcribeKind)
        let transcribeKeyRow = fieldRow("API key", transcribeKey)
        let transcribeTenantRow = fieldRow("Tenant ID", transcribeTenant)
        transcribeKeyRows = [transcribeKeyRow]
        transcribeEntraRows = [transcribeTenantRow]
        installContent(vStack([
            sectionTitle("Foundry transcription"), fieldRow("Authentication", transcribeAuth),
            fieldRow("Endpoint", transcribeEndpoint), fieldRow("Model", transcribeKind),
            fieldRow("Deployment", transcribeDeployment), transcribeKeyRow,
            transcribeTenantRow,
        ], spacing: 8), in: transcribeFields)

        localRuntimeLabel.font = .systemFont(ofSize: 11)
        localRuntimeLabel.textColor = ViewPalette.muted
        let runtimeCard = CardView(content: hStack([
            vStack([boldLabel("Local runtime"), localRuntimeLabel], spacing: 2),
            flexibleSpace(), accentLabel("No Python required"),
        ], spacing: 14), fill: ViewPalette.successSoft, border: ViewPalette.successBorder)
        localModelsStack.spacing = 8
        installContent(vStack([
            sectionTitle("Local models"),
            mutedWrappingLabel("Download only when needed. Packages are pinned and SHA-256 verified before use; downloading does not change your active model."),
            runtimeCard, localModelsStack,
        ], spacing: 9), in: localFields)
        rebuildLocalModels()

        return makePage(title: "Model Selection", subtitle: "Choose the recognition service or local model used for new dictation sessions.", views: [
            status, engineRow, macFields, azureFields, transcribeFields, localFields,
        ])
    }

    private func buildProfilesPage() -> NSView {
        profileFields.removeAll()
        let shortcut = fieldRow("Profile switch shortcut", boldLabel("Option + Shift + G"))
        var cards: [NSView] = [shortcut]
        for index in 0..<2 {
            let active = NSButton(radioButtonWithTitle: "Active profile", target: self, action: #selector(profileActiveChanged(_:)))
            active.tag = index
            let name = NSTextField()
            name.placeholderString = index == 0 ? "Desktop" : "Mobile"
            name.delegate = self
            name.tag = 100 + index
            let key = NSPopUpButton()
            key.addItems(withTitles: ActivationKey.allCases.map(\.displayName))
            key.tag = 200 + index
            observe(key)
            let mode = NSPopUpButton()
            mode.addItems(withTitles: ["Hold to talk", "Press once to start / stop"])
            mode.tag = 300 + index
            observe(mode)
            let overlay = NSPopUpButton()
            overlay.addItems(withTitles: ["Top", "Bottom"])
            overlay.tag = 400 + index
            observe(overlay)
            let fields = ProfileFields(active: active, name: name, key: key, mode: mode, overlay: overlay)
            profileFields.append(fields)

            let profileTitle = boldLabel("Profile \(index + 1)", size: 15)
            profileTitle.setContentHuggingPriority(.defaultLow, for: .horizontal)
            active.setContentHuggingPriority(.required, for: .horizontal)
            active.setContentCompressionResistancePriority(.required, for: .horizontal)
            let heading = hStack([profileTitle, active], spacing: 8)

            let activationControl = labeledControl("Activation key", key)
            let behaviorControl = labeledControl("Behavior", mode)
            let overlayControl = labeledControl("Overlay", overlay)
            let controls = hStack([activationControl, behaviorControl, overlayControl], spacing: 10)
            behaviorControl.widthAnchor.constraint(equalTo: activationControl.widthAnchor, multiplier: 1.65 / 1.05).isActive = true
            overlayControl.widthAnchor.constraint(equalTo: activationControl.widthAnchor, multiplier: 0.85 / 1.05).isActive = true
            let card = CardView(content: vStack([heading, fieldRow("Name", name, labelWidth: 92), controls], spacing: 8))
            cards.append(card)
        }
        return makePage(
            title: "Input Profiles",
            subtitle: "Activation and overlay presets for each macOS setup",
            views: cards
        )
    }

    private func buildVocabularyPage() -> NSView {
        vocabularyEngine.font = .systemFont(ofSize: 13, weight: .semibold)
        vocabularyMode.font = .systemFont(ofSize: 12)
        vocabularyMode.setContentHuggingPriority(.defaultLow, for: .horizontal)
        vocabularyMode.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        let engineMetric = metric("Current speech engine", vocabularyEngine)
        engineMetric.widthAnchor.constraint(equalToConstant: 135).isActive = true
        let engineCard = CardView(content: hStack([
            engineMetric, vocabularyMode,
        ], spacing: 18), fill: ViewPalette.successSoft, border: ViewPalette.successBorder, leadingAccent: ViewPalette.accent)

        vocabularyText.delegate = self
        vocabularyText.font = .systemFont(ofSize: 13)
        let vocabularyScroll = textEditor(vocabularyText, height: 130)
        vocabularyCount.font = .systemFont(ofSize: 11)
        vocabularyCount.textColor = ViewPalette.muted
        let countRow = hStack([vocabularyCount, flexibleSpace(), smallLabel("Stored locally · no local limit", color: ViewPalette.muted)], spacing: 8)
        let suggest = NSButton(title: "Suggest terms from edits", target: self, action: #selector(suggestVocabulary))
        suggest.setContentHuggingPriority(.required, for: .horizontal)
        suggest.setContentCompressionResistancePriority(.required, for: .horizontal)
        vocabularySuggestionStatus.font = .systemFont(ofSize: 11)
        vocabularySuggestionStatus.textColor = ViewPalette.muted
        vocabularySuggestionStatus.setContentHuggingPriority(.defaultLow, for: .horizontal)
        vocabularySuggestionStatus.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        let supported = mutedWrappingLabel("macOS Speech and Azure Speech (first 500), GPT-4o Transcribe and GPT-4o Mini Transcribe (prompt)")
        let unsupported = mutedWrappingLabel("GPT-4o Transcribe Diarize, unknown GPT deployments, FunASR models, and Qwen3-ASR models")
        let supportRows = vStack([
            fieldRow("Supported", supported, labelWidth: 112, alignment: .top),
            fieldRow("Not supported", unsupported, labelWidth: 112, alignment: .top),
        ], spacing: 6)

        return makePage(title: "Vocabulary", subtitle: "Shared recognition hints for supported speech engines", views: [
            engineCard,
            sectionTitle("Recognition terms"),
            mutedWrappingLabel("Separate terms with commas, semicolons, or new lines. Spaces inside a term are preserved."),
            vocabularyScroll, countRow,
            divider(), sectionTitle("Learn from edits"),
            mutedWrappingLabel("Ask your configured LLM to find recurring names and domain terms in locally stored correction samples."),
            hStack([suggest, vocabularySuggestionStatus], spacing: 12),
            divider(), sectionTitle("Model support"), supportRows,
        ])
    }

    private func buildRefinementPage() -> NSView {
        observe(llmEnabled)
        [llmBaseURL, llmKey, llmModel].forEach(observe)
        llmPrompt.delegate = self
        llmPrompt.font = .systemFont(ofSize: 13)
        let test = NSButton(title: "Test LLM", target: self, action: #selector(testLLM))
        llmStatus.font = .systemFont(ofSize: 11)
        llmStatus.textColor = ViewPalette.muted

        return makePage(title: "Refinement", subtitle: "Optional OpenAI-compatible text correction", views: [
            llmEnabled,
            fieldRow("API base URL", llmBaseURL), fieldRow("API key", llmKey),
            fieldRow("Model", llmModel),
            fieldRow("Custom prompt", textEditor(llmPrompt, height: 112), alignment: .top),
            hStack([spacer(width: 150), test, llmStatus], spacing: 12),
        ])
    }

    private func buildAppPage() -> NSView {
        language.addItems(withTitles: AppSettings.supportedLanguages.map(\.display))
        observe(language)
        [useContext, learnEdits, diagnostic].forEach(observe)
        startAtLogin.target = self
        startAtLogin.action = #selector(startAtLoginChanged)

        let check = NSButton(title: "Check for updates", target: self, action: #selector(checkUpdates))
        updateButton.target = self
        updateButton.action = #selector(installUpdate)
        updateButton.isHidden = !runtime.updateAvailable
        let log = NSButton(title: "Open log", target: self, action: #selector(openLog))
        return makePage(title: "App", subtitle: "Language, privacy, startup, and diagnostics", views: [
            sectionTitle("Dictation"), fieldRow("Language", language),
            divider(), sectionTitle("Privacy and learning"), useContext, learnEdits, diagnostic,
            smallLabel("Diagnostic logs stay on this device and are never uploaded to gujiguji servers.", color: ViewPalette.muted),
            divider(), sectionTitle("Maintenance"), startAtLogin,
            hStack([check, updateButton, log], spacing: 8),
        ])
    }

    private func loadDraftIntoControls() {
        enginePopup.selectItem(at: engineIndex(draft.engine))
        azureAuth.selectItem(at: draft.azureAuthMode == .key ? 0 : 1)
        azureKey.stringValue = draft.azureKey
        azureRegion.stringValue = draft.azureRegion
        azureEndpoint.stringValue = draft.azureEndpoint
        azureTenant.stringValue = draft.azureTenantId
        azureResourceId.stringValue = draft.azureResourceId
        transcribeAuth.selectItem(at: draft.transcribeAuthMode == .key ? 0 : 1)
        transcribeEndpoint.stringValue = draft.transcribeEndpoint
        transcribeKind.selectItem(at: transcribeKindIndex(draft.transcribeModelKind))
        transcribeDeployment.stringValue = draft.transcribeModel
        transcribeKey.stringValue = draft.transcribeApiKey
        transcribeTenant.stringValue = draft.transcribeTenantId

        for index in profileFields.indices where index < draft.profiles.count {
            let profile = draft.profiles[index]
            let fields = profileFields[index]
            fields.active.state = profile.id == draft.activeProfileId ? .on : .off
            fields.name.stringValue = profile.name
            fields.key.selectItem(at: ActivationKey.allCases.firstIndex(of: profile.activationKey) ?? 0)
            fields.mode.selectItem(at: profile.pttMode == .hold ? 0 : 1)
            fields.overlay.selectItem(at: profile.overlayPosition == .top ? 0 : 1)
        }

        vocabularyText.string = draft.recognitionVocabulary.joined(separator: ", ")
        llmEnabled.state = draft.llmEnabled ? .on : .off
        llmBaseURL.stringValue = draft.llmBaseUrl
        llmKey.stringValue = draft.llmApiKey
        llmModel.stringValue = draft.llmModel
        llmPrompt.string = draft.llmPrompt
        language.selectItem(at: AppSettings.supportedLanguages.firstIndex(where: { $0.code == draft.language }) ?? 1)
        useContext.state = draft.useContext ? .on : .off
        learnEdits.state = draft.learnFromEdits ? .on : .off
        diagnostic.state = draft.diagnosticLogging ? .on : .off
        startAtLogin.state = runtime.startAtLogin ? .on : .off
        statusLabel.stringValue = runtime.statusMessage
        refreshComputedUI()
    }

    private func pullDraftFromControls() {
        draft.engine = [SpeechEngineKind.macOS, .azure, .gptTranscribe, .funAsr][safe: enginePopup.indexOfSelectedItem] ?? .macOS
        draft.azureAuthMode = azureAuth.indexOfSelectedItem == 0 ? .key : .entraId
        draft.azureKey = azureKey.stringValue
        draft.azureRegion = azureRegion.stringValue
        draft.azureEndpoint = azureEndpoint.stringValue
        draft.azureTenantId = azureTenant.stringValue
        draft.azureResourceId = azureResourceId.stringValue
        draft.transcribeAuthMode = transcribeAuth.indexOfSelectedItem == 0 ? .key : .entraId
        draft.transcribeEndpoint = transcribeEndpoint.stringValue
        draft.transcribeModelKind = transcribeKindValue(transcribeKind.indexOfSelectedItem)
        draft.transcribeModel = transcribeDeployment.stringValue
        draft.transcribeApiKey = transcribeKey.stringValue
        draft.transcribeTenantId = transcribeTenant.stringValue

        for index in profileFields.indices where index < draft.profiles.count {
            let fields = profileFields[index]
            draft.profiles[index].name = fields.name.stringValue
            draft.profiles[index].activationKey = ActivationKey.allCases[safe: fields.key.indexOfSelectedItem] ?? .rightControl
            draft.profiles[index].pttMode = fields.mode.indexOfSelectedItem == 0 ? .hold : .toggle
            draft.profiles[index].overlayPosition = fields.overlay.indexOfSelectedItem == 0 ? .top : .bottom
            if fields.active.state == .on { draft.activeProfileId = draft.profiles[index].id }
        }

        draft.recognitionVocabulary = RecognitionVocabulary.parse(vocabularyText.string)
        draft.llmEnabled = llmEnabled.state == .on
        draft.llmBaseUrl = llmBaseURL.stringValue
        draft.llmApiKey = llmKey.stringValue
        draft.llmModel = llmModel.stringValue
        draft.llmPrompt = llmPrompt.string
        draft.language = AppSettings.supportedLanguages[safe: language.indexOfSelectedItem]?.code ?? "zh-CN"
        draft.useContext = useContext.state == .on
        draft.learnFromEdits = learnEdits.state == .on
        draft.diagnosticLogging = diagnostic.state == .on
    }

    private func refreshComputedUI() {
        let engine = engineDisplay(draft.engine)
        let selectedModel = runtime.localModels.first(where: { $0.id == draft.funAsrModelId })
        let installed = selectedModel?.isInstalled == true
        let localProblem = draft.engine == .funAsr && !installed

        overviewReadinessTitle.stringValue = localProblem ? "Local speech needs attention" : "gujiguji is ready"
        overviewReadinessText.stringValue = localProblem
            ? "Download \(selectedModel?.name ?? "the selected model") before starting a local dictation session."
            : "\(engine) is selected for new dictation sessions."
        overviewBanner.setColors(
            fill: localProblem ? ViewPalette.amberSoft : ViewPalette.successSoft,
            border: localProblem ? ViewPalette.amberBorder : ViewPalette.successBorder,
            leadingAccent: localProblem ? ViewPalette.amber : ViewPalette.accent
        )
        overviewEngine.stringValue = engine
        overviewEngine.textColor = ViewPalette.accent
        overviewModel.stringValue = selectedModel?.name ?? "Not selected"
        overviewLLM.stringValue = draft.llmEnabled ? "On" : "Off"
        let installedCount = runtime.localModels.filter(\.isInstalled).count
        overviewLocalStatus.stringValue = installedCount == 0 ? "No models installed" : "\(installedCount) installed"
        let active = draft.activeProfile
        overviewPTT.stringValue = "\(active.name) · \(active.activationKey.displayName) · \(active.pttMode == .hold ? "Hold" : "Toggle")"
        overviewLanguage.stringValue = AppSettings.supportedLanguages.first(where: { $0.code == draft.language })?.display ?? draft.language

        macFields.isHidden = draft.engine != .macOS
        azureFields.isHidden = draft.engine != .azure
        transcribeFields.isHidden = draft.engine != .gptTranscribe
        localFields.isHidden = draft.engine != .funAsr
        azureKeyRows.forEach { $0.isHidden = draft.azureAuthMode != .key }
        azureEntraRows.forEach { $0.isHidden = draft.azureAuthMode != .entraId }
        transcribeKeyRows.forEach { $0.isHidden = draft.transcribeAuthMode != .key }
        transcribeEntraRows.forEach { $0.isHidden = draft.transcribeAuthMode != .entraId }
        azureKey.isEnabled = draft.azureAuthMode == .key
        azureRegion.isEnabled = true
        azureEndpoint.isEnabled = draft.azureAuthMode == .entraId
        azureTenant.isEnabled = draft.azureAuthMode == .entraId
        azureResourceId.isEnabled = draft.azureAuthMode == .entraId
        transcribeKey.isEnabled = draft.transcribeAuthMode == .key
        transcribeTenant.isEnabled = draft.transcribeAuthMode == .entraId

        if let installing = runtime.installingModelId,
           let model = runtime.localModels.first(where: { $0.id == installing }) {
            let progress = runtime.installationProgress.map { " · \(Int($0 * 100))%" } ?? ""
            modelStatus.stringValue = "Installing \(model.name)\(progress). \(runtime.installationStatus)"
        } else if localProblem {
            modelStatus.stringValue = "Download \(selectedModel?.name ?? "the selected local model") before saving this selection."
        } else {
            modelStatus.stringValue = "\(engine) will be used for new dictation sessions."
        }
        localRuntimeLabel.stringValue = runtime.localRuntimeSummary

        let vocabularyModeText: String
        switch draft.engine {
        case .macOS: vocabularyModeText = "Used as macOS contextual strings (first 500)"
        case .azure: vocabularyModeText = "Used as an Azure Phrase List"
        case .gptTranscribe where draft.transcribeModelKind == .gpt4oTranscribe || draft.transcribeModelKind == .gpt4oMiniTranscribe:
            vocabularyModeText = "Used as a transcription prompt"
        case .funAsr where selectedLocalModelIsQwen:
            vocabularyModeText = "Qwen3-ASR auto-detects language; this runtime has no native hotword prompt"
        default: vocabularyModeText = "The selected engine does not use recognition vocabulary hints"
        }
        vocabularyEngine.stringValue = engine
        vocabularyMode.stringValue = vocabularyModeText
        vocabularyCount.stringValue = draft.recognitionVocabulary.count == 1 ? "1 term" : "\(draft.recognitionVocabulary.count) terms"

        let llmConfigured = LlmRefiner.isConfigured(draft)
        useContext.isEnabled = llmConfigured
        learnEdits.isEnabled = llmConfigured

        let dirty = draft != original
        saveButton.isEnabled = dirty && runtime.installingModelId == nil && !localProblem
        saveButton.title = localProblem ? "Model required" : "Save changes"
        discardButton.title = dirty ? "Discard changes" : "Close"
        if dirty && runtime.statusMessage == "Settings saved" {
            statusLabel.stringValue = localProblem ? "Install the selected local model to continue" : "Unsaved changes"
        } else {
            statusLabel.stringValue = runtime.statusMessage
        }
    }

    private func rebuildLocalModels() {
        localModelsStack.arrangedSubviews.forEach {
            localModelsStack.removeArrangedSubview($0)
            $0.removeFromSuperview()
        }
        let operationInProgress = runtime.installingModelId != nil
        for model in runtime.localModels {
            let selected = draft.funAsrModelId == model.id
            let installing = runtime.installingModelId == model.id
            let selectedForUse = selected && draft.engine == .funAsr
            let currentlyActive = original.engine == .funAsr && original.funAsrModelId == model.id
            let status = NSTextField(labelWithString: model.isInstalled ? "Installed" : model.downloadSize)
            status.font = .systemFont(ofSize: 11, weight: .semibold)
            status.textColor = model.isInstalled ? ViewPalette.success : ViewPalette.muted
            var heading: [NSView] = [boldLabel(model.name)]
            if model.isRecommended {
                heading.append(smallLabel("Recommended", color: ViewPalette.blue))
            }
            heading.append(status)
            let text = vStack([
                hStack(heading, spacing: 8),
                mutedWrappingLabel(model.detail),
            ], spacing: 3)
            text.setContentHuggingPriority(.defaultLow, for: .horizontal)
            text.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)

            let actionsStack = hStack([], spacing: 6)
            actionsStack.setContentHuggingPriority(.required, for: .horizontal)
            actionsStack.setContentCompressionResistancePriority(.required, for: .horizontal)
            if installing {
                let cancel = actionButton("Cancel", #selector(cancelInstall), representedObject: nil)
                actionsStack.addArrangedSubview(cancel)
            } else if model.isInstalled {
                if selectedForUse {
                    let active = NSButton(title: original.funAsrModelId == model.id && original.engine == .funAsr ? "Active" : "Will use after Save", target: nil, action: nil)
                    active.isEnabled = false
                    actionsStack.addArrangedSubview(active)
                } else {
                    let use = actionButton("Use after Save", #selector(useLocalModel(_:)), representedObject: model.id)
                    use.isEnabled = !operationInProgress
                    actionsStack.addArrangedSubview(use)
                }
                if !selectedForUse && !currentlyActive {
                    let remove = actionButton("Remove", #selector(removeLocalModel(_:)), representedObject: model.id)
                    remove.isEnabled = !operationInProgress
                    actionsStack.addArrangedSubview(remove)
                }
            } else {
                let download = actionButton("Download", #selector(installLocalModel(_:)), representedObject: model.id)
                download.isEnabled = !operationInProgress
                actionsStack.addArrangedSubview(download)
            }
            let card = CardView(content: hStack([text, actionsStack], spacing: 12))
            localModelsStack.addArrangedSubview(card)
        }
    }

    @objc private func navigate(_ sender: NSButton) {
        guard let page = SettingsPage(rawValue: sender.tag) else { return }
        selectPage(page)
    }

    private func selectPage(_ selected: SettingsPage) {
        for (page, view) in pages { view.isHidden = page != selected }
        for (page, button) in navigation {
            button.state = page == selected ? .on : .off
            button.wantsLayer = true
            button.layer?.cornerRadius = 4
            button.layer?.backgroundColor = page == selected ? ViewPalette.selectedNav.cgColor : NSColor.clear.cgColor
            button.contentTintColor = page == selected ? ViewPalette.accent : ViewPalette.text
            button.font = .systemFont(ofSize: 13, weight: page == selected ? .semibold : .regular)
        }
    }

    @objc private func fieldChanged(_ sender: Any?) {
        pullDraftFromControls()
        refreshComputedUI()
    }

    func controlTextDidChange(_ obj: Notification) { fieldChanged(obj.object) }
    func textDidChange(_ notification: Notification) { fieldChanged(notification.object) }

    @objc private func profileActiveChanged(_ sender: NSButton) {
        for (index, fields) in profileFields.enumerated() {
            fields.active.state = index == sender.tag ? .on : .off
        }
        fieldChanged(sender)
    }

    @objc private func savePressed() {
        pullDraftFromControls()
        var submitted = draft
        if let error = actions.save(submitted, original) {
            statusLabel.stringValue = error
            statusLabel.textColor = ViewPalette.error
            return
        }
        submitted.normalize()
        original = submitted
        draft = submitted
        loadDraftIntoControls()
        statusLabel.stringValue = "Settings saved"
        statusLabel.textColor = ViewPalette.muted
    }

    @objc private func discardPressed() {
        actions.discard()
        window?.close()
    }

    @objc private func chooseModel() { selectPage(.models) }
    @objc private func suggestVocabulary() {
        pullDraftFromControls()
        vocabularySuggestionStatus.stringValue = "Analyzing local correction samples…"
        actions.suggestVocabulary(draft)
    }
    @objc private func testLLM() {
        pullDraftFromControls()
        llmStatus.stringValue = "Testing…"
        actions.testLLM(draft)
    }
    @objc private func startAtLoginChanged() { actions.setStartAtLogin(startAtLogin.state == .on) }
    @objc private func checkUpdates() { actions.checkForUpdates() }
    @objc private func installUpdate() { actions.installUpdate() }
    @objc private func openLog() { actions.openLog() }
    @objc private func cancelInstall() { actions.cancelLocalModelInstall() }

    @objc private func installLocalModel(_ sender: NSButton) {
        guard let id = sender.identifier?.rawValue else { return }
        actions.installLocalModel(id)
    }

    @objc private func removeLocalModel(_ sender: NSButton) {
        guard let id = sender.identifier?.rawValue else { return }
        actions.removeLocalModel(id)
    }

    @objc private func useLocalModel(_ sender: NSButton) {
        guard let id = sender.identifier?.rawValue else { return }
        draft.funAsrModelId = id
        draft.engine = .funAsr
        enginePopup.selectItem(at: 3)
        rebuildLocalModels()
        refreshComputedUI()
    }

    private func observe(_ control: NSControl) {
        control.target = self
        control.action = #selector(fieldChanged(_:))
        if let text = control as? NSTextField { text.delegate = self }
    }

    private func actionButton(_ title: String, _ action: Selector, representedObject: Any?) -> NSButton {
        let button = NSButton(title: title, target: self, action: action)
        if let id = representedObject as? String {
            button.identifier = NSUserInterfaceItemIdentifier(id)
        }
        button.bezelStyle = .rounded
        return button
    }

    private func engineIndex(_ engine: SpeechEngineKind) -> Int {
        switch engine {
        case .macOS: 0
        case .azure: 1
        case .gptTranscribe: 2
        case .funAsr: 3
        }
    }

    private func transcribeKindIndex(_ kind: TranscribeModelKind) -> Int {
        switch kind {
        case .gpt4oTranscribe: 0
        case .gpt4oMiniTranscribe: 1
        case .gpt4oTranscribeDiarize: 2
        case .unknown: 3
        }
    }

    private func transcribeKindValue(_ index: Int) -> TranscribeModelKind {
        [.gpt4oTranscribe, .gpt4oMiniTranscribe, .gpt4oTranscribeDiarize, .unknown][safe: index] ?? .unknown
    }

    private func engineDisplay(_ engine: SpeechEngineKind) -> String {
        switch engine {
        case .macOS: "macOS Speech"
        case .azure: "Azure Speech"
        case .gptTranscribe: "GPT-4o Transcribe"
        case .funAsr:
            selectedLocalModelIsQwen
                ? "Qwen3-ASR (local)" : "FunASR (local)"
        }
    }

    private var selectedLocalModelIsQwen: Bool {
        (try? FunAsrCatalog.model(draft.funAsrModelId).runner) == .qwen3Asr
    }
}

// MARK: - First-run onboarding public surface

enum OnboardingPermissionKind: Int {
    case microphone
    case accessibility
}

enum OnboardingPermissionState: Equatable {
    case unknown
    case granted
    case denied
}

enum OnboardingCompletionChoice {
    case configured
    case defaultLocal
    case macOSFallback
}

struct OnboardingViewState: Equatable {
    var microphone: OnboardingPermissionState = .unknown
    var accessibility: OnboardingPermissionState = .unknown
    var recognitionReady = false
    var usesConfiguredRecognition = false
    var recognitionSummary = "本地识别 · Qwen3-ASR 0.6B"
    var installingLocalModel = false
    var installationProgress: Double?
    var installationStatus = "下载 Qwen3-ASR 0.6B（约 850 MB），使用 Metal/CPU 本地识别，语音不会上传。"
}

struct OnboardingViewActions {
    var requestPermission: (OnboardingPermissionKind) -> Void
    var installLocalModel: () -> Void
    var cancelLocalModelInstall: () -> Void
    var setPTTMode: (PttMode) -> Void
    var complete: (OnboardingCompletionChoice, PttMode) -> Bool
    var openSettings: () -> Void

    init(
        requestPermission: @escaping (OnboardingPermissionKind) -> Void = { _ in },
        installLocalModel: @escaping () -> Void = {},
        cancelLocalModelInstall: @escaping () -> Void = {},
        setPTTMode: @escaping (PttMode) -> Void = { _ in },
        complete: @escaping (OnboardingCompletionChoice, PttMode) -> Bool,
        openSettings: @escaping () -> Void = {}
    ) {
        self.requestPermission = requestPermission
        self.installLocalModel = installLocalModel
        self.cancelLocalModelInstall = cancelLocalModelInstall
        self.setPTTMode = setPTTMode
        self.complete = complete
        self.openSettings = openSettings
    }
}

private enum OnboardingPracticeStage: Equatable {
    case awaitingFocus
    case readyToTalk
    case listening
    case processing
    case complete
}

@MainActor
final class OnboardingWindowController: NSWindowController, NSTextViewDelegate {
    private var profile: InputProfile
    private var state: OnboardingViewState
    private let actions: OnboardingViewActions
    private var page = 0

    private let progressSecondHalf = NSView()
    private let stepCount = NSTextField(labelWithString: "1 / 2")
    private let pageHost = NSView()
    private let practicePage = NSView()
    private let completionPage = NSView()
    private let practiceFooter = NSView()
    private let completionFooter = NSView()

    private let microphoneStatus = NSTextField(labelWithString: "")
    private let accessibilityStatus = NSTextField(labelWithString: "")
    private let microphoneButton = NSButton(title: "允许", target: nil, action: nil)
    private let accessibilityButton = NSButton(title: "打开系统设置", target: nil, action: nil)
    private let modelTitle = NSTextField(labelWithString: "使用本地 Qwen3-ASR（推荐）")
    private let modelStatus = NSTextField(wrappingLabelWithString: "")
    private let modelProgress = NSProgressIndicator()
    private let installButton = NSButton(title: "下载并使用", target: nil, action: nil)
    private let cancelInstallButton = NSButton(title: "取消", target: nil, action: nil)
    private let holdRadio = NSButton(radioButtonWithTitle: "按住说话", target: nil, action: nil)
    private let toggleRadio = NSButton(radioButtonWithTitle: "按一下开始 / 再按一下结束", target: nil, action: nil)
    private let practiceText = NSTextView()
    private let practiceSteps = PracticeStepsView()
    private let practiceBars = PracticeBarsView()
    private let practiceKeycap = KeycapView(text: "")
    private let practiceStatusDot = PracticeStatusDotView()
    private let practiceStatus = NSTextField(labelWithString: "先点击上面的文本框")
    private let practiceDetail = NSTextField(wrappingLabelWithString: "光标出现后，再使用说话键。")
    private lazy var practiceNotepad = PracticeNotepadView(textView: practiceText)
    private var practiceStage = OnboardingPracticeStage.awaitingFocus
    private let continueButton = NSButton(title: "继续", target: nil, action: nil)
    private let skipButton = NSButton(title: "跳过演练", target: nil, action: nil)
    private let completionSummary = NSTextField(wrappingLabelWithString: "")
    private let completionTalk = NSTextField(labelWithString: "")
    private let completionSubmit = NSTextField(labelWithString: "")

    init(profile: InputProfile, state: OnboardingViewState = OnboardingViewState(), actions: OnboardingViewActions) {
        self.profile = profile
        self.state = state
        self.actions = actions
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 900, height: 650),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.title = "gujiguji 快速设置"
        window.minSize = NSSize(width: 720, height: 560)
        window.isReleasedWhenClosed = false
        super.init(window: window)
        practiceKeycap.update(text: profile.activationKey.displayName)
        buildOnboarding()
        applyPTTModeText()
        refreshOnboardingState()
        showPage(0)
        window.center()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func updateState(_ state: OnboardingViewState, profile: InputProfile? = nil) {
        self.state = state
        if let profile {
            self.profile = profile
            practiceKeycap.update(text: profile.activationKey.displayName)
            applyPTTModeText()
        }
        refreshOnboardingState()
    }

    /// Called after AppModel routes a real recognition result back into the practice target.
    func insertPracticeTranscript(_ transcript: String) {
        guard !transcript.isEmpty else { return }
        if !practiceText.string.isEmpty, !practiceText.string.hasSuffix(" ") { practiceText.string += " " }
        practiceText.string += transcript
        refreshPracticeCompletion()
    }

    func updatePractice(status: String, detail: String, listening: Bool) {
        let stage: OnboardingPracticeStage
        if status.contains("已输入") || status.contains("完成") {
            stage = .complete
        } else if status.contains("处理") || status.contains("识别") {
            stage = .processing
        } else if listening || status.contains("聆听") || status.contains("准备") {
            stage = .listening
        } else if status.contains("取消") || status.contains("暂停") {
            stage = .readyToTalk
        } else {
            stage = .awaitingFocus
        }

        let dotColor: NSColor
        if stage == .complete {
            dotColor = ViewPalette.success
        } else if listening {
            dotColor = ViewPalette.onboardingAccent
        } else if status.contains("取消") || status.contains("暂停") || status.contains("失败") {
            dotColor = ViewPalette.amber
        } else if stage == .processing || stage == .readyToTalk {
            dotColor = ViewPalette.blue
        } else {
            dotColor = ViewPalette.color(0x9DA8AD)
        }

        setPracticeStatus(status, detail: detail, dotColor: dotColor)
        setPracticeStage(stage, listening: listening)
        practiceBars.setActive(listening)
    }

    private func buildOnboarding() {
        guard let root = window?.contentView else { return }
        root.wantsLayer = true
        root.layer?.backgroundColor = NSColor.white.cgColor

        let header = buildOnboardingHeader()
        let progress = buildProgressBar()
        let footerHost = NSView()
        [header, progress, pageHost, footerHost].forEach {
            $0.translatesAutoresizingMaskIntoConstraints = false
            root.addSubview($0)
        }
        pageHost.addSubview(practicePage)
        pageHost.addSubview(completionPage)
        practicePage.translatesAutoresizingMaskIntoConstraints = false
        completionPage.translatesAutoresizingMaskIntoConstraints = false
        for view in [practicePage, completionPage] {
            NSLayoutConstraint.activate([
                view.leadingAnchor.constraint(equalTo: pageHost.leadingAnchor),
                view.trailingAnchor.constraint(equalTo: pageHost.trailingAnchor),
                view.topAnchor.constraint(equalTo: pageHost.topAnchor),
                view.bottomAnchor.constraint(equalTo: pageHost.bottomAnchor),
            ])
        }
        installContent(buildPracticePage(), in: practicePage)
        installContent(buildCompletionPage(), in: completionPage)

        footerHost.wantsLayer = true
        footerHost.layer?.backgroundColor = ViewPalette.onboardingFooter.cgColor
        footerHost.layer?.borderColor = ViewPalette.line.cgColor
        footerHost.layer?.borderWidth = 1
        [practiceFooter, completionFooter].forEach {
            $0.translatesAutoresizingMaskIntoConstraints = false
            footerHost.addSubview($0)
            NSLayoutConstraint.activate([
                $0.leadingAnchor.constraint(equalTo: footerHost.leadingAnchor, constant: 24),
                $0.trailingAnchor.constraint(equalTo: footerHost.trailingAnchor, constant: -24),
                $0.topAnchor.constraint(equalTo: footerHost.topAnchor),
                $0.bottomAnchor.constraint(equalTo: footerHost.bottomAnchor),
            ])
        }
        buildPracticeFooter()
        buildCompletionFooter()

        NSLayoutConstraint.activate([
            header.leadingAnchor.constraint(equalTo: root.leadingAnchor), header.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            header.topAnchor.constraint(equalTo: root.topAnchor), header.heightAnchor.constraint(equalToConstant: 54),
            progress.leadingAnchor.constraint(equalTo: root.leadingAnchor), progress.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            progress.topAnchor.constraint(equalTo: header.bottomAnchor), progress.heightAnchor.constraint(equalToConstant: 4),
            pageHost.leadingAnchor.constraint(equalTo: root.leadingAnchor), pageHost.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            pageHost.topAnchor.constraint(equalTo: progress.bottomAnchor), pageHost.bottomAnchor.constraint(equalTo: footerHost.topAnchor),
            footerHost.leadingAnchor.constraint(equalTo: root.leadingAnchor), footerHost.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            footerHost.bottomAnchor.constraint(equalTo: root.bottomAnchor), footerHost.heightAnchor.constraint(equalToConstant: 64),
        ])
    }

    private func buildOnboardingHeader() -> NSView {
        let header = NSView()
        let logo = WaveLogoView()
        logo.translatesAutoresizingMaskIntoConstraints = false
        let brand = boldLabel("gujiguji", size: 15)
        let runningDot = NSView()
        runningDot.wantsLayer = true
        runningDot.layer?.backgroundColor = ViewPalette.success.cgColor
        runningDot.layer?.cornerRadius = 4
        runningDot.translatesAutoresizingMaskIntoConstraints = false
        stepCount.font = .systemFont(ofSize: 12, weight: .semibold)
        stepCount.textColor = ViewPalette.muted
        let content = hStack([
            logo, brand, flexibleSpace(), runningDot,
            smallLabel("正在菜单栏运行", color: ViewPalette.muted), spacer(width: 12), stepCount,
        ], spacing: 10)
        content.translatesAutoresizingMaskIntoConstraints = false
        header.addSubview(content)
        NSLayoutConstraint.activate([
            logo.widthAnchor.constraint(equalToConstant: 30), logo.heightAnchor.constraint(equalToConstant: 30),
            runningDot.widthAnchor.constraint(equalToConstant: 8), runningDot.heightAnchor.constraint(equalToConstant: 8),
            content.leadingAnchor.constraint(equalTo: header.leadingAnchor, constant: 24),
            content.trailingAnchor.constraint(equalTo: header.trailingAnchor, constant: -24),
            content.centerYAnchor.constraint(equalTo: header.centerYAnchor),
        ])
        return header
    }

    private func buildProgressBar() -> NSView {
        let host = NSView()
        host.wantsLayer = true
        host.layer?.backgroundColor = ViewPalette.line.cgColor
        let first = NSView()
        first.wantsLayer = true
        first.layer?.backgroundColor = ViewPalette.onboardingAccent.cgColor
        progressSecondHalf.wantsLayer = true
        progressSecondHalf.layer?.backgroundColor = ViewPalette.line.cgColor
        [first, progressSecondHalf].forEach {
            $0.translatesAutoresizingMaskIntoConstraints = false
            host.addSubview($0)
        }
        NSLayoutConstraint.activate([
            first.leadingAnchor.constraint(equalTo: host.leadingAnchor), first.topAnchor.constraint(equalTo: host.topAnchor), first.bottomAnchor.constraint(equalTo: host.bottomAnchor),
            first.widthAnchor.constraint(equalTo: host.widthAnchor, multiplier: 0.5),
            progressSecondHalf.leadingAnchor.constraint(equalTo: first.trailingAnchor), progressSecondHalf.trailingAnchor.constraint(equalTo: host.trailingAnchor),
            progressSecondHalf.topAnchor.constraint(equalTo: host.topAnchor), progressSecondHalf.bottomAnchor.constraint(equalTo: host.bottomAnchor),
        ])
        return host
    }

    private func buildPracticePage() -> NSView {
        let content = NSStackView()
        content.orientation = .vertical
        content.alignment = .width
        content.spacing = 10

        content.addArrangedSubview(pageTitle("说一句，看看它如何输入", size: 28))
        content.addArrangedSubview(mutedWrappingLabel("gujiguji 会把你说的话输入到当前有光标的文本框。先完成权限与本地识别设置，再在下面试一次。", size: 14))
        content.addArrangedSubview(buildPermissionCard())
        content.addArrangedSubview(buildLocalModelCard())
        content.addArrangedSubview(buildModeCard())
        content.addArrangedSubview(buildPracticeCard())

        return onboardingScroll(content, horizontalInset: 30, verticalInset: 16)
    }

    private func buildPermissionCard() -> NSView {
        microphoneButton.target = self
        microphoneButton.action = #selector(requestPermission(_:))
        microphoneButton.tag = OnboardingPermissionKind.microphone.rawValue
        accessibilityButton.target = self
        accessibilityButton.action = #selector(requestPermission(_:))
        accessibilityButton.tag = OnboardingPermissionKind.accessibility.rawValue

        let microphone = permissionRow(
            title: "麦克风",
            detail: "用于录制当前一次语音输入",
            status: microphoneStatus,
            button: microphoneButton
        )
        let accessibility = permissionRow(
            title: "辅助功能",
            detail: "用于监听说话键并把文字写回当前应用",
            status: accessibilityStatus,
            button: accessibilityButton
        )
        return CardView(content: vStack([boldLabel("系统权限"), microphone, divider(), accessibility], spacing: 7), fill: ViewPalette.panel, border: ViewPalette.line)
    }

    private func permissionRow(title: String, detail: String, status: NSTextField, button: NSButton) -> NSView {
        status.font = .systemFont(ofSize: 11, weight: .semibold)
        return hStack([
            vStack([boldLabel(title), smallLabel(detail, color: ViewPalette.muted)], spacing: 2),
            flexibleSpace(), status, button,
        ], spacing: 10)
    }

    private func buildLocalModelCard() -> NSView {
        modelTitle.font = .systemFont(ofSize: 13, weight: .semibold)
        modelStatus.font = .systemFont(ofSize: 11)
        modelStatus.textColor = ViewPalette.muted
        modelProgress.minValue = 0
        modelProgress.maxValue = 1
        modelProgress.isIndeterminate = true
        modelProgress.style = .bar
        modelProgress.controlSize = .small
        modelProgress.isHidden = true
        installButton.target = self
        installButton.action = #selector(installLocalModel)
        cancelInstallButton.target = self
        cancelInstallButton.action = #selector(cancelLocalModelInstall)
        cancelInstallButton.isHidden = true
        let text = vStack([modelTitle, modelStatus, modelProgress], spacing: 4)
        return CardView(
            content: hStack([WaveLogoView(size: 32), text, flexibleSpace(), cancelInstallButton, installButton], spacing: 10),
            fill: ViewPalette.onboardingAccentSoft,
            border: ViewPalette.onboardingAccentBorder
        )
    }

    private func buildModeCard() -> NSView {
        holdRadio.target = self
        holdRadio.action = #selector(pttModeChanged(_:))
        holdRadio.tag = 0
        toggleRadio.target = self
        toggleRadio.action = #selector(pttModeChanged(_:))
        toggleRadio.tag = 1
        let hold = vStack([holdRadio, smallLabel("按住时聆听，松开后输入", color: ViewPalette.muted)], spacing: 2)
        let toggle = vStack([toggleRadio, smallLabel("无需一直按住按键", color: ViewPalette.muted)], spacing: 2)
        return CardView(content: hStack([
            vStack([boldLabel("选择说话方式"), smallLabel("设置后立即在下方试用", color: ViewPalette.muted)], spacing: 2),
            flexibleSpace(), hold, toggle,
        ], spacing: 20), fill: ViewPalette.onboardingPanel, border: ViewPalette.line)
    }

    private func buildPracticeCard() -> NSView {
        practiceText.delegate = self
        practiceText.font = .monospacedSystemFont(ofSize: 14, weight: .regular)
        practiceText.string = ""
        practiceStatus.font = .systemFont(ofSize: 12, weight: .semibold)
        practiceStatus.textColor = ViewPalette.text
        practiceStatus.identifier = NSUserInterfaceItemIdentifier("onboarding.practice.status")
        practiceDetail.font = .systemFont(ofSize: 11)
        practiceDetail.textColor = ViewPalette.muted

        let statusHeading = hStack([practiceStatusDot, practiceStatus], spacing: 9)
        let detailRow = hStack([spacer(width: 18), practiceDetail], spacing: 0)
        let barsRow = hStack([spacer(width: 18), practiceBars, flexibleSpace()], spacing: 0)
        let status = vStack([statusHeading, detailRow, barsRow], spacing: 3)
        status.setContentHuggingPriority(.defaultLow, for: .horizontal)
        status.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)

        let keyAndStatus = hStack([practiceKeycap, status], spacing: 14)
        let right = vStack([practiceNotepad, keyAndStatus], spacing: 8)
        right.setContentHuggingPriority(.defaultLow, for: .horizontal)
        right.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)

        let practice = hStack([practiceSteps, right], spacing: 24)
        practice.alignment = .top
        practice.identifier = NSUserInterfaceItemIdentifier("onboarding.practice.region")
        return practice
    }

    private func buildCompletionPage() -> NSView {
        completionSummary.font = .systemFont(ofSize: 13)
        completionSummary.textColor = ViewPalette.muted
        let title = hStack([
            checkBadge(),
            vStack([pageTitle("gujiguji 已准备好", size: 27), completionSummary], spacing: 5),
        ], spacing: 12)
        let steps = hStack([
            completionStep("1", "点击文本框"), arrowLabel(),
            completionStep("2", completionTalk), arrowLabel(),
            completionStep("3", completionSubmit),
        ], spacing: 8)
        let statusItem = CardView(content: hStack([
            vStack([
                boldLabel("关闭引导后，gujiguji 仍会在菜单栏运行", size: 16),
                mutedWrappingLabel("需要暂停、切换 Profile、修改设置或退出时，点击菜单栏里的 gujiguji 图标。"),
            ], spacing: 6),
            flexibleSpace(), StatusMenuMockView(),
        ], spacing: 18), fill: ViewPalette.onboardingPanel, border: ViewPalette.line, leadingAccent: ViewPalette.blue)
        let settingsButton = NSButton(title: "打开设置", target: self, action: #selector(openSettings))
        let optional = CardView(content: hStack([
            WaveLogoView(size: 38),
            vStack([
                boldLabel("可选的语音与输入设置", size: 15),
                mutedWrappingLabel("也可切换其他本地模型、云端引擎、语言、Profile 与说话键。"),
            ], spacing: 4), flexibleSpace(), settingsButton,
        ], spacing: 12), fill: .white, border: ViewPalette.line)
        let content = vStack([title, steps, statusItem, optional], spacing: 14)
        return onboardingScroll(content, horizontalInset: 34, verticalInset: 18)
    }

    private func buildPracticeFooter() {
        skipButton.target = self
        skipButton.action = #selector(skipPractice)
        skipButton.isBordered = false
        continueButton.target = self
        continueButton.action = #selector(continuePressed)
        continueButton.keyEquivalent = "\r"
        let stack = hStack([skipButton, flexibleSpace(), continueButton], spacing: 8)
        stack.translatesAutoresizingMaskIntoConstraints = false
        practiceFooter.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: practiceFooter.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: practiceFooter.trailingAnchor),
            stack.centerYAnchor.constraint(equalTo: practiceFooter.centerYAnchor),
            continueButton.widthAnchor.constraint(greaterThanOrEqualToConstant: 108),
        ])
    }

    private func buildCompletionFooter() {
        let again = NSButton(title: "再练一次", target: self, action: #selector(practiceAgain))
        again.isBordered = false
        let finish = NSButton(title: "关闭引导并开始使用", target: self, action: #selector(finish))
        finish.keyEquivalent = "\r"
        let stack = hStack([again, flexibleSpace(), finish], spacing: 8)
        stack.translatesAutoresizingMaskIntoConstraints = false
        completionFooter.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: completionFooter.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: completionFooter.trailingAnchor),
            stack.centerYAnchor.constraint(equalTo: completionFooter.centerYAnchor),
            finish.widthAnchor.constraint(greaterThanOrEqualToConstant: 206),
        ])
    }

    private func refreshOnboardingState() {
        updatePermission(state.microphone, label: microphoneStatus, button: microphoneButton)
        updatePermission(state.accessibility, label: accessibilityStatus, button: accessibilityButton)
        modelStatus.stringValue = state.installationStatus
        modelTitle.stringValue = state.recognitionReady ? "识别引擎已就绪" : state.installingLocalModel ? "正在准备本地识别" : "使用本地 Qwen3-ASR（推荐）"
        installButton.isHidden = state.recognitionReady || state.installingLocalModel || state.usesConfiguredRecognition
        cancelInstallButton.isHidden = !state.installingLocalModel
        modelProgress.isHidden = !state.installingLocalModel
        if state.installingLocalModel {
            if let progress = state.installationProgress {
                modelProgress.isIndeterminate = false
                modelProgress.doubleValue = progress
                modelProgress.stopAnimation(nil)
            } else {
                modelProgress.isIndeterminate = true
                modelProgress.startAnimation(nil)
            }
        } else {
            modelProgress.stopAnimation(nil)
        }
        skipButton.title = state.recognitionReady ? "跳过演练" : "改用 macOS Speech"
        refreshPracticeCompletion()
    }

    private func updatePermission(_ state: OnboardingPermissionState, label: NSTextField, button: NSButton) {
        switch state {
        case .unknown:
            label.stringValue = "未检查"
            label.textColor = ViewPalette.muted
            button.isHidden = false
        case .granted:
            label.stringValue = "已允许"
            label.textColor = ViewPalette.success
            button.isHidden = true
        case .denied:
            label.stringValue = "需要允许"
            label.textColor = ViewPalette.amber
            button.title = "打开系统设置"
            button.isHidden = false
        }
    }

    private func refreshPracticeCompletion() {
        let permissionsReady = state.microphone == .granted && state.accessibility == .granted
        let hasText = !practiceText.string.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        practiceNotepad.refreshPlaceholder()
        continueButton.isEnabled = permissionsReady && state.recognitionReady && hasText && !state.installingLocalModel
        skipButton.isEnabled = permissionsReady && !state.installingLocalModel
        if hasText {
            setPracticeStage(.complete, listening: false)
            practiceBars.setActive(false)
            setPracticeStatus(
                "演练完成，可以继续",
                detail: profile.pttMode == .toggle
                ? "日常使用也是这样：点击文本框，按一下开始，再按一下输入。"
                : "日常使用也是这样：点击文本框，按住说话，松开输入。",
                dotColor: ViewPalette.success
            )
        } else if practiceStage == .complete {
            let focused = window?.firstResponder === practiceText
            setPracticeStage(focused ? .readyToTalk : .awaitingFocus, listening: false)
            setPracticeStatus(
                focused ? "文本框已聚焦" : "先点击上面的文本框",
                detail: focused ? practiceStartInstruction() : "光标出现后，再使用说话键。",
                dotColor: focused ? ViewPalette.blue : ViewPalette.color(0x9DA8AD)
            )
        }
    }

    private func applyPTTModeText() {
        holdRadio.state = profile.pttMode == .hold ? .on : .off
        toggleRadio.state = profile.pttMode == .toggle ? .on : .off
        let key = profile.activationKey.displayName
        practiceKeycap.update(text: key)
        practiceSteps.updateCopy(key: key, mode: profile.pttMode)
        if profile.pttMode == .toggle {
            practiceNotepad.updatePlaceholder("点击这里，按一下 \(key) 开始聆听，再按一下结束并输入")
            completionTalk.stringValue = "按一下开始"
            completionSubmit.stringValue = "再按一下输入"
            completionSummary.stringValue = "在其他应用中点击文本框，按一下 \(key) 开始聆听，再按一下结束并输入。"
        } else {
            practiceNotepad.updatePlaceholder("点击这里，按住 \(key) 说话，松开后文字会输入到这里")
            completionTalk.stringValue = "按住说话"
            completionSubmit.stringValue = "松开输入"
            completionSummary.stringValue = "在其他应用中点击文本框，按住 \(key) 说话，松开后文字会输入到原来的光标位置。"
        }
    }

    private func setPracticeStage(_ stage: OnboardingPracticeStage, listening: Bool) {
        practiceStage = stage
        practiceSteps.update(stage: stage)
        if listening {
            practiceKeycap.setState(profile.pttMode == .toggle ? .latched : .pressed)
        } else {
            practiceKeycap.setState(.idle)
        }
    }

    private func setPracticeStatus(_ title: String, detail: String, dotColor: NSColor) {
        practiceStatus.stringValue = title
        practiceStatus.textColor = ViewPalette.text
        practiceDetail.stringValue = detail
        practiceStatusDot.setColor(dotColor)
        practiceStatus.toolTip = "\(title)。\(detail)"
    }

    private func practiceStartInstruction() -> String {
        let key = profile.activationKey.displayName
        return profile.pttMode == .toggle
            ? "按一下 \(key) 开始，说完后再按一下。"
            : "现在按住 \(key)，说一句话，然后松开。"
    }

    private func showPage(_ page: Int) {
        self.page = page
        practicePage.isHidden = page != 0
        practiceFooter.isHidden = page != 0
        completionPage.isHidden = page != 1
        completionFooter.isHidden = page != 1
        stepCount.stringValue = page == 0 ? "1 / 2" : "2 / 2"
        progressSecondHalf.layer?.backgroundColor = (page == 0 ? ViewPalette.line : ViewPalette.onboardingAccent).cgColor
        window?.title = page == 0 ? "gujiguji 快速设置" : "gujiguji 已准备好"
    }

    func textDidBeginEditing(_ notification: Notification) {
        guard practiceText.string.isEmpty else { return }
        setPracticeStage(.readyToTalk, listening: false)
        setPracticeStatus(
            "文本框已聚焦",
            detail: practiceStartInstruction(),
            dotColor: ViewPalette.blue
        )
    }

    func textDidChange(_ notification: Notification) { refreshPracticeCompletion() }

    @objc private func requestPermission(_ sender: NSButton) {
        guard let kind = OnboardingPermissionKind(rawValue: sender.tag) else { return }
        actions.requestPermission(kind)
    }
    @objc private func installLocalModel() { actions.installLocalModel() }
    @objc private func cancelLocalModelInstall() { actions.cancelLocalModelInstall() }
    @objc private func pttModeChanged(_ sender: NSButton) {
        profile.pttMode = sender.tag == 0 ? .hold : .toggle
        actions.setPTTMode(profile.pttMode)
        practiceText.string = ""
        practiceBars.setActive(false)
        applyPTTModeText()
        setPracticeStage(.awaitingFocus, listening: false)
        setPracticeStatus(
            "说话方式已设置",
            detail: state.recognitionReady ? "点击上面的文本框后即可按所选方式试用。" : "识别引擎就绪后即可按所选方式试用。",
            dotColor: ViewPalette.blue
        )
        refreshPracticeCompletion()
    }
    @objc private func continuePressed() { if continueButton.isEnabled { showPage(1) } }
    @objc private func practiceAgain() {
        practiceText.string = ""
        practiceBars.setActive(false)
        showPage(0)
        _ = window?.makeFirstResponder(practiceText)
        setPracticeStage(.readyToTalk, listening: false)
        setPracticeStatus("文本框已聚焦", detail: practiceStartInstruction(), dotColor: ViewPalette.blue)
        refreshPracticeCompletion()
    }
    @objc private func skipPractice() {
        let choice: OnboardingCompletionChoice = state.recognitionReady
            ? (state.usesConfiguredRecognition ? .configured : .defaultLocal)
            : .macOSFallback
        if actions.complete(choice, profile.pttMode) { window?.close() }
    }
    @objc private func finish() {
        let choice: OnboardingCompletionChoice = state.usesConfiguredRecognition ? .configured : .defaultLocal
        if actions.complete(choice, profile.pttMode) { window?.close() }
    }
    @objc private func openSettings() { actions.openSettings() }
}

// MARK: - Shared AppKit building blocks

private enum ViewPalette {
    static let accent = color(0x2F7658)
    static let background = color(0xFAFAFA)
    static let sidebar = color(0xF1F1F1)
    static let footer = color(0xF3F3F3)
    static let text = color(0x242424)
    static let muted = color(0x6B6B6B)
    static let error = color(0xB42318)
    static let line = color(0xD8D8D8)
    static let panel = NSColor.white
    static let selectedNav = color(0xE4EEE8)
    static let success = color(0x1E744C)
    static let successSoft = color(0xF4F8F5)
    static let successBorder = color(0xBFD2C7)
    static let amber = color(0xA65F1F)
    static let amberSoft = color(0xFFF3E6)
    static let amberBorder = color(0xE4D4B7)
    static let blue = color(0x2868AD)
    static let blueSoft = color(0xE8F0FA)
    static let onboardingAccent = color(0x146B5D)
    static let onboardingAccentSoft = color(0xE4F2EE)
    static let onboardingAccentBorder = color(0xACCCC3)
    static let onboardingPanel = color(0xF8FAFB)
    static let onboardingFooter = color(0xFAFBFB)

    static func color(_ value: Int, alpha: CGFloat = 1) -> NSColor {
        NSColor(
            calibratedRed: CGFloat((value >> 16) & 0xFF) / 255,
            green: CGFloat((value >> 8) & 0xFF) / 255,
            blue: CGFloat(value & 0xFF) / 255,
            alpha: alpha
        )
    }
}

private final class CardView: NSView {
    private var bodyView: NSView?
    private let accent = NSView()

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        translatesAutoresizingMaskIntoConstraints = false
        wantsLayer = true
        layer?.cornerRadius = 6
        layer?.borderWidth = 1
        layer?.backgroundColor = ViewPalette.panel.cgColor
        layer?.borderColor = ViewPalette.line.cgColor
        accent.translatesAutoresizingMaskIntoConstraints = false
        accent.wantsLayer = true
        accent.isHidden = true
        addSubview(accent)
        NSLayoutConstraint.activate([
            accent.leadingAnchor.constraint(equalTo: leadingAnchor),
            accent.topAnchor.constraint(equalTo: topAnchor),
            accent.bottomAnchor.constraint(equalTo: bottomAnchor),
            accent.widthAnchor.constraint(equalToConstant: 4),
        ])
    }

    convenience init(
        content: NSView,
        fill: NSColor = ViewPalette.panel,
        border: NSColor = ViewPalette.line,
        leadingAccent: NSColor? = nil
    ) {
        self.init(frame: .zero)
        setContent(content, fill: fill, border: border, leadingAccent: leadingAccent)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func setContent(
        _ content: NSView,
        fill: NSColor = ViewPalette.panel,
        border: NSColor = ViewPalette.line,
        leadingAccent: NSColor? = nil
    ) {
        bodyView?.removeFromSuperview()
        bodyView = content
        content.translatesAutoresizingMaskIntoConstraints = false
        addSubview(content)
        accent.isHidden = leadingAccent == nil
        accent.layer?.backgroundColor = leadingAccent?.cgColor
        setColors(fill: fill, border: border, leadingAccent: leadingAccent)
        NSLayoutConstraint.activate([
            content.leadingAnchor.constraint(equalTo: leadingAnchor, constant: leadingAccent == nil ? 14 : 18),
            content.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -14),
            content.topAnchor.constraint(equalTo: topAnchor, constant: 11),
            content.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -11),
        ])
    }

    func setColors(fill: NSColor, border: NSColor, leadingAccent: NSColor?) {
        layer?.backgroundColor = fill.cgColor
        layer?.borderColor = border.cgColor
        accent.isHidden = leadingAccent == nil
        accent.layer?.backgroundColor = leadingAccent?.cgColor
    }
}

private final class VerticalFillStackView: NSStackView {
    private var fillConstraints: [ObjectIdentifier: NSLayoutConstraint] = [:]

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        orientation = .vertical
        alignment = .leading
        distribution = .fill
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func addArrangedSubview(_ view: NSView) {
        super.addArrangedSubview(view)
        view.translatesAutoresizingMaskIntoConstraints = false
        let constraint = view.widthAnchor.constraint(equalTo: widthAnchor)
        constraint.isActive = true
        fillConstraints[ObjectIdentifier(view)] = constraint
    }

    override func removeArrangedSubview(_ view: NSView) {
        fillConstraints.removeValue(forKey: ObjectIdentifier(view))?.isActive = false
        super.removeArrangedSubview(view)
    }
}

private final class FlippedDocumentView: NSView {
    override var isFlipped: Bool { true }
}

private func makePage(title: String, subtitle: String, views: [NSView]) -> NSView {
    let stack = vStack([], spacing: 10)
    stack.addArrangedSubview(pageTitle(title))
    let sub = smallLabel(subtitle, color: ViewPalette.muted, size: 12)
    stack.addArrangedSubview(sub)
    stack.setCustomSpacing(20, after: sub)
    for view in views { stack.addArrangedSubview(view) }
    return scrolling(stack, insets: NSEdgeInsets(top: 0, left: 0, bottom: 8, right: 0))
}

private func onboardingScroll(_ content: NSView, horizontalInset: CGFloat, verticalInset: CGFloat) -> NSView {
    scrolling(content, insets: NSEdgeInsets(top: verticalInset, left: horizontalInset, bottom: verticalInset, right: horizontalInset))
}

private func scrolling(_ content: NSView, insets: NSEdgeInsets) -> NSScrollView {
    let scroll = NSScrollView()
    scroll.drawsBackground = false
    scroll.hasVerticalScroller = true
    scroll.hasHorizontalScroller = false
    scroll.autohidesScrollers = true

    let document = FlippedDocumentView()
    document.translatesAutoresizingMaskIntoConstraints = false
    content.translatesAutoresizingMaskIntoConstraints = false
    document.addSubview(content)
    scroll.documentView = document
    let clip = scroll.contentView
    NSLayoutConstraint.activate([
        document.leadingAnchor.constraint(equalTo: clip.leadingAnchor),
        document.topAnchor.constraint(equalTo: clip.topAnchor),
        document.widthAnchor.constraint(equalTo: clip.widthAnchor),
        document.heightAnchor.constraint(greaterThanOrEqualTo: clip.heightAnchor),
        content.leadingAnchor.constraint(equalTo: document.leadingAnchor, constant: insets.left),
        content.trailingAnchor.constraint(equalTo: document.trailingAnchor, constant: -insets.right),
        content.topAnchor.constraint(equalTo: document.topAnchor, constant: insets.top),
        content.bottomAnchor.constraint(lessThanOrEqualTo: document.bottomAnchor, constant: -insets.bottom),
    ])
    let viewportHeight = document.heightAnchor.constraint(equalTo: clip.heightAnchor)
    viewportHeight.priority = .defaultLow
    viewportHeight.isActive = true
    let contentHeight = document.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: insets.bottom)
    contentHeight.priority = NSLayoutConstraint.Priority(249)
    contentHeight.isActive = true
    return scroll
}

private func installContent(_ content: NSView, in host: NSView) {
    content.translatesAutoresizingMaskIntoConstraints = false
    host.addSubview(content)
    NSLayoutConstraint.activate([
        content.leadingAnchor.constraint(equalTo: host.leadingAnchor),
        content.trailingAnchor.constraint(equalTo: host.trailingAnchor),
        content.topAnchor.constraint(equalTo: host.topAnchor),
        content.bottomAnchor.constraint(equalTo: host.bottomAnchor),
    ])
}

private func vStack(_ views: [NSView], spacing: CGFloat) -> NSStackView {
    let stack = VerticalFillStackView()
    stack.spacing = spacing
    for view in views { stack.addArrangedSubview(view) }
    return stack
}

private func hStack(_ views: [NSView], spacing: CGFloat) -> NSStackView {
    let stack = NSStackView(views: views)
    stack.orientation = .horizontal
    stack.alignment = .centerY
    stack.distribution = .fill
    stack.spacing = spacing
    return stack
}

private func equalHStack(_ views: [NSView], spacing: CGFloat) -> NSStackView {
    let stack = hStack(views, spacing: spacing)
    stack.distribution = .fillEqually
    return stack
}

private func navigationSymbolImage(_ name: String, description: String) -> NSImage? {
    guard let symbol = NSImage(systemSymbolName: name, accessibilityDescription: description) else { return nil }
    let canvas = NSImage(size: NSSize(width: 18, height: 18), flipped: false) { rect in
        symbol.draw(in: rect.insetBy(dx: 1, dy: 1), from: .zero, operation: .sourceOver, fraction: 1)
        return true
    }
    canvas.isTemplate = true
    return canvas
}

private func flexibleSpace() -> NSView {
    let view = NSView()
    view.setContentHuggingPriority(.defaultLow, for: .horizontal)
    view.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
    view.heightAnchor.constraint(equalToConstant: 0).isActive = true
    return view
}

private func spacer(width: CGFloat) -> NSView {
    let view = NSView()
    view.widthAnchor.constraint(equalToConstant: width).isActive = true
    return view
}

private func smallLabel(
    _ text: String,
    color: NSColor = ViewPalette.text,
    size: CGFloat = 11,
    weight: NSFont.Weight = .regular
) -> NSTextField {
    let label = NSTextField(labelWithString: text)
    label.font = .systemFont(ofSize: size, weight: weight)
    label.textColor = color
    return label
}

private func boldLabel(_ text: String, size: CGFloat = 13) -> NSTextField {
    smallLabel(text, color: ViewPalette.text, size: size, weight: .semibold)
}

private func accentLabel(_ text: String) -> NSTextField {
    smallLabel(text, color: ViewPalette.accent, size: 12, weight: .semibold)
}

private func mutedWrappingLabel(_ text: String, size: CGFloat = 12) -> NSTextField {
    let label = NSTextField(wrappingLabelWithString: text)
    label.font = .systemFont(ofSize: size)
    label.textColor = ViewPalette.muted
    label.setContentHuggingPriority(.defaultLow, for: .horizontal)
    label.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
    return label
}

private func pageTitle(_ text: String, size: CGFloat = 22) -> NSTextField {
    let label = NSTextField(labelWithString: text)
    label.font = .systemFont(ofSize: size, weight: .semibold)
    label.textColor = ViewPalette.text
    return label
}

private func sectionTitle(_ text: String) -> NSTextField {
    let label = boldLabel(text, size: 15)
    return label
}

private func fieldRow(
    _ title: String,
    _ control: NSView,
    labelWidth: CGFloat = 150,
    alignment: NSLayoutConstraint.Attribute = .centerY
) -> NSView {
    let label = smallLabel(title, color: ViewPalette.text, size: 13)
    label.alignment = .left
    label.widthAnchor.constraint(equalToConstant: labelWidth).isActive = true
    control.setContentHuggingPriority(.defaultLow, for: .horizontal)
    control.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
    let row = hStack([label, control], spacing: 0)
    row.alignment = alignment
    return row
}

private func labeledControl(_ title: String, _ control: NSView) -> NSView {
    let label = smallLabel(title, color: ViewPalette.muted, size: 11)
    control.setContentHuggingPriority(.defaultLow, for: .horizontal)
    return vStack([label, control], spacing: 2)
}

private func textEditor(_ textView: NSTextView, height: CGFloat) -> NSScrollView {
    let scroll = NSScrollView()
    scroll.borderType = .bezelBorder
    scroll.hasVerticalScroller = true
    scroll.autohidesScrollers = true
    scroll.drawsBackground = true
    scroll.backgroundColor = .textBackgroundColor
    textView.isRichText = false
    textView.isAutomaticQuoteSubstitutionEnabled = false
    textView.isAutomaticDashSubstitutionEnabled = false
    textView.textContainerInset = NSSize(width: 7, height: 6)
    scroll.documentView = textView
    scroll.heightAnchor.constraint(equalToConstant: height).isActive = true
    return scroll
}

private func metric(_ title: String, _ value: NSTextField) -> NSView {
    value.font = .systemFont(ofSize: 13, weight: .semibold)
    value.lineBreakMode = .byTruncatingTail
    value.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
    return vStack([smallLabel(title, color: ViewPalette.muted), value], spacing: 3)
}

private func valueCard(title: String, subtitle: String, value: NSTextField) -> NSView {
    value.font = .systemFont(ofSize: 13, weight: .semibold)
    value.textColor = ViewPalette.accent
    return CardView(content: hStack([
        vStack([boldLabel(title), smallLabel(subtitle, color: ViewPalette.muted)], spacing: 2),
        flexibleSpace(), value,
    ], spacing: 12))
}

private func divider() -> NSView {
    let view = NSBox()
    view.boxType = .separator
    return view
}

private func statusDot() -> NSView {
    let dot = NSView()
    dot.wantsLayer = true
    dot.layer?.backgroundColor = ViewPalette.blue.cgColor
    dot.layer?.cornerRadius = 4.5
    dot.widthAnchor.constraint(equalToConstant: 9).isActive = true
    dot.heightAnchor.constraint(equalToConstant: 9).isActive = true
    return dot
}

private func checkBadge() -> NSView {
    let circle = NSView()
    circle.wantsLayer = true
    circle.layer?.backgroundColor = ViewPalette.successSoft.cgColor
    circle.layer?.cornerRadius = 21
    circle.widthAnchor.constraint(equalToConstant: 42).isActive = true
    circle.heightAnchor.constraint(equalToConstant: 42).isActive = true
    let check = smallLabel("✓", color: ViewPalette.success, size: 20, weight: .bold)
    check.translatesAutoresizingMaskIntoConstraints = false
    circle.addSubview(check)
    NSLayoutConstraint.activate([check.centerXAnchor.constraint(equalTo: circle.centerXAnchor), check.centerYAnchor.constraint(equalTo: circle.centerYAnchor)])
    return circle
}

private func completionStep(_ number: String, _ text: String) -> NSView {
    completionStep(number, smallLabel(text, color: ViewPalette.text, size: 11))
}

private func completionStep(_ number: String, _ text: NSView) -> NSView {
    let badge = NSView()
    badge.wantsLayer = true
    badge.layer?.backgroundColor = ViewPalette.blueSoft.cgColor
    badge.layer?.cornerRadius = 11
    badge.widthAnchor.constraint(equalToConstant: 22).isActive = true
    badge.heightAnchor.constraint(equalToConstant: 22).isActive = true
    let numberLabel = smallLabel(number, color: ViewPalette.blue, size: 10, weight: .bold)
    numberLabel.translatesAutoresizingMaskIntoConstraints = false
    badge.addSubview(numberLabel)
    NSLayoutConstraint.activate([numberLabel.centerXAnchor.constraint(equalTo: badge.centerXAnchor), numberLabel.centerYAnchor.constraint(equalTo: badge.centerYAnchor)])
    return CardView(content: hStack([badge, text], spacing: 7), fill: ViewPalette.onboardingPanel, border: ViewPalette.line)
}

private func arrowLabel() -> NSTextField {
    smallLabel("→", color: ViewPalette.muted, size: 14)
}

private final class WaveLogoView: NSView {
    init(size: CGFloat? = nil) {
        super.init(frame: .zero)
        if let size {
            widthAnchor.constraint(equalToConstant: size).isActive = true
            heightAnchor.constraint(equalToConstant: size).isActive = true
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        let square = NSBezierPath(roundedRect: bounds, xRadius: max(4, bounds.width * 0.2), yRadius: max(4, bounds.height * 0.2))
        ViewPalette.color(0xDFFF52).setFill()
        square.fill()
        ViewPalette.color(0x171A1D).setFill()
        let heights: [CGFloat] = [0.16, 0.48, 0.82, 0.50, 0.18]
        for (index, factor) in heights.enumerated() {
            let width = max(2, bounds.width * 0.075)
            let height = max(width, bounds.height * factor)
            let x = bounds.width * (0.20 + CGFloat(index) * 0.15)
            let rect = NSRect(x: x, y: (bounds.height - height) / 2, width: width, height: height)
            NSBezierPath(roundedRect: rect, xRadius: width / 2, yRadius: width / 2).fill()
        }
    }
}

private enum PracticeKeycapState: Equatable {
    case idle
    case pressed
    case latched
}

private final class KeycapView: NSView {
    private let label = NSTextField(wrappingLabelWithString: "")
    private var keyText = ""
    private var state = PracticeKeycapState.idle

    init(text: String) {
        super.init(frame: .zero)
        identifier = NSUserInterfaceItemIdentifier("onboarding.practice.keycap")
        keyText = text
        label.stringValue = text
        label.alignment = .center
        label.font = .systemFont(ofSize: 11, weight: .semibold)
        label.textColor = ViewPalette.color(0x26343C)
        label.translatesAutoresizingMaskIntoConstraints = false
        addSubview(label)
        NSLayoutConstraint.activate([
            widthAnchor.constraint(equalToConstant: 88), heightAnchor.constraint(equalToConstant: 58),
            label.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 6),
            label.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -6),
            label.centerYAnchor.constraint(equalTo: centerYAnchor),
        ])
        updateToolTip()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func update(text: String) {
        keyText = text
        label.stringValue = text
        updateToolTip()
    }

    func setState(_ state: PracticeKeycapState) {
        self.state = state
        label.textColor = state == .idle ? ViewPalette.color(0x26343C) : ViewPalette.onboardingAccent
        updateToolTip()
        needsDisplay = true
    }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        let border = state == .idle ? ViewPalette.color(0x87959C) : ViewPalette.onboardingAccent
        let fill = state == .idle ? ViewPalette.onboardingPanel : ViewPalette.onboardingAccentSoft
        let depth: CGFloat = state == .pressed ? 1 : 4
        let outerRect = bounds.insetBy(dx: 0.5, dy: 0.5)
        border.setFill()
        NSBezierPath(roundedRect: outerRect, xRadius: 7, yRadius: 7).fill()
        let innerRect = NSRect(
            x: outerRect.minX + 1,
            y: outerRect.minY + depth,
            width: outerRect.width - 2,
            height: outerRect.height - depth - 1
        )
        fill.setFill()
        NSBezierPath(roundedRect: innerRect, xRadius: 6, yRadius: 6).fill()
    }

    private func updateToolTip() {
        let stateText = switch state {
        case .idle: "未按下"
        case .pressed: "正在按住"
        case .latched: "聆听已开启"
        }
        toolTip = "当前说话键：\(keyText)，\(stateText)"
    }
}

private final class PracticeStepsView: NSView {
    private let cards = [
        PracticeStepCardView(index: 1, title: "点击文本框", detail: "先把光标放到需要输入的位置"),
        PracticeStepCardView(index: 2, title: "按住说话键说话", detail: "按住时 gujiguji 才会聆听"),
        PracticeStepCardView(index: 3, title: "松开即可输入", detail: "文字会回到刚才的光标位置"),
    ]

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        identifier = NSUserInterfaceItemIdentifier("onboarding.practice.steps")
        let stack = vStack(cards, spacing: 8)
        stack.translatesAutoresizingMaskIntoConstraints = false
        addSubview(stack)
        NSLayoutConstraint.activate([
            widthAnchor.constraint(equalToConstant: 210),
            stack.leadingAnchor.constraint(equalTo: leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: trailingAnchor),
            stack.topAnchor.constraint(equalTo: topAnchor),
            stack.bottomAnchor.constraint(equalTo: bottomAnchor),
        ])
        update(stage: .awaitingFocus)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func updateCopy(key: String, mode: PttMode) {
        if mode == .toggle {
            cards[1].updateCopy(
                title: "按一下 \(key) 开始",
                detail: "松开按键后 gujiguji 开始聆听"
            )
            cards[2].updateCopy(
                title: "再按一下 \(key) 结束",
                detail: "识别完成后文字会回到当前光标位置"
            )
        } else {
            cards[1].updateCopy(
                title: "按住 \(key) 说话",
                detail: "按住时 gujiguji 才会聆听"
            )
            cards[2].updateCopy(
                title: "松开即可输入",
                detail: "文字会回到刚才的光标位置"
            )
        }
    }

    func update(stage: OnboardingPracticeStage) {
        let completedThrough: Int
        let activeStep: Int
        switch stage {
        case .awaitingFocus:
            completedThrough = 0
            activeStep = 1
        case .readyToTalk, .listening:
            completedThrough = 1
            activeStep = 2
        case .processing:
            completedThrough = 2
            activeStep = 3
        case .complete:
            completedThrough = 3
            activeStep = 0
        }
        for (offset, card) in cards.enumerated() {
            let index = offset + 1
            card.update(active: index == activeStep, complete: index <= completedThrough)
        }
    }
}

private final class PracticeStepCardView: NSView {
    private let index: Int
    private let leadingBar = NSView()
    private let badge = NSView()
    private let badgeText = NSTextField(labelWithString: "")
    private let titleLabel = NSTextField(wrappingLabelWithString: "")
    private let detailLabel = NSTextField(wrappingLabelWithString: "")

    init(index: Int, title: String, detail: String) {
        self.index = index
        super.init(frame: .zero)
        identifier = NSUserInterfaceItemIdentifier("onboarding.practice.step.\(index)")
        wantsLayer = true
        layer?.cornerRadius = 4

        leadingBar.translatesAutoresizingMaskIntoConstraints = false
        leadingBar.wantsLayer = true
        badge.translatesAutoresizingMaskIntoConstraints = false
        badge.wantsLayer = true
        badge.layer?.cornerRadius = 13.5
        badgeText.stringValue = String(index)
        badgeText.alignment = .center
        badgeText.font = .systemFont(ofSize: 12, weight: .bold)
        badgeText.translatesAutoresizingMaskIntoConstraints = false
        badge.addSubview(badgeText)

        titleLabel.font = .systemFont(ofSize: 13, weight: .semibold)
        titleLabel.textColor = ViewPalette.text
        titleLabel.identifier = NSUserInterfaceItemIdentifier("onboarding.practice.step.\(index).title")
        detailLabel.font = .systemFont(ofSize: 11)
        detailLabel.textColor = ViewPalette.muted
        detailLabel.identifier = NSUserInterfaceItemIdentifier("onboarding.practice.step.\(index).detail")
        updateCopy(title: title, detail: detail)

        let labels = vStack([titleLabel, detailLabel], spacing: 3)
        labels.translatesAutoresizingMaskIntoConstraints = false
        [leadingBar, badge, labels].forEach(addSubview)
        NSLayoutConstraint.activate([
            heightAnchor.constraint(greaterThanOrEqualToConstant: 58),
            leadingBar.leadingAnchor.constraint(equalTo: leadingAnchor),
            leadingBar.topAnchor.constraint(equalTo: topAnchor),
            leadingBar.bottomAnchor.constraint(equalTo: bottomAnchor),
            leadingBar.widthAnchor.constraint(equalToConstant: 3),
            badge.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 10),
            badge.centerYAnchor.constraint(equalTo: centerYAnchor),
            badge.widthAnchor.constraint(equalToConstant: 27),
            badge.heightAnchor.constraint(equalToConstant: 27),
            badgeText.centerXAnchor.constraint(equalTo: badge.centerXAnchor),
            badgeText.centerYAnchor.constraint(equalTo: badge.centerYAnchor),
            labels.leadingAnchor.constraint(equalTo: badge.trailingAnchor, constant: 9),
            labels.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -8),
            labels.centerYAnchor.constraint(equalTo: centerYAnchor),
            labels.topAnchor.constraint(greaterThanOrEqualTo: topAnchor, constant: 8),
            labels.bottomAnchor.constraint(lessThanOrEqualTo: bottomAnchor, constant: -8),
        ])
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func updateCopy(title: String, detail: String) {
        titleLabel.stringValue = title
        detailLabel.stringValue = detail
    }

    func update(active: Bool, complete: Bool) {
        layer?.backgroundColor = (active ? ViewPalette.blueSoft : NSColor.clear).cgColor
        leadingBar.layer?.backgroundColor = (
            active ? ViewPalette.blue : complete ? ViewPalette.success : NSColor.clear
        ).cgColor
        badge.layer?.backgroundColor = (
            complete ? ViewPalette.success : active ? ViewPalette.blue : NSColor.clear
        ).cgColor
        badge.layer?.borderColor = ViewPalette.color(0xAEBAC0).cgColor
        badge.layer?.borderWidth = active || complete ? 0 : 1
        badgeText.stringValue = complete ? "✓" : String(index)
        badgeText.textColor = active || complete ? .white : ViewPalette.muted
        toolTip = complete
            ? "步骤 \(index)，已完成"
            : active ? "步骤 \(index)，当前步骤" : "步骤 \(index)，未开始"
    }
}

private final class PracticeStatusDotView: NSView {
    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        identifier = NSUserInterfaceItemIdentifier("onboarding.practice.status-dot")
        wantsLayer = true
        layer?.cornerRadius = 4.5
        layer?.backgroundColor = ViewPalette.color(0x9DA8AD).cgColor
        NSLayoutConstraint.activate([
            widthAnchor.constraint(equalToConstant: 9),
            heightAnchor.constraint(equalToConstant: 9),
        ])
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func setColor(_ color: NSColor) { layer?.backgroundColor = color.cgColor }
}

private final class PassthroughLabel: NSTextField {
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
}

private final class PracticeNotepadView: NSView {
    private let textView: NSTextView
    private let placeholder = PassthroughLabel(wrappingLabelWithString: "")

    init(textView: NSTextView) {
        self.textView = textView
        super.init(frame: .zero)
        identifier = NSUserInterfaceItemIdentifier("onboarding.practice.notepad")
        wantsLayer = true
        layer?.backgroundColor = NSColor.white.cgColor
        layer?.borderColor = ViewPalette.color(0xAEB9BE).cgColor
        layer?.borderWidth = 1
        layer?.cornerRadius = 6
        layer?.masksToBounds = true

        let titleBar = NSView()
        titleBar.wantsLayer = true
        titleBar.layer?.backgroundColor = ViewPalette.color(0xF3F5F6).cgColor
        let icon = NSView()
        icon.wantsLayer = true
        icon.layer?.backgroundColor = ViewPalette.color(0xD9E9F8).cgColor
        icon.translatesAutoresizingMaskIntoConstraints = false
        let iconText = smallLabel("N", color: ViewPalette.blue, size: 10, weight: .bold)
        iconText.translatesAutoresizingMaskIntoConstraints = false
        icon.addSubview(iconText)
        let title = smallLabel("无标题 - 记事本", color: ViewPalette.color(0x3F4B52), size: 11)
        let controls = smallLabel("—   □   ×", color: ViewPalette.color(0x5D6870), size: 11)
        let titleContent = hStack([icon, title, flexibleSpace(), controls], spacing: 7)
        titleContent.translatesAutoresizingMaskIntoConstraints = false
        titleBar.addSubview(titleContent)
        NSLayoutConstraint.activate([
            icon.widthAnchor.constraint(equalToConstant: 18),
            icon.heightAnchor.constraint(equalToConstant: 18),
            iconText.centerXAnchor.constraint(equalTo: icon.centerXAnchor),
            iconText.centerYAnchor.constraint(equalTo: icon.centerYAnchor),
            titleContent.leadingAnchor.constraint(equalTo: titleBar.leadingAnchor, constant: 9),
            titleContent.trailingAnchor.constraint(equalTo: titleBar.trailingAnchor, constant: -9),
            titleContent.centerYAnchor.constraint(equalTo: titleBar.centerYAnchor),
            titleBar.heightAnchor.constraint(equalToConstant: 30),
        ])

        let menuBar = NSView()
        menuBar.wantsLayer = true
        menuBar.layer?.backgroundColor = NSColor.white.cgColor
        let menu = smallLabel("文件    编辑    查看", color: ViewPalette.color(0x3F4B52), size: 10)
        menu.translatesAutoresizingMaskIntoConstraints = false
        menuBar.addSubview(menu)
        NSLayoutConstraint.activate([
            menu.leadingAnchor.constraint(equalTo: menuBar.leadingAnchor, constant: 10),
            menu.centerYAnchor.constraint(equalTo: menuBar.centerYAnchor),
            menuBar.heightAnchor.constraint(equalToConstant: 24),
        ])

        let editorHost = NSView()
        let scroll = NSScrollView()
        scroll.identifier = NSUserInterfaceItemIdentifier("onboarding.practice.notepad.editor-scroll")
        scroll.borderType = .noBorder
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.drawsBackground = true
        scroll.backgroundColor = .white
        scroll.translatesAutoresizingMaskIntoConstraints = false

        textView.identifier = NSUserInterfaceItemIdentifier("onboarding.practice.notepad.editor")
        textView.isRichText = false
        textView.isAutomaticQuoteSubstitutionEnabled = false
        textView.isAutomaticDashSubstitutionEnabled = false
        textView.isHorizontallyResizable = false
        textView.isVerticallyResizable = true
        textView.autoresizingMask = [.width]
        textView.textContainer?.widthTracksTextView = true
        textView.textContainerInset = NSSize(width: 12, height: 9)
        textView.backgroundColor = .white
        textView.textColor = ViewPalette.color(0x20272C)
        textView.frame = NSRect(x: 0, y: 0, width: 320, height: 80)
        scroll.documentView = textView

        placeholder.font = .monospacedSystemFont(ofSize: 13, weight: .regular)
        placeholder.textColor = ViewPalette.color(0x8A969C)
        placeholder.identifier = NSUserInterfaceItemIdentifier("onboarding.practice.notepad.placeholder")
        placeholder.translatesAutoresizingMaskIntoConstraints = false
        editorHost.addSubview(scroll)
        editorHost.addSubview(placeholder)
        NSLayoutConstraint.activate([
            scroll.leadingAnchor.constraint(equalTo: editorHost.leadingAnchor),
            scroll.trailingAnchor.constraint(equalTo: editorHost.trailingAnchor),
            scroll.topAnchor.constraint(equalTo: editorHost.topAnchor),
            scroll.bottomAnchor.constraint(equalTo: editorHost.bottomAnchor),
            placeholder.leadingAnchor.constraint(equalTo: editorHost.leadingAnchor, constant: 13),
            placeholder.trailingAnchor.constraint(lessThanOrEqualTo: editorHost.trailingAnchor, constant: -12),
            placeholder.topAnchor.constraint(equalTo: editorHost.topAnchor, constant: 10),
        ])

        let statusBar = NSView()
        statusBar.wantsLayer = true
        statusBar.layer?.backgroundColor = ViewPalette.color(0xF7F8F8).cgColor
        let position = smallLabel("第 1 行，第 1 列    100%    UTF-8", color: ViewPalette.color(0x748087), size: 9)
        position.translatesAutoresizingMaskIntoConstraints = false
        statusBar.addSubview(position)
        NSLayoutConstraint.activate([
            position.trailingAnchor.constraint(equalTo: statusBar.trailingAnchor, constant: -9),
            position.centerYAnchor.constraint(equalTo: statusBar.centerYAnchor),
            statusBar.heightAnchor.constraint(equalToConstant: 22),
        ])

        let content = vStack([titleBar, menuBar, editorHost, statusBar], spacing: 0)
        content.translatesAutoresizingMaskIntoConstraints = false
        addSubview(content)
        NSLayoutConstraint.activate([
            heightAnchor.constraint(equalToConstant: 158),
            content.leadingAnchor.constraint(equalTo: leadingAnchor),
            content.trailingAnchor.constraint(equalTo: trailingAnchor),
            content.topAnchor.constraint(equalTo: topAnchor),
            content.bottomAnchor.constraint(equalTo: bottomAnchor),
        ])
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func updatePlaceholder(_ text: String) {
        placeholder.stringValue = text
        refreshPlaceholder()
    }

    func refreshPlaceholder() { placeholder.isHidden = !textView.string.isEmpty }
}

private final class PracticeBarsView: NSView {
    private var timer: Timer?
    private var phase = 0.0

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        identifier = NSUserInterfaceItemIdentifier("onboarding.practice.bars")
        widthAnchor.constraint(equalToConstant: 56).isActive = true
        heightAnchor.constraint(equalToConstant: 20).isActive = true
        isHidden = true
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func setActive(_ active: Bool) {
        isHidden = !active
        if active, timer == nil {
            let timer = Timer(timeInterval: 1 / 24, repeats: true) { [weak self] _ in
                self?.phase += 0.22
                self?.needsDisplay = true
            }
            RunLoop.main.add(timer, forMode: .common)
            self.timer = timer
        } else if !active {
            timer?.invalidate()
            timer = nil
            phase = 0
            needsDisplay = true
        }
    }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        ViewPalette.onboardingAccent.setFill()
        for index in 0..<5 {
            let height = timer == nil ? 4 : 5 + CGFloat((sin(phase + Double(index) * 0.9) + 1) * 6)
            let rect = NSRect(x: CGFloat(index) * 11 + 2, y: (bounds.height - height) / 2,
                              width: 5, height: height)
            NSBezierPath(roundedRect: rect, xRadius: 2.5, yRadius: 2.5).fill()
        }
    }
}

private final class StatusMenuMockView: NSView {
    init() {
        super.init(frame: .zero)
        widthAnchor.constraint(equalToConstant: 230).isActive = true
        heightAnchor.constraint(equalToConstant: 104).isActive = true
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        ViewPalette.color(0x202326).setFill()
        NSBezierPath(rect: NSRect(x: 0, y: 0, width: bounds.width, height: 34)).fill()
        ViewPalette.color(0xDFFF52).setFill()
        NSBezierPath(roundedRect: NSRect(x: bounds.width - 48, y: 8, width: 18, height: 18), xRadius: 4, yRadius: 4).fill()
        NSColor.white.setFill()
        NSBezierPath(roundedRect: NSRect(x: 30, y: 25, width: 150, height: 78), xRadius: 4, yRadius: 4).fill()
        let items = ["gujiguji", "暂停聆听", "设置…", "退出"]
        for (index, item) in items.enumerated() {
            let attributes: [NSAttributedString.Key: Any] = [
                .font: NSFont.systemFont(ofSize: 10, weight: index == 0 ? .semibold : .regular),
                .foregroundColor: index == 2 ? ViewPalette.blue : ViewPalette.text,
            ]
            (item as NSString).draw(at: NSPoint(x: 40, y: 81 - CGFloat(index) * 17), withAttributes: attributes)
        }
    }
}

private extension Array {
    subscript(safe index: Int) -> Element? {
        indices.contains(index) ? self[index] : nil
    }
}
