import AppKit
import ApplicationServices
import AVFoundation
import CoreGraphics
import Foundation
import ServiceManagement
import UserNotifications

enum PlatformPermissions {
    static var hasAccessibility: Bool { AXIsProcessTrusted() }
    static var hasInputMonitoring: Bool { CGPreflightListenEventAccess() }

    @discardableResult
    static func requestAccessibility() -> Bool {
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        return AXIsProcessTrustedWithOptions(options)
    }

    @discardableResult
    static func requestInputMonitoring() -> Bool { CGRequestListenEventAccess() }

    static func requestMicrophone() async -> Bool {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized: return true
        case .notDetermined: return await AVCaptureDevice.requestAccess(for: .audio)
        default: return false
        }
    }
}

private func keyboardTapCallback(
    proxy: CGEventTapProxy,
    type: CGEventType,
    event: CGEvent,
    refcon: UnsafeMutableRawPointer?
) -> Unmanaged<CGEvent>? {
    guard let refcon else { return Unmanaged.passUnretained(event) }
    return Unmanaged<KeyboardMonitor>.fromOpaque(refcon).takeUnretainedValue()
        .handle(proxy: proxy, type: type, event: event)
}

enum ActivationCycleInput: Equatable {
    case down(startsChorded: Bool)
    case up
    case otherKey
    case recoveredRelease
}

struct ActivationCycle {
    private(set) var isDown = false
    private(set) var isChorded = false

    mutating func consume(_ input: ActivationCycleInput) -> PttGesture? {
        switch input {
        case .down(let startsChorded):
            guard !isDown else { return nil }
            isDown = true
            isChorded = startsChorded
            return startsChorded ? .cancelled : .pressed
        case .up:
            guard isDown else { return nil }
            isDown = false
            defer { isChorded = false }
            return isChorded ? nil : .released
        case .otherKey:
            guard isDown, !isChorded else { return nil }
            isChorded = true
            return .cancelled
        case .recoveredRelease:
            guard isDown else { return nil }
            isDown = false
            defer { isChorded = false }
            return isChorded ? nil : .recoveredRelease
        }
    }

    mutating func reset() {
        isDown = false
        isChorded = false
    }
}

struct KeyPressLatch {
    private(set) var isDown = false

    mutating func begin() -> Bool {
        guard !isDown else { return false }
        isDown = true
        return true
    }

    mutating func end() { isDown = false }

    mutating func reconcile(physicalKeyDown: Bool) {
        if !physicalKeyDown { isDown = false }
    }
}

struct MouseButtonCycle {
    private(set) var pressedButtonMask = 0

    var hasPressedButton: Bool { pressedButtonMask != 0 }

    mutating func consume(type: CGEventType, buttonNumber: Int64) -> Bool {
        guard buttonNumber >= 0, buttonNumber < Int64(Int.bitWidth) else { return false }
        let buttonMask = 1 << Int(buttonNumber)
        switch type {
        case .leftMouseDown, .rightMouseDown, .otherMouseDown:
            pressedButtonMask |= buttonMask
            return true
        case .leftMouseUp, .rightMouseUp, .otherMouseUp:
            pressedButtonMask &= ~buttonMask
            return false
        default:
            return false
        }
    }

    mutating func reconcile(pressedButtonMask: Int) {
        self.pressedButtonMask = pressedButtonMask
    }

    mutating func reset() { pressedButtonMask = 0 }
}

enum EscapeKeyAction: Equatable {
    case cancelSession
    case replaceChordCancellation
}

struct EscapeKeyCycle {
    private(set) var isDown = false

    mutating func keyDown(activationCycle: ActivationCycle) -> EscapeKeyAction? {
        guard !isDown else { return nil }
        isDown = true
        if activationCycle.isDown {
            return activationCycle.isChorded ? .cancelSession : .replaceChordCancellation
        }
        return .cancelSession
    }

    mutating func keyUp() { isDown = false }

    mutating func reconcile(physicalKeyDown: Bool) {
        if !physicalKeyDown { isDown = false }
    }
}

final class KeyboardMonitor {
    static let injectionTag: Int64 = 0x47554A49
    static let escapeKeyCode: CGKeyCode = 53

    var onPressed: (() -> Void)?
    var onReleased: (() -> Void)?
    var onCancelled: (() -> Void)?
    var onRecoveredRelease: (() -> Void)?
    var onEscape: (() -> Void)?
    var onSubmitted: (() -> Void)?
    var onSwitchProfile: (() -> Void)?

    private var activationKey: ActivationKey
    private var tap: CFMachPort?
    private var runLoopSource: CFRunLoopSource?
    private var watchdog: Timer?
    private var activationCycle = ActivationCycle()
    private var capsLockFlagState = CGEventSource.flagsState(.combinedSessionState)
        .contains(.maskAlphaShift)
    private var profileSwitchLatch = KeyPressLatch()
    private var escapeKeyCycle = EscapeKeyCycle()
    private var mouseButtonCycle = MouseButtonCycle()
    private var pressedNonModifierKeys: Set<CGKeyCode> = []

    init(activationKey: ActivationKey) { self.activationKey = activationKey }

