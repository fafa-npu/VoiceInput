# VoiceInput Control Pod Overlay Design

## Goal

Replace the generic dark pill overlay with a compact, distinctive control-pod treatment while preserving all current dictation behavior.

## Visual Direction

- Keep the overlay bottom-center, transparent, topmost, non-activating, and click-through.
- Use a 64 px fluorescent yellow-green waveform module attached to a 54 px graphite information pod.
- Show a small uppercase phase label above the transcript or status text.
- Show a compact live indicator at the right edge while the overlay is visible.
- Keep corners at 18 px or less for a precise industrial shape rather than a soft pill.
- Use graphite, off-white, muted gray, and one yellow-green accent. Do not add gradients, decorative blobs, or extra controls.

## Behavior

- Preserve the existing entrance, exit, width, monitor-positioning, focus, and click-through behavior.
- Preserve transcript tail fitting and animated width changes.
- Listening displays `LIVE INPUT`; processing statuses display `PROCESSING`.
- Placeholder text remains subdued; recognized text remains full contrast.
- The waveform continues to reflect the existing live RMS level.

## Files

- `src/VoiceInput/Views/OverlayWindow.xaml`: replace the single capsule layout with the attached waveform module and information pod.
- `src/VoiceInput/Views/OverlayWindow.xaml.cs`: update width geometry and switch the phase label when status changes.
- `src/VoiceInput/Controls/WaveformControl.cs`: change the frozen waveform brush to the control-pod foreground color.
- `tests/VoiceInput.Tests/OverlayWindowLayoutTests.cs`: add focused XAML/layout assertions for the control-pod structure and stable dimensions.

## Verification

- Focused overlay layout test passes.
- Full test suite passes.
- Release build completes with zero warnings and errors.
- Installed overlay is visually checked in listening and processing states on the local desktop.

## Out Of Scope

- No new user settings, themes, dependencies, icons, audio behavior, or Setup UI changes.
