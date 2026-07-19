import AppKit
import QuartzCore

/// Every visual state exposed by the WPF overlay, expressed without coupling the UI to AppModel.
enum OverlayPresentation: Equatable {
    case starting
    case listening(String = "Listening…")
    case partial(String)
    case transcribing
    case refining
    case profile(name: String, activation: String)
    case failure(String)
}

/// Fixed-size, non-activating overlay host. The capsule grows inside the host so neither focus nor
/// the active application's window geometry is disturbed while partial results arrive.
@MainActor
final class OverlayWindowController: NSWindowController {
    private enum Metrics {
        static let host = NSSize(width: 820, height: 150)
        static let capsuleHeight: CGFloat = 64
        static let minimumWidth: CGFloat = 220
        static let maximumWidth: CGFloat = 726
        static let maximumTextWidth: CGFloat = 580
        static let textChrome: CGFloat = 146 // 64 wave + 16 padding + 54 live + 12 padding
        static let edgeMargin: CGFloat = 64
    }

    private let capsule = OverlayCapsuleView()
    private var position: OverlayPosition = .bottom
    private var anchorScreen: NSScreen?
    private var autoHideTimer: Timer?
    private var presentationGeneration = 0

    /// Optional pull source used at display refresh cadence. `setLevel(_:)` remains available for
    /// push-based audio pipelines.
    var levelSource: (() -> Double)? {
        didSet { capsule.waveform.levelSource = levelSource }
    }