    func update(activationKey: ActivationKey) {
        guard self.activationKey != activationKey else { return }
        self.activationKey = activationKey
        activationCycle.reset()
        capsLockFlagState = CGEventSource.flagsState(.combinedSessionState)
            .contains(.maskAlphaShift)
        escapeKeyCycle.keyUp()
        mouseButtonCycle.reconcile(pressedButtonMask: NSEvent.pressedMouseButtons)
        pressedNonModifierKeys.removeAll(keepingCapacity: true)
    }

    func start() throws {
        guard tap == nil else { return }
        capsLockFlagState = CGEventSource.flagsState(.combinedSessionState)
            .contains(.maskAlphaShift)
        mouseButtonCycle.reconcile(pressedButtonMask: NSEvent.pressedMouseButtons)
        let mask = (1 << CGEventType.flagsChanged.rawValue)
            | (1 << CGEventType.keyDown.rawValue)
            | (1 << CGEventType.keyUp.rawValue)
            | (1 << CGEventType.leftMouseDown.rawValue)
            | (1 << CGEventType.leftMouseUp.rawValue)
            | (1 << CGEventType.rightMouseDown.rawValue)
            | (1 << CGEventType.rightMouseUp.rawValue)
            | (1 << CGEventType.otherMouseDown.rawValue)
            | (1 << CGEventType.otherMouseUp.rawValue)
            | (1 << CGEventType.tapDisabledByTimeout.rawValue)
            | (1 << CGEventType.tapDisabledByUserInput.rawValue)
        guard let tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: .listenOnly,
            eventsOfInterest: CGEventMask(mask),
            callback: keyboardTapCallback,
            userInfo: Unmanaged.passUnretained(self).toOpaque()
        ) else {
            throw SpeechFault(.service, "Input Monitoring permission is required for the activation key.")
        }
        self.tap = tap
        let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        runLoopSource = source
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        watchdog = Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { [weak self] _ in
            self?.checkPhysicalState()
        }
    }

    func stop() {
        watchdog?.invalidate()
        watchdog = nil
        if let source = runLoopSource { CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes) }
        if let tap { CGEvent.tapEnable(tap: tap, enable: false) }
        runLoopSource = nil
        tap = nil
        activationCycle.reset()
        profileSwitchLatch.end()
        escapeKeyCycle.keyUp()
        mouseButtonCycle.reset()
        pressedNonModifierKeys.removeAll(keepingCapacity: true)
    }

    fileprivate func handle(
        proxy: CGEventTapProxy,
        type: CGEventType,
        event: CGEvent
    ) -> Unmanaged<CGEvent>? {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            // Events may have been lost while the tap was disabled. Fail the
            // active activation cycle closed and discard key latches that can
            // no longer be paired with a trustworthy key-up.
            if let gesture = activationCycle.consume(.otherKey) { emit(gesture) }
            profileSwitchLatch.end()
            escapeKeyCycle.keyUp()
            mouseButtonCycle.reconcile(pressedButtonMask: NSEvent.pressedMouseButtons)
            pressedNonModifierKeys.removeAll(keepingCapacity: true)
            if let tap { CGEvent.tapEnable(tap: tap, enable: true) }
            return Unmanaged.passUnretained(event)
        }
        if event.getIntegerValueField(.eventSourceUserData) == Self.injectionTag {
            return Unmanaged.passUnretained(event)
        }

        if mouseButtonCycle.consume(
            type: type,
            buttonNumber: event.getIntegerValueField(.mouseEventButtonNumber)
        ) {
            if let gesture = activationCycle.consume(.otherKey) { emit(gesture) }
            return Unmanaged.passUnretained(event)
        }
        if type == .leftMouseUp || type == .rightMouseUp || type == .otherMouseUp {
            return Unmanaged.passUnretained(event)
        }

        let code = CGKeyCode(event.getIntegerValueField(.keyboardEventKeycode))
        let isActivation = code == activationKey.keyCode
        var escapeAction: EscapeKeyAction?
        if code == Self.escapeKeyCode {
            if type == .keyDown {
                escapeAction = escapeKeyCycle.keyDown(activationCycle: activationCycle)
            } else if type == .keyUp {
                escapeKeyCycle.keyUp()
            }
        }

        if !isActivation {
            if type == .keyDown { pressedNonModifierKeys.insert(code) }
            if type == .keyUp { pressedNonModifierKeys.remove(code) }
        }

        if type == .keyDown, code == 36 { onSubmitted?() }
        if code == 5, type == .keyUp { profileSwitchLatch.end() }

        if let input = activationCycleInput(
            type: type, eventFlags: event.flags, isActivation: isActivation),
           let gesture = activationCycle.consume(input) {
            if escapeAction != .replaceChordCancellation { emit(gesture) }
        }

        if escapeAction != nil { onEscape?() }

        if type == .keyDown, code == 5,
           event.flags.contains(.maskAlternate), event.flags.contains(.maskShift),
           profileSwitchLatch.begin() {
            onSwitchProfile?()
        }
        return Unmanaged.passUnretained(event)
    }

    private func activationCycleInput(
        type: CGEventType,
        eventFlags: CGEventFlags,
        isActivation: Bool
    ) -> ActivationCycleInput? {
        guard isActivation else {
            return type == .keyDown || type == .flagsChanged ? .otherKey : nil
        }
        switch type {
        case .keyDown:
            return .down(startsChorded: startsChorded(eventFlags))
        case .keyUp:
            return .up
        case .flagsChanged:
            if activationKey == .capsLock {
                let nextFlagState = eventFlags.contains(.maskAlphaShift)
                let isPress = Self.capsLockEventIsPress(
                    previousFlagState: capsLockFlagState,
                    eventFlagState: nextFlagState)
                capsLockFlagState = nextFlagState
                return isPress ? .down(startsChorded: startsChorded(eventFlags)) : .up
            }
            if eventFlags.contains(activationKey.modifierFlag) {
                return .down(startsChorded: startsChorded(eventFlags))
            }
            return .up
        default:
            return nil
        }
    }

    private func startsChorded(_ flags: CGEventFlags) -> Bool {
        hasUnexpectedModifiers(flags)
            || !pressedNonModifierKeys.isEmpty
            || mouseButtonCycle.hasPressedButton
            || activationKey.siblingKeyCodes.contains(where: {
                CGEventSource.keyState(.combinedSessionState, key: $0)
            })
    }

    private func emit(_ gesture: PttGesture) {
        switch gesture {
        case .pressed: onPressed?()
        case .released: onReleased?()
        case .cancelled: onCancelled?()
        case .recoveredRelease: onRecoveredRelease?()
        }
    }

    private func physicalKeyDown() -> Bool {
        CGEventSource.keyState(.combinedSessionState, key: activationKey.keyCode)
    }

    private func checkPhysicalState() {
        profileSwitchLatch.reconcile(
            physicalKeyDown: CGEventSource.keyState(.combinedSessionState, key: 5))
        escapeKeyCycle.reconcile(
            physicalKeyDown: CGEventSource.keyState(
                .combinedSessionState, key: Self.escapeKeyCode))
        mouseButtonCycle.reconcile(pressedButtonMask: NSEvent.pressedMouseButtons)
        pressedNonModifierKeys = Set(pressedNonModifierKeys.filter {
            CGEventSource.keyState(.combinedSessionState, key: $0)
        })
        guard activationCycle.isDown, !physicalKeyDown() else { return }
        guard let gesture = activationCycle.consume(.recoveredRelease) else { return }
        // Caps Lock changes its lock flag on key-down and does not expose a
        // consistent key-up flagsChanged event. Its physical key-state poll is
        // therefore the normal release path, not an ambiguous recovery.
        emit(activationKey == .capsLock ? .released : gesture)
    }

    static func capsLockEventIsPress(previousFlagState: Bool, eventFlagState: Bool) -> Bool {
        previousFlagState != eventFlagState
    }

    private func hasUnexpectedModifiers(_ flags: CGEventFlags) -> Bool {
        Self.containsUnexpectedModifier(flags, activationKey: activationKey)
    }

    static func containsUnexpectedModifier(
        _ flags: CGEventFlags,
        activationKey: ActivationKey
    ) -> Bool {
        var allowed: CGEventFlags = []
        switch activationKey {
        case .rightControl, .leftControl: allowed = .maskControl
        case .rightOption: allowed = .maskAlternate
        case .rightShift: allowed = .maskShift
        case .capsLock: allowed = .maskAlphaShift
        case .fn: allowed = .maskSecondaryFn
        }
        let relevant: CGEventFlags = [
            .maskControl, .maskAlternate, .maskShift, .maskCommand, .maskSecondaryFn,
        ]
        return !flags.intersection(relevant).subtracting(allowed).isEmpty
    }
}

