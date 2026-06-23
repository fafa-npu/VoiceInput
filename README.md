# VoiceInput (Windows)

A system-tray, hold-to-talk voice input method for Windows — the Windows port of the macOS
menu-bar dictation app. Hold a key, speak, release, and the transcribed text is injected into
whatever input field has focus.

Built with **C# / .NET 10 + WPF**, targeting **Windows 10 1903+ / Windows 11**.

## Features

- **Hold-to-talk.** Hold the push-to-talk key (default **Right Ctrl**, rebindable in the tray
  menu) to record; release to transcribe and inject. Chord-aware: pressing another key while the
  PTT key is held (e.g. Right-Ctrl+C) cancels dictation and lets the shortcut through.
  > The macOS version uses **Fn**; on Windows the Fn key is handled in keyboard firmware and is
  > not visible to software, so a standard key is used instead.
- **Default language Simplified Chinese (zh-CN)**, switchable to English / 繁體中文 / 日本語 /
  한국어 from the tray.
- **Streaming recognition** via **Azure Speech SDK** (best zh-CN quality, needs a key) with
  **Windows on-device dictation** as the no-key fallback.
- **Elegant capsule overlay** at the bottom-center of the screen with a live, RMS-driven 5-bar
  waveform and the running transcript.
- **IME-aware injection.** Pastes via clipboard + Ctrl+V, temporarily closing the target window's
  IME so a CJK input method can't intercept the paste, then restoring IME state and clipboard.
- **Optional LLM refinement.** Conservatively fixes obvious recognition errors (e.g. 配森 →
  Python, 杰森 → JSON) via any OpenAI-compatible API. Never rewrites or polishes correct text.
- **Tray-only**, no taskbar window. API keys are encrypted at rest with Windows DPAPI.

## Install (one-click)

**From a cloned repo** (needs the .NET 10 SDK — builds, installs to `%LOCALAPPDATA%`, enables
auto-start at login, and launches):

```powershell
git clone https://microsoft.ghe.com/Zhao-Hua/VoiceInput.git
cd VoiceInput
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
```

**From a prebuilt release** (no .NET SDK needed) — download `VoiceInput.exe` from the
[Releases](https://microsoft.ghe.com/Zhao-Hua/VoiceInput/releases) page, then either just
double-click it to run, or for auto-start at login:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1 -Source .\VoiceInput.exe
```

Uninstall any time: `scripts\install.ps1 -Uninstall`.

## Build & run (developers)

Requires the **.NET 10 SDK** and the **Windows Desktop** runtime.

```bash
make build      # compile
make run        # run from source (Debug)
make publish    # self-contained single-file exe -> publish/VoiceInput.exe (~80 MB; WPF can't be trimmed)
make install    # publish + install to %LOCALAPPDATA% + auto-start + launch
make release VERSION=v0.1.0   # publish + attach the exe to a GitHub Enterprise release
```

Or directly:

```bash
dotnet run --project src/VoiceInput/VoiceInput.csproj
```

The exe is self-contained, so end users do **not** need the .NET runtime installed.

### Signing

`make publish` Authenticode-signs the exe if you provide a certificate:

```bash
make publish SIGN_PFX=mycert.pfx SIGN_PWD=secret           # from a PFX
make publish SIGN_SUBJECT="My Company"                      # from the user cert store
```

Without a cert, publishing still succeeds (unsigned) and prints a note.

## Notes / first-version limitations

- **Windows on-device dictation** uses Windows' predefined dictation grammar, which is
  web-service-backed: it may require the **Online speech recognition** privacy setting to be on
  (Settings → Privacy → Speech) and the **zh-CN speech language pack** to be installed
  (Settings → Time & language → Language). If the language isn't available the app shows a
  notification and opens Speech settings. For the most reliable zh-CN, configure Azure Speech.
- The overlay uses a custom translucent capsule (exact pill shape) rather than true OS
  acrylic/Mica — in WPF, `AllowsTransparency` and DWM backdrops are mutually exclusive, so true
  blur is deferred.
- Clipboard restore preserves **plain text** only in this version (images/files on the clipboard
  are not restored).
- Per-utterance language identification for tight intra-sentence Chinese+English code-switching
  is limited by the recognition engines; the LLM refinement step helps correct the residue.

## Settings location

`%APPDATA%\VoiceInput\settings.json` — secret fields (Azure key, LLM key) are DPAPI-encrypted
(per-user). The file is safe to sit on disk; only the current Windows user can decrypt it.
