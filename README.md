# VoiceInput (Windows)

A system-tray, hold-to-talk voice input method for Windows — the Windows port of the macOS
menu-bar dictation app. Hold a key, speak, release, and the transcribed text is injected into
whatever input field has focus.

Built with **C# / .NET 10 + WPF**, targeting **Windows 10 1903+ / Windows 11**.

## Features

- **Hold-to-talk.** Hold the push-to-talk key (default **Right Ctrl**, rebindable in the tray
  menu) to record; release to transcribe and inject. Chord-aware: pressing another key while the
  PTT key is held (e.g. Right-Ctrl+C) cancels dictation and lets the shortcut through. A watchdog
  recovers if a key-up is ever missed (UAC / lock screen).
  > The macOS version uses **Fn**; on Windows the Fn key is handled in keyboard firmware and is
  > not visible to software, so a standard key is used instead.
- **Default language Simplified Chinese (zh-CN)**, switchable to English / 繁體中文 / 日本語 /
  한국어 from the tray.
- **Streaming recognition** via the **Azure Speech SDK** (best zh-CN quality, needs a key/region)
  with **Windows on-device dictation** as the no-key fallback. Switch engines from the tray.
- **Elegant capsule overlay** at the bottom-center with a live, RMS-driven 5-bar waveform and the
  running transcript. It **follows the monitor you're working on** (multi-monitor aware), grows
  smoothly with the text, and shows the **latest words** of a long transcript (tail) instead of
  clipping them.
- **IME-aware injection.** Pastes via clipboard + Ctrl+V, temporarily closing the target window's
  IME so a CJK input method can't intercept the paste, then restoring IME state and clipboard.
- **Optional LLM refinement** via any OpenAI-compatible API: fixes obvious recognition errors
  (e.g. 配森 → Python, 杰森 → JSON), **adds punctuation**, and **removes filler words / hesitations**
  (嗯 / 呃 / 那个 / um / uh / you know …) — while **never translating** or rewriting meaningful text.
- **Single instance**, **tray-only** (no taskbar window), custom microphone tray icon. API keys are
  encrypted at rest with Windows **DPAPI**; the diagnostic log never records transcript text by
  default.

## Controls

| Action                      | How                                                                                    |
| --------------------------- | -------------------------------------------------------------------------------------- |
| **Talk**                    | Hold **Right Ctrl** (rebindable: tray → Push-to-talk key), speak, release              |
| **Start the app**           | Start Menu → search **VoiceInput**, or it auto-starts at login                         |
| **Quit**                    | Right-click the tray icon (blue mic, bottom-right) → **Quit**                          |
| **Pause / resume**          | Tray → **Pause listening** / **Resume listening** (stays running, ignores the PTT key) |
| **Auto-start at login**     | Tray → **Start at login** (toggle on/off)                                              |
| **Language / engine / LLM** | Tray submenus + **Settings…**                                                          |
| **Check for updates**       | Tray → **Check for updates…**                                                          |

Launching a second copy while one is running does nothing — the second instance exits silently.

## Install (one-click)

**From a cloned repo** (needs the .NET 10 SDK — builds, installs to `%LOCALAPPDATA%`, adds Start
Menu + auto-start shortcuts, and launches):

```powershell
git clone https://microsoft.ghe.com/Zhao-Hua/VoiceInput.git
cd VoiceInput
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
```

**From a prebuilt release** (no .NET SDK needed) — download `VoiceInput.exe` from the
[Releases](https://microsoft.ghe.com/Zhao-Hua/VoiceInput/releases) page, then either just
double-click it to run, or install with auto-start + Start Menu entry:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1 -Source .\VoiceInput.exe
```

Uninstall any time (stops it, removes the install + both shortcuts): `scripts\install.ps1 -Uninstall`.

## Configuration

Tray → **Settings…**:

- **Speech Engine** — Windows (on-device) or Azure Speech (Key + Region).
- **LLM refinement** — enable + API Base URL / API Key / Model (default `gpt-4.1-mini`).
  Test and Save buttons.

`%APPDATA%\VoiceInput\settings.json` — secret fields (Azure key, LLM key) are DPAPI-encrypted
(per-user); only the current Windows user can decrypt them.

## Build & run (developers)

Requires the **.NET 10 SDK** and the **Windows Desktop** runtime.

```bash
make build      # compile
make run        # run from source (Debug)
make publish    # self-contained single-file exe -> publish/VoiceInput.exe (~80 MB; WPF can't be trimmed)
make install    # publish + install to %LOCALAPPDATA% + Start Menu + auto-start + launch
make release VERSION=vX.Y.Z   # build the versioned exe + publish a GHE release (scripts/release.ps1)
```

Or directly: `dotnet run --project src/VoiceInput/VoiceInput.csproj`.
The exe is self-contained, so end users do **not** need the .NET runtime installed.

## Updates

The app shows its version in the tray, checks the latest GHE release at startup (and via
**Check for updates…**), and offers **Update to vX.Y.Z…** when a newer one exists — it downloads
the new exe and restarts, **only when the user chooses it** (never automatic).

Releasing is REST-based (`scripts/release.ps1`) because older `gh` mishandles this GHE instance's
asset upload:

```bash
make release VERSION=v0.1.3      # bump src/VoiceInput <Version> to match first
```

This bakes the version into the exe, creates the release, and uploads `VoiceInput.exe`. In-app
update check/download use the GHE REST API with the token from `gh auth`, so users need
`gh auth login --hostname microsoft.ghe.com` once for in-app updates (downloading from the
Releases page in a browser needs no setup).

## Notes / limitations

- **Windows on-device dictation** uses Windows' web-service-backed dictation grammar: it may need
  the **Online speech recognition** privacy setting on (Settings → Privacy → Speech) and the
  **zh-CN speech language pack** installed. If unavailable the app notifies and opens Speech
  settings. For the most reliable zh-CN, use Azure Speech.
- The overlay uses a custom translucent capsule (exact pill shape) rather than true OS
  acrylic/Mica — in WPF, `AllowsTransparency` and DWM backdrops are mutually exclusive.
- Clipboard restore preserves **plain text** only (images/files are not restored).
- Tight intra-sentence Chinese+English code-switching is limited by the recognition engines; the
  LLM refinement step helps correct the residue.