final class AudioCapture: @unchecked Sendable {
    static let sampleRate = 16_000.0
    var onChunk: ((Data) -> Void)? {
        get { callbackLock.withLock { chunkHandler } }
        set { callbackLock.withLock { chunkHandler = newValue } }
    }
    var onLevel: ((Float) -> Void)? {
        get { callbackLock.withLock { levelHandler } }
        set { callbackLock.withLock { levelHandler = newValue } }
    }

    private let engine = AVAudioEngine()
    private let queue = DispatchQueue(label: "gujiguji.audio")
    private let callbackLock = NSLock()
    private var chunkHandler: ((Data) -> Void)?
    private var levelHandler: ((Float) -> Void)?
    private var converter: AVAudioConverter?
    private var targetFormat: AVAudioFormat?
    private var sessionActive = false
    private var sessionGeneration = 0
    private var tapInstalled = false
    private var firstFrame: CheckedContinuation<Void, Error>?

    func beginSession() async throws {
        try await withCheckedThrowingContinuation { continuation in
            queue.async {
                self.sessionGeneration &+= 1
                self.sessionActive = true
                self.firstFrame = continuation
                do {
                    if !self.tapInstalled { try self.startEngine() }
                } catch {
                    self.firstFrame = nil
                    continuation.resume(throwing: error)
                    return
                }
                let generation = self.sessionGeneration
                self.queue.asyncAfter(deadline: .now() + 3) { [weak self] in
                    guard let self, sessionGeneration == generation, let pending = firstFrame else { return }
                    firstFrame = nil
                    pending.resume()
                }
            }
        }
    }