    init() {
        let panel = PassiveOverlayPanel(
            contentRect: NSRect(origin: .zero, size: Metrics.host),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: true
        )
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.hidesOnDeactivate = false
        panel.ignoresMouseEvents = true
        panel.isFloatingPanel = true
        panel.becomesKeyOnlyIfNeeded = true
        panel.level = .statusBar
        panel.collectionBehavior = [
            .canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle,
        ]

        let host = NSView(frame: NSRect(origin: .zero, size: Metrics.host))
        host.wantsLayer = true
        host.layer?.backgroundColor = NSColor.clear.cgColor
        capsule.frame = NSRect(
            x: (Metrics.host.width - Metrics.minimumWidth) / 2,
            y: (Metrics.host.height - Metrics.capsuleHeight) / 2,
            width: Metrics.minimumWidth,
            height: Metrics.capsuleHeight
        )
        host.addSubview(capsule)
        panel.contentView = host
        super.init(window: panel)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func show(_ presentation: OverlayPresentation, position: OverlayPosition, screen: NSScreen? = nil) {
        presentationGeneration += 1
        self.position = position
        anchorScreen = screen
        autoHideTimer?.invalidate()
        capsule.layer?.removeAllAnimations()
        capsule.layer?.opacity = 1
        capsule.layer?.transform = CATransform3DIdentity
        apply(presentation, animateWidth: window?.isVisible == true)
        reposition()

        guard let panel = window else { return }
        let entering = !panel.isVisible
        panel.alphaValue = 1
        panel.orderFrontRegardless()
        if entering { playEntrance() }

        let autoHideDelay: TimeInterval? = switch presentation {
        case .profile: 1.6
        case .failure: 2.8
        default: nil
        }
        if let autoHideDelay {
            let timer = Timer(timeInterval: autoHideDelay, repeats: false) { [weak self] _ in
                MainActor.assumeIsolated { self?.hideAnimated() }
            }
            RunLoop.main.add(timer, forMode: .common)
            autoHideTimer = timer
        }
    }

    /// Updates a visible state without repeating the entrance animation.
    func update(_ presentation: OverlayPresentation) {
        apply(presentation, animateWidth: true)
    }

    func setLevel(_ rms: Double) {
        capsule.waveform.level = rms
    }

    func hideAnimated() {
        presentationGeneration += 1
        let hidingGeneration = presentationGeneration
        autoHideTimer?.invalidate()
        autoHideTimer = nil
        capsule.waveform.stop()
        guard let layer = capsule.layer, window?.isVisible == true else {
            window?.orderOut(nil)
            return
        }

        CATransaction.begin()
        CATransaction.setAnimationDuration(0.22)
        CATransaction.setAnimationTimingFunction(CAMediaTimingFunction(name: .easeIn))
        CATransaction.setCompletionBlock { [weak self] in
            DispatchQueue.main.async {
                guard let self, self.presentationGeneration == hidingGeneration else { return }
                self.window?.orderOut(nil)
                self.capsule.layer?.opacity = 1
                self.capsule.layer?.transform = CATransform3DIdentity
            }
        }
        layer.opacity = 0
        layer.transform = CATransform3DMakeScale(0.9, 0.9, 1)
        CATransaction.commit()
    }

    private func apply(_ presentation: OverlayPresentation, animateWidth: Bool) {
        let phase: String
        let text: String
        let live: Bool
        let waveform: Bool
        let dimmed: Bool

        switch presentation {
        case .starting:
            phase = "STARTING"
            text = "Starting…"
            live = false
            waveform = true
            dimmed = true
        case .listening(let placeholder):
            phase = "LIVE INPUT"
            text = placeholder.isEmpty ? "Listening…" : placeholder
            live = true
            waveform = true
            dimmed = true
        case .partial(let partial):
            phase = "LIVE INPUT"
            text = partial
            live = true
            waveform = true
            dimmed = false
        case .transcribing:
            phase = "PROCESSING"
            text = "Transcribing…"
            live = false
            waveform = false
            dimmed = true
        case .refining:
            phase = "PROCESSING"
            text = "Refining…"
            live = false
            waveform = false
            dimmed = true
        case .profile(let name, let activation):
            phase = "INPUT PROFILE"
            text = "\(name) · \(activation)"
            live = false
            waveform = false
            dimmed = false
        case .failure(let message):
            phase = "VOICE INPUT"
            text = message
            live = false
            waveform = false
            dimmed = false
        }

        let fitted = fitTail(text)
        capsule.set(phase: phase, text: fitted, live: live, dimmed: dimmed)
        waveform ? capsule.waveform.start() : capsule.waveform.stop()
        setCapsuleWidth(targetWidth(for: fitted), animated: animateWidth)
    }

    private func targetWidth(for text: String) -> CGFloat {
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: 18, weight: .medium),
        ]
        let textWidth = min(
            ceil((text as NSString).size(withAttributes: attributes).width),
            Metrics.maximumTextWidth
        )
        return min(max(Metrics.textChrome + textWidth + 2, Metrics.minimumWidth), Metrics.maximumWidth)
    }

    private func fitTail(_ text: String) -> String {
        guard targetTextWidth(text) > Metrics.maximumTextWidth else { return text }
        let characters = Array(text)
        var low = 0
        var high = characters.count
        while low < high {
            let middle = (low + high) / 2
            let candidate = "…" + String(characters[middle...])
            if targetTextWidth(candidate) <= Metrics.maximumTextWidth {
                high = middle
            } else {
                low = middle + 1
            }
        }
        return "…" + String(characters[low...])
    }

    private func targetTextWidth(_ text: String) -> CGFloat {
        (text as NSString).size(withAttributes: [
            .font: NSFont.systemFont(ofSize: 18, weight: .medium),
        ]).width
    }

    private func setCapsuleWidth(_ width: CGFloat, animated: Bool) {
        let frame = NSRect(
            x: (Metrics.host.width - width) / 2,
            y: (Metrics.host.height - Metrics.capsuleHeight) / 2,
            width: width,
            height: Metrics.capsuleHeight
        )
        if animated {
            NSAnimationContext.runAnimationGroup { context in
                context.duration = 0.25
                context.timingFunction = CAMediaTimingFunction(name: .easeOut)
                capsule.animator().frame = frame
            }
        } else {
            capsule.frame = frame
        }
    }

    private func playEntrance() {
        guard let layer = capsule.layer else { return }
        layer.opacity = 1
        layer.transform = CATransform3DIdentity

        let spring = CASpringAnimation(keyPath: "transform")
        spring.fromValue = CATransform3DTranslate(CATransform3DMakeScale(0.85, 0.85, 1), 0, -14, 0)
        spring.toValue = CATransform3DIdentity
        spring.mass = 1
        spring.stiffness = 260
        spring.damping = 20
        spring.initialVelocity = 0
        spring.duration = 0.35
        layer.add(spring, forKey: "overlayEntrance")

        let fade = CABasicAnimation(keyPath: "opacity")
        fade.fromValue = 0
        fade.toValue = 1
        fade.duration = 0.2
        layer.add(fade, forKey: "overlayFadeIn")
    }

    private func reposition() {
        guard let panel = window else { return }
        let pointer = NSEvent.mouseLocation
        let screen = anchorScreen
            ?? NSScreen.screens.first(where: { NSMouseInRect(pointer, $0.frame, false) })
            ?? panel.screen
            ?? NSScreen.main
        guard let visible = screen?.visibleFrame else { return }

        let origin = NSPoint(
            x: visible.midX - Metrics.host.width / 2,
            y: position == .top
                ? visible.maxY - Metrics.host.height - Metrics.edgeMargin
                : visible.minY + Metrics.edgeMargin
        )
        panel.setFrame(NSRect(origin: origin, size: Metrics.host), display: false)
    }
}

private final class PassiveOverlayPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

private final class OverlayCapsuleView: NSView {
    let waveform = RMSWaveformView(frame: .zero)
    private let waveModule = NSView()
    private let informationPod = NSView()
    private let phaseLabel = NSTextField(labelWithString: "LIVE INPUT")
    private let textLabel = NSTextField(labelWithString: "")
    private let liveDot = NSView()
    private let liveLabel = NSTextField(labelWithString: "LIVE")

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        layer?.shadowColor = NSColor.black.cgColor
        layer?.shadowOpacity = 0.45
        layer?.shadowRadius = 12
        layer?.shadowOffset = NSSize(width: 0, height: -4)

