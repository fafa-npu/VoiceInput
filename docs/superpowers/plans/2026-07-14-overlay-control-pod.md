# VoiceInput Control Pod Overlay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the existing generic capsule with the approved compact control-pod overlay without changing dictation behavior.

**Architecture:** Keep the existing `OverlayWindow` lifecycle, width animation, text fitting, and monitor placement. Change only its visual tree and the waveform brush, with one focused layout test guarding the named elements and stable dimensions.

**Tech Stack:** .NET 10, WPF XAML, xUnit

---

### Task 1: Guard The Approved Layout

**Files:**
- Create: `tests/VoiceInput.Tests/OverlayWindowLayoutTests.cs`

- [ ] Add a test that parses `OverlayWindow.xaml` and asserts the `WaveModule`, `InformationPod`, `PhaseLabel`, `Label`, and `LiveIndicator` elements exist.
- [ ] Assert the host remains transparent, topmost, non-activating, and fixed at `820x150`.
- [ ] Run `dotnet test tests/VoiceInput.Tests/VoiceInput.Tests.csproj --filter FullyQualifiedName~OverlayWindowLayoutTests --no-restore` and verify the new test fails because the control-pod elements do not exist.

### Task 2: Implement The Control Pod

**Files:**
- Modify: `src/VoiceInput/Views/OverlayWindow.xaml`
- Modify: `src/VoiceInput/Views/OverlayWindow.xaml.cs`
- Modify: `src/VoiceInput/Controls/WaveformControl.cs`

- [ ] Replace the capsule content with an attached 64 px yellow-green `WaveModule` and 54 px graphite `InformationPod`.
- [ ] Add `PhaseLabel` and `LiveIndicator`, using `LIVE INPUT` while listening and `PROCESSING` for status text.
- [ ] Update width geometry so the existing animated outer width still fits the attached modules and transcript tail.
- [ ] Change the waveform brush to graphite so it remains legible on the yellow-green module.
- [ ] Run the focused test and verify it passes.

### Task 3: Verify And Install

**Files:**
- No source changes expected.

- [ ] Run `dotnet test tests/VoiceInput.Tests/VoiceInput.Tests.csproj --no-restore` and verify all regular tests pass.
- [ ] Run `dotnet build src/VoiceInput/VoiceInput.csproj -c Release --no-restore` and verify zero warnings and errors.
- [ ] Publish and run `scripts/install.ps1 -Source publish/VoiceInput.exe -AllowUnsignedDevelopmentBuild`.
- [ ] Verify the published and installed executable SHA-256 hashes match and the installed process is running.
- [ ] Visually inspect listening and processing overlay states.

No commit or push is included because the user has not approved git write operations.