    /// Drains all tap buffers already queued before ending the session. This keeps
    /// the final syllable available to the recognizer on key release.
    func endSession() async {
        await withCheckedContinuation { continuation in
            queue.async { [weak self] in
                guard let self else { continuation.resume(); return }
                sessionActive = false
                scheduleReleaseOnQueue(for: sessionGeneration)
                continuation.resume()
            }
        }
    }

    /// Ends without draining callbacks; used for explicit cancellation.
    func cancelSession() {
        queue.async { [weak self] in
            guard let self else { return }
            sessionActive = false
            scheduleReleaseOnQueue(for: sessionGeneration)
        }
    }

    func release() {
        queue.sync {
            sessionGeneration &+= 1
            releaseOnQueue()
        }
    }

    /// Must be called while executing on queue.
    private func scheduleReleaseOnQueue(for generation: Int) {
        queue.asyncAfter(deadline: .now() + 60) { [weak self] in
            guard let self, !sessionActive, sessionGeneration == generation else { return }
            releaseOnQueue()
        }
    }

    /// Must be called while executing on queue.
    private func releaseOnQueue() {
        sessionActive = false
        firstFrame?.resume(throwing: CancellationError())
        firstFrame = nil
        if tapInstalled {
            engine.inputNode.removeTap(onBus: 0)
            engine.stop()
        }
        converter = nil
        targetFormat = nil
        tapInstalled = false
    }

    private func startEngine() throws {
        let input = engine.inputNode
        let sourceFormat = input.outputFormat(forBus: 0)
        guard sourceFormat.sampleRate > 0,
              let target = AVAudioFormat(commonFormat: .pcmFormatInt16,
                                         sampleRate: Self.sampleRate,
                                         channels: 1,
                                         interleaved: true),
              let converter = AVAudioConverter(from: sourceFormat, to: target) else {
            throw SpeechFault(.service, "The selected microphone format is not supported.")
        }
        self.converter = converter
        targetFormat = target
        input.installTap(onBus: 0, bufferSize: 1_024, format: sourceFormat) { [weak self] buffer, _ in
            self?.convert(buffer)
        }
        tapInstalled = true
        do {
            engine.prepare()
            try engine.start()
        } catch {
            // installTap succeeds before engine.start can fail. Remove it here so
            // the next attempt cannot hit AVAudioEngine's duplicate-tap abort.
            input.removeTap(onBus: 0)
            engine.stop()
            self.converter = nil
            targetFormat = nil
            tapInstalled = false
            throw error
        }
    }

    private func convert(_ input: AVAudioPCMBuffer) {
        // AVAudioEngine owns and may reuse the tap buffer as soon as this callback
        // returns. Copy it before dispatching conversion to our serial queue.
        guard let input = OwnedAudioBuffer(copying: input) else { return }
        queue.async { [weak self] in
            guard let self, let converter, let targetFormat else { return }
            let ratio = targetFormat.sampleRate / input.buffer.format.sampleRate
            let capacity = AVAudioFrameCount(ceil(Double(input.buffer.frameLength) * ratio)) + 16
            guard let output = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: capacity) else { return }
            var error: NSError?
            let status = converter.convert(to: output, error: &error) { _, outStatus in
                input.next(status: outStatus)
            }
            guard status != .error, output.frameLength > 0,
                  let pointer = output.int16ChannelData?[0] else { return }
            let count = Int(output.frameLength)
            let data = Data(bytes: pointer, count: count * MemoryLayout<Int16>.size)
            let rms = (0..<count).reduce(0.0) { value, index in
                let sample = Double(pointer[index]) / Double(Int16.max)
                return value + sample * sample
            }
            let level = Float(min(1, sqrt(rms / Double(max(1, count))) * 5))
            if let pending = firstFrame { firstFrame = nil; pending.resume() }
            guard sessionActive else { return }
            let callbacks = callbackLock.withLock { (self.chunkHandler, self.levelHandler) }
            callbacks.0?(data)
            callbacks.1?(level)
        }
    }
}

/// A tap buffer copy whose storage is exclusively owned by the conversion queue.
private final class OwnedAudioBuffer: @unchecked Sendable {
    let buffer: AVAudioPCMBuffer
    private let lock = NSLock()
    private var supplied = false

    init?(copying source: AVAudioPCMBuffer) {
        guard let copy = AVAudioPCMBuffer(
            pcmFormat: source.format,
            frameCapacity: source.frameLength
        ) else { return nil }
        copy.frameLength = source.frameLength
        let sourceBuffers = UnsafeMutableAudioBufferListPointer(source.mutableAudioBufferList)
        let destinationBuffers = UnsafeMutableAudioBufferListPointer(copy.mutableAudioBufferList)
        guard sourceBuffers.count == destinationBuffers.count else { return nil }
        for index in sourceBuffers.indices {
            let sourceBuffer = sourceBuffers[index]
            let byteCount = min(Int(sourceBuffer.mDataByteSize), Int(destinationBuffers[index].mDataByteSize))
            guard byteCount == 0 || (sourceBuffer.mData != nil && destinationBuffers[index].mData != nil) else {
                return nil
            }
            if byteCount > 0 {
                memcpy(destinationBuffers[index].mData, sourceBuffer.mData, byteCount)
            }
            destinationBuffers[index].mDataByteSize = UInt32(byteCount)
        }
        buffer = copy
    }