        waveModule.wantsLayer = true
        waveModule.layer?.backgroundColor = NSColor(rgb: 0xDFFF52).cgColor
        waveModule.layer?.cornerRadius = 16
        waveModule.addSubview(waveform)
        addSubview(waveModule)

        informationPod.wantsLayer = true
        informationPod.layer?.backgroundColor = NSColor(rgb: 0x171A1D).cgColor
        informationPod.layer?.cornerRadius = 12
        addSubview(informationPod, positioned: .below, relativeTo: waveModule)

        phaseLabel.font = .systemFont(ofSize: 10, weight: .semibold)
        phaseLabel.textColor = NSColor(rgb: 0xADB2B7)
        phaseLabel.backgroundColor = .clear
        textLabel.font = .systemFont(ofSize: 18, weight: .medium)
        textLabel.textColor = NSColor(rgb: 0xF5F5F7)
        textLabel.lineBreakMode = .byClipping
        textLabel.maximumNumberOfLines = 1
        informationPod.addSubview(phaseLabel)
        informationPod.addSubview(textLabel)

        liveDot.wantsLayer = true
        liveDot.layer?.backgroundColor = NSColor(rgb: 0xDFFF52).cgColor
        liveDot.layer?.cornerRadius = 3
        informationPod.addSubview(liveDot)
        liveLabel.font = .systemFont(ofSize: 10, weight: .semibold)
        liveLabel.textColor = NSColor(rgb: 0xDFFF52)
        informationPod.addSubview(liveLabel)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func layout() {
        super.layout()
        waveModule.frame = NSRect(x: 0, y: 0, width: 64, height: 64)
        waveform.frame = NSRect(x: 10, y: 16, width: 44, height: 32)
        informationPod.frame = NSRect(x: 62, y: 5, width: max(0, bounds.width - 62), height: 54)

        let indicatorWidth: CGFloat = liveLabel.isHidden ? 0 : 47
        let available = max(0, informationPod.bounds.width - 28 - indicatorWidth)
        phaseLabel.frame = NSRect(x: 16, y: 10, width: available, height: 13)
        textLabel.frame = NSRect(x: 16, y: 24, width: available, height: 24)
        liveDot.frame = NSRect(x: informationPod.bounds.width - 47, y: 24, width: 6, height: 6)
        liveLabel.frame = NSRect(x: informationPod.bounds.width - 36, y: 19, width: 31, height: 15)
    }

    func set(phase: String, text: String, live: Bool, dimmed: Bool) {
        phaseLabel.stringValue = phase
        textLabel.stringValue = text
        textLabel.alphaValue = dimmed ? 0.7 : 1
        liveDot.isHidden = !live
        liveLabel.isHidden = !live
        needsLayout = true
    }
}

private final class RMSWaveformView: NSView {
    var levelSource: (() -> Double)?
    var level: Double = 0 {
        didSet { level = min(max(level, 0), 1) }
    }

    private var displayTimer: Timer?
    private var smoothedLevel = 0.08
    private var phase = 0.0

    override var isFlipped: Bool { true }

    func start() {
        guard displayTimer == nil else { return }
        let timer = Timer(timeInterval: 1 / 30, repeats: true) { [weak self] _ in
            guard let self else { return }
            let next = min(max(self.levelSource?() ?? self.level, 0), 1)
            self.smoothedLevel += (next - self.smoothedLevel) * (next > self.smoothedLevel ? 0.48 : 0.16)
            self.phase += 0.23
            self.needsDisplay = true
        }
        RunLoop.main.add(timer, forMode: .common)
        displayTimer = timer
    }

    func stop() {
        displayTimer?.invalidate()
        displayTimer = nil
        smoothedLevel = 0.08
        needsDisplay = true
    }

    override func draw(_ dirtyRect: NSRect) {
        super.draw(dirtyRect)
        NSColor(rgb: 0x171A1D).setFill()
        let multipliers = [0.35, 0.75, 1.0, 0.68, 0.28]
        let xPositions: [CGFloat] = [3, 12, 21, 30, 39]
        for index in 0..<5 {
            let movement = 0.82 + 0.18 * sin(phase + Double(index) * 0.92)
            let normalized = max(0.08, smoothedLevel * multipliers[index] * movement)
            let height = CGFloat(4 + normalized * 25)
            let rect = NSRect(x: xPositions[index], y: (bounds.height - height) / 2, width: 5, height: height)
            NSBezierPath(roundedRect: rect, xRadius: 2.5, yRadius: 2.5).fill()
        }
    }
}

private extension NSColor {
    convenience init(rgb: Int) {
        self.init(
            calibratedRed: CGFloat((rgb >> 16) & 0xFF) / 255,
            green: CGFloat((rgb >> 8) & 0xFF) / 255,
            blue: CGFloat(rgb & 0xFF) / 255,
            alpha: 1
        )
    }
}