    func next(status: UnsafeMutablePointer<AVAudioConverterInputStatus>) -> AVAudioBuffer? {
        lock.withLock {
            guard !supplied else {
                status.pointee = .noDataNow
                return nil
            }
            supplied = true
            status.pointee = .haveData
            return buffer
        }
    }
}

enum InputFocusMode: String, Sendable {
    case exactElement
    case windowEditorProxy
}

struct InputTarget: @unchecked Sendable {
    let processIdentifier: pid_t
    let application: AXUIElement
    let window: AXUIElement?
    let element: AXUIElement
    let focusAnchor: AXUIElement
    let focusMode: InputFocusMode

    init(
        processIdentifier: pid_t,
        application: AXUIElement,
        window: AXUIElement?,
        element: AXUIElement,
        focusAnchor: AXUIElement? = nil,
        focusMode: InputFocusMode = .exactElement
    ) {
        self.processIdentifier = processIdentifier
        self.application = application
        self.window = window
        self.element = element
        self.focusAnchor = focusAnchor ?? element
        self.focusMode = focusMode
    }
}

struct InjectionResult: Sendable {
    let success: Bool
    let utf16UnitsInserted: Int
    let utf16UnitsSubmitted: Int
    let verified: Bool
    let error: String?

    init(
        success: Bool,
        utf16UnitsInserted: Int,
        error: String?,
        utf16UnitsSubmitted: Int? = nil,
        verified: Bool? = nil
    ) {
        self.success = success
        self.utf16UnitsInserted = utf16UnitsInserted
        self.utf16UnitsSubmitted = utf16UnitsSubmitted ?? utf16UnitsInserted
        self.verified = verified ?? success
        self.error = error
    }
}

enum DeliveryObservation: Equatable, Sendable {
    case expected
    case unchanged
    case ambiguous
}

struct AccessibilityElementTraits {
    let enabled: Bool?
    let editable: Bool?
    let role: String?
    let roleDescription: String?
    let valueSettable: Bool
    let selectedTextSettable: Bool
}

private struct ResolvedInputElement {
    let element: AXUIElement
    let focusAnchor: AXUIElement
    let focusMode: InputFocusMode
}

enum AccessibilityReader {
    static func captureTarget(logFailure: Bool = false) -> InputTarget? {
        let system = AXUIElementCreateSystemWide()
        let application: AXUIElement?
        if let frontmost = NSWorkspace.shared.frontmostApplication {
            application = AXUIElementCreateApplication(frontmost.processIdentifier)
        } else if let systemApplication: AXUIElement = attribute(system, kAXFocusedApplicationAttribute) {
            application = systemApplication
        } else {
            application = nil
        }
        guard let application else {
            if logFailure { AppLog.write("input target not found: no focused application") }
            return nil
        }
        AXUIElementSetMessagingTimeout(application, 0.25)
        var pid: pid_t = 0
        AXUIElementGetPid(application, &pid)

        let appFocused: AXUIElement? = attribute(application, kAXFocusedUIElementAttribute)
        let rawSystemFocused: AXUIElement? = attribute(system, kAXFocusedUIElementAttribute)
        let systemFocused = rawSystemFocused.flatMap { elementBelongs($0, to: pid) ? $0 : nil }
        let focusedWindow: AXUIElement? = attribute(application, kAXFocusedWindowAttribute)
        var seeds: [(element: AXUIElement, representsFocus: Bool, allowEditorProxy: Bool)] = []
        if let appFocused { seeds.append((appFocused, true, true)) }
        if let systemFocused, !seeds.contains(where: { CFEqual($0.element, systemFocused) }) {
            seeds.append((systemFocused, true, true))
        }
        if let focusedWindow, !seeds.contains(where: { CFEqual($0.element, focusedWindow) }) {
            seeds.append((focusedWindow, false, true))
        }

        guard let resolved = seeds.lazy.compactMap({
            resolveInputElement(
                from: $0.element,
                seedRepresentsFocus: $0.representsFocus,
                allowEditorProxy: $0.allowEditorProxy)
        }).first else {
            if logFailure {
                let bundle = NSRunningApplication(processIdentifier: pid)?.bundleIdentifier ?? "unknown"
                AppLog.write(
                    "input target not found: app=\(bundle) "
                    + "appFocused={\(diagnosticSummary(appFocused))} "
                    + "systemFocused={\(diagnosticSummary(systemFocused))} "
                    + "focusedWindow={\(diagnosticSummary(focusedWindow))}")
            }
            return nil
        }
        let window: AXUIElement? = attribute(resolved.element, kAXWindowAttribute) ?? focusedWindow
        return InputTarget(
            processIdentifier: pid,
            application: application,
            window: window,
            element: resolved.element,
            focusAnchor: resolved.focusAnchor,
            focusMode: resolved.focusMode)
    }

    static func isCurrent(_ target: InputTarget) -> Bool {
        if let frontmost = NSWorkspace.shared.frontmostApplication,
           frontmost.processIdentifier != target.processIdentifier { return false }
        if !focusedWindowMatches(target) { return false }

        let focused: AXUIElement? = attribute(target.application, kAXFocusedUIElementAttribute)
        switch target.focusMode {
        case .exactElement:
            if attribute(target.element, kAXFocusedAttribute) as Bool? == true { return true }
            if let focused {
                if CFEqual(focused, target.element) { return true }
                if CFEqual(focused, target.focusAnchor), CFEqual(target.focusAnchor, target.element) { return true }
                if let resolved = resolveInputElement(
                    from: focused, seedRepresentsFocus: true, allowEditorProxy: false),
                   CFEqual(resolved.element, target.element) { return true }
            }
            return false
        case .windowEditorProxy:
            guard attribute(target.element, kAXFocusedAttribute) as Bool? == true else { return false }
            // A capture that starts at AXFocusedWindow uses the window itself as
            // its anchor. AXFocusedUIElement will not equal that window after the
            // Chromium accessibility bridge recovers, so the focused editor plus
            // the already validated process/window is the stable identity here.
            if let window = target.window, CFEqual(target.focusAnchor, window) { return true }
            guard let focused else { return false }
            return CFEqual(focused, target.focusAnchor)
        }
    }

    static func screen(for target: InputTarget) -> NSScreen? {
        guard let window = target.window,
              let position = pointAttribute(window, kAXPositionAttribute),
              let size = sizeAttribute(window, kAXSizeAttribute),
              let primary = NSScreen.screens.first else { return nil }
        let center = CGPoint(x: position.x + size.width / 2, y: position.y + size.height / 2)
        return NSScreen.screens.first { screen in
            let accessibilityFrame = CGRect(
                x: screen.frame.minX,
                y: primary.frame.maxY - screen.frame.maxY,
                width: screen.frame.width,
                height: screen.frame.height)
            return accessibilityFrame.contains(center)
        }
    }

    static func focusedValue() -> String? {
        guard let target = captureTarget() else { return nil }
        return focusedValue(of: target)
    }

    static func focusedValue(of target: InputTarget) -> String? {
        guard isCurrent(target) else { return nil }
        return stringAttribute(target.element, kAXValueAttribute)
    }

    static func surroundingText(for target: InputTarget,
                                maxCharacters: Int = 1_500, maxNodes: Int = 250) -> String? {
        guard isCurrent(target) else { return nil }
        AXUIElementSetMessagingTimeout(target.application, 0.25)
        if let value = stringAttribute(target.element, kAXValueAttribute), !value.isEmpty {
            return String(value.suffix(maxCharacters))
        }
        let root = target.window ?? target.application
        var queue = [root]
        var visited = 0
        while !queue.isEmpty && visited < maxNodes {
            let element = queue.removeFirst()
            visited += 1
            if let value = stringAttribute(element, kAXValueAttribute), !value.isEmpty {
                return String(value.suffix(maxCharacters))
            }
            if let children: [AXUIElement] = attribute(element, kAXChildrenAttribute) {
                queue.append(contentsOf: children.prefix(maxNodes - visited))
            }
        }
        return nil
    }

    @MainActor
    static func inject(_ text: String, into target: InputTarget) async -> InjectionResult {
        assert(Thread.isMainThread, "Accessibility text injection must run on the main thread.")
        guard !text.isEmpty else { return InjectionResult(success: true, utf16UnitsInserted: 0, error: nil) }
        guard isCurrent(target) else {
            return InjectionResult(success: false, utf16UnitsInserted: 0,
                                   error: "The focused app or input control changed while gujiguji was processing.")
        }

        let verificationValues: (before: String, expected: String)? = {
            guard target.focusMode == .exactElement,
                  let before = focusedValue(of: target),
                  let range = selectedTextRange(of: target) else { return nil }
            let field = before as NSString
            guard NSMaxRange(range) <= field.length else { return nil }
            return (before, field.replacingCharacters(in: range, with: text))
        }()
        guard let source = CGEventSource(stateID: .privateState) else {
            return InjectionResult(success: false, utf16UnitsInserted: 0,
                                   error: "macOS could not create a keyboard event source.")
        }

        // Build the complete batch before posting anything so an allocation
        // failure cannot leave a partial prefix in the editor.
        var events: [(down: CGEvent, up: CGEvent, unitCount: Int)] = []
        for units in unicodeEventUnits(text) {
            guard let down = CGEvent(keyboardEventSource: source, virtualKey: 0, keyDown: true),
                  let up = CGEvent(keyboardEventSource: source, virtualKey: 0, keyDown: false) else {
                return InjectionResult(
                    success: false,
                    utf16UnitsInserted: 0,
                    error: "macOS could not create keyboard events.",
                    utf16UnitsSubmitted: 0,
                    verified: false)
            }
            down.flags = []
            up.flags = []
            down.setIntegerValueField(.eventSourceUserData, value: KeyboardMonitor.injectionTag)
            up.setIntegerValueField(.eventSourceUserData, value: KeyboardMonitor.injectionTag)
            units.withUnsafeBufferPointer { pointer in
                down.keyboardSetUnicodeString(stringLength: units.count, unicodeString: pointer.baseAddress!)
                up.keyboardSetUnicodeString(stringLength: units.count, unicodeString: pointer.baseAddress!)
            }
            events.append((down, up, units.count))
        }

        var submitted = 0
        for event in events {
            event.down.postToPid(target.processIdentifier)
            event.up.postToPid(target.processIdentifier)
            submitted += event.unitCount
        }
        await Task.yield()

        if let verificationValues {
            var latestValue: String?
            for delay in [10, 25, 50, 100, 200, 400] {
                if Task.isCancelled {
                    return InjectionResult(
                        success: true,
                        utf16UnitsInserted: 0,
                        error: nil,
                        utf16UnitsSubmitted: submitted,
                        verified: false)
                }
                try? await Task.sleep(for: .milliseconds(delay))
                guard isCurrent(target) else {
                    return InjectionResult(
                        success: true,
                        utf16UnitsInserted: 0,
                        error: nil,
                        utf16UnitsSubmitted: submitted,
                        verified: false)
                }
                latestValue = focusedValue(of: target)
                if classifyDelivery(
                    before: verificationValues.before,
                    expected: verificationValues.expected,
                    current: latestValue) == .expected {
                    return InjectionResult(
                        success: true,
                        utf16UnitsInserted: text.utf16.count,
                        error: nil,
                        utf16UnitsSubmitted: submitted,
                        verified: true)
                }
            }
            switch classifyDelivery(
                before: verificationValues.before,
                expected: verificationValues.expected,
                current: latestValue) {
            case .expected:
                return InjectionResult(
                    success: true,
                    utf16UnitsInserted: text.utf16.count,
                    error: nil,
                    utf16UnitsSubmitted: submitted,
                    verified: true)
            case .unchanged:
                return InjectionResult(
                    success: false,
                    utf16UnitsInserted: 0,
                    error: "macOS confirmed that the target control did not receive the text.",
                    utf16UnitsSubmitted: submitted,
                    verified: false)
            case .ambiguous:
                return InjectionResult(
                    success: true,
                    utf16UnitsInserted: 0,
                    error: nil,
                    utf16UnitsSubmitted: submitted,
                    verified: false)
            }
        }
        return InjectionResult(
            success: true,
            utf16UnitsInserted: 0,
            error: nil,
            utf16UnitsSubmitted: submitted,
            verified: false)
    }

    static func unicodeEventUnits(_ text: String) -> [[UInt16]] {
        text.unicodeScalars.map { Array(String($0).utf16) }
    }

    static func classifyDelivery(before: String, expected: String, current: String?) -> DeliveryObservation {
        guard let current else { return .ambiguous }
        if current == expected { return .expected }
        if current == before { return .unchanged }
        return .ambiguous
    }

    private static func attribute<T>(_ element: AXUIElement, _ name: String) -> T? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success else { return nil }
        return value as? T
    }

    private static func stringAttribute(_ element: AXUIElement, _ name: String) -> String? {
        attribute(element, name) as String?
    }

    static func classifiesAsTextEntry(_ traits: AccessibilityElementTraits) -> Bool {
        if traits.enabled == false { return false }
        if traits.editable == true || traits.selectedTextSettable { return true }
        if let role = traits.role, role == kAXTextFieldRole || role == kAXTextAreaRole {
            return true
        }
        let description = traits.roleDescription?.lowercased() ?? ""
        return ["text field", "text area", "editable text", "edit text", "文本框", "文本区域"]
            .contains(where: description.contains)
    }

    static func classifiesAsEditorProxy(_ traits: AccessibilityElementTraits) -> Bool {
        guard traits.enabled != false, traits.valueSettable else { return false }
        let identity = [traits.role, traits.roleDescription]
            .compactMap { $0?.lowercased() }
            .joined(separator: " ")
        return identity.contains("editor")
    }

    private static func isTextEntry(_ element: AXUIElement) -> Bool {
        classifiesAsTextEntry(traits(of: element))
    }

    private static func traits(of element: AXUIElement) -> AccessibilityElementTraits {
        var valueSettable = DarwinBoolean(false)
        _ = AXUIElementIsAttributeSettable(
            element, kAXValueAttribute as CFString, &valueSettable)
        var selectedTextSettable = DarwinBoolean(false)
        _ = AXUIElementIsAttributeSettable(
            element, kAXSelectedTextAttribute as CFString, &selectedTextSettable)
        return AccessibilityElementTraits(
            enabled: attribute(element, kAXEnabledAttribute),
            editable: attribute(element, kAXIsEditableAttribute),
            role: attribute(element, kAXRoleAttribute),
            roleDescription: attribute(element, kAXRoleDescriptionAttribute),
            valueSettable: valueSettable.boolValue,
            selectedTextSettable: selectedTextSettable.boolValue)
    }

    private static func resolveInputElement(
        from seed: AXUIElement,
        seedRepresentsFocus: Bool,
        allowEditorProxy: Bool
    ) -> ResolvedInputElement? {
        let seedTraits = traits(of: seed)
        if classifiesAsTextEntry(seedTraits) || (seedRepresentsFocus && classifiesAsEditorProxy(seedTraits)) {
            return ResolvedInputElement(
                element: seed,
                focusAnchor: seed,
                focusMode: classifiesAsEditorProxy(seedTraits) || !seedRepresentsFocus
                    ? .windowEditorProxy : .exactElement)
        }

        var ancestor = seed
        var ancestors = [seed]
        for _ in 0..<8 {
            guard let parent: AXUIElement = attribute(ancestor, kAXParentAttribute),
                  !ancestors.contains(where: { CFEqual($0, parent) }) else { break }
            let parentTraits = traits(of: parent)
            if classifiesAsTextEntry(parentTraits) || classifiesAsEditorProxy(parentTraits) {
                return ResolvedInputElement(
                    element: parent,
                    focusAnchor: seed,
                    focusMode: classifiesAsEditorProxy(parentTraits) || !seedRepresentsFocus
                        ? .windowEditorProxy : .exactElement)
            }
            ancestors.append(parent)
            ancestor = parent
        }

        let maxNodes = 2_000
        var queue = [seed]
        var nextIndex = 0
        var visitedHashes: Set<CFHashCode> = []
        while nextIndex < queue.count, visitedHashes.count < maxNodes {
            let element = queue[nextIndex]
            nextIndex += 1
            guard visitedHashes.insert(CFHash(element)).inserted else { continue }
            if !CFEqual(element, seed) {
                let focused = attribute(element, kAXFocusedAttribute) as Bool? == true
                if focused {
                    let elementTraits = traits(of: element)
                    if classifiesAsTextEntry(elementTraits)
                        || (allowEditorProxy && classifiesAsEditorProxy(elementTraits)) {
                        return ResolvedInputElement(
                            element: element,
                            focusAnchor: seed,
                            focusMode: classifiesAsEditorProxy(elementTraits)
                                ? .windowEditorProxy : .exactElement)
                    }
                }
            }
            if let children: [AXUIElement] = attribute(element, kAXChildrenAttribute) {
                let queuedButUnvisited = queue.count - nextIndex
                let available = max(0, maxNodes - visitedHashes.count - queuedButUnvisited)
                queue.append(contentsOf: children.prefix(available))
            }
        }
        return nil
    }

    private static func elementBelongs(_ element: AXUIElement, to processIdentifier: pid_t) -> Bool {
        var candidatePID: pid_t = 0
        return AXUIElementGetPid(element, &candidatePID) == .success && candidatePID == processIdentifier
    }

    private static func focusedWindowMatches(_ target: InputTarget) -> Bool {
        guard let expected = target.window,
              let current: AXUIElement = attribute(target.application, kAXFocusedWindowAttribute) else {
            return true
        }
        if CFEqual(expected, current) { return true }
        let expectedIdentifier: String? = attribute(expected, kAXIdentifierAttribute)
        let currentIdentifier: String? = attribute(current, kAXIdentifierAttribute)
        if let expectedIdentifier, let currentIdentifier { return expectedIdentifier == currentIdentifier }
        return false
    }

    private static func diagnosticSummary(_ element: AXUIElement?) -> String {
        guard let element else { return "nil" }
        let traits = traits(of: element)
        let childCount = (attribute(element, kAXChildrenAttribute) as [AXUIElement]?)?.count ?? 0
        let focused = attribute(element, kAXFocusedAttribute) as Bool? ?? false
        return "role=\(traits.role ?? "nil"),subrole=\((attribute(element, kAXSubroleAttribute) as String?) ?? "nil"),"
            + "enabled=\(traits.enabled.map(String.init) ?? "nil"),editable=\(traits.editable.map(String.init) ?? "nil"),"
            + "focused=\(focused),valueSettable=\(traits.valueSettable),"
            + "selectedTextSettable=\(traits.selectedTextSettable),children=\(childCount)"
    }

    private static func pointAttribute(_ element: AXUIElement, _ name: String) -> CGPoint? {
        guard let value: AXValue = attribute(element, name), AXValueGetType(value) == .cgPoint else { return nil }
        var point = CGPoint.zero
        return AXValueGetValue(value, .cgPoint, &point) ? point : nil
    }

    private static func sizeAttribute(_ element: AXUIElement, _ name: String) -> CGSize? {
        guard let value: AXValue = attribute(element, name), AXValueGetType(value) == .cgSize else { return nil }
        var size = CGSize.zero
        return AXValueGetValue(value, .cgSize, &size) ? size : nil
    }
}

enum PlatformNotification {
    static func requestAuthorization() {
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { _, _ in }
    }

    static func show(title: String, body: String) {
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        let request = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
        UNUserNotificationCenter.current().add(request)
    }
}

enum LoginItemService {
    static var enabled: Bool { SMAppService.mainApp.status == .enabled }

    static func setEnabled(_ enabled: Bool) throws {
        if enabled { try SMAppService.mainApp.register() }
        else { try SMAppService.mainApp.unregister() }
    }
}
