# VoiceInput (Windows)

A system-tray, hold-to-talk voice input method for Windows — the Windows port of the macOS
menu-bar dictation app. Hold a key, speak, release, and the transcribed text is typed into
whatever input field has focus.

Built with **C# / .NET 10 + WPF**, targeting **Windows 10 1903+ / Windows 11**.

## What makes it different

Most dictation tools just dump raw speech-to-text. VoiceInput adds a **speech-aware LLM refinement
layer** on top:

- **Sound-error correction.** The refine prompt knows the input was _spoken_, so it fixes the
  errors speech recognition actually makes — Chinese homophones / near-homophones, and tech terms
  misheard into phonetics (配森 → Python, 杰森 → JSON, 瑞克特 → React) — instead of treating them
  as typos. It also adds punctuation, strips filler words (嗯 / 呃 / um / uh / you know), and
  **never translates** or rewrites correct text.
- **Context-aware refinement.** Opt in and it reads the text around your cursor — including your
  **terminal buffer** — via UI Automation, and feeds it to the LLM as context. Say a branch name
  it mishears and it corrects to the real one on screen (`瑞法克特` → `query-inspector-refactor`).
- **Fully customizable prompt.** The refine system prompt is yours to change — set `LlmPrompt` in
  `settings.json` to tune the behavior, or leave it blank for the built-in speech-aware default.
- **Bring your own model.** Any OpenAI-compatible endpoint (Base URL / Key / Model).

## Features

- **Hold-to-talk.** Hold the push-to-talk key (default **Right Ctrl**, rebindable) to record;
  release to transcribe and inject. Chord-aware (Right-Ctrl+C still works); a watchdog recovers a
  missed key-up (UAC / lock screen).
  > macOS uses **Fn**; on Windows Fn is firmware-handled and invisible to software, so a standard
  > key is used.
- **Default Simplified Chinese (zh-CN)**, switchable to English / 繁體中文 / 日本語 / 한국어 / Tiếng Việt.
- **Three speech engines:** **Windows on-device** (no key, offline), **Azure Speech** (streaming,
  low-latency zh-CN), and **gpt-4o-transcribe** via **Azure AI Foundry** (batch — highest accuracy,
  transcribes on release). Azure Speech and gpt-4o-transcribe each support **account-key** or
  **Microsoft Entra ID** auth (interactive sign-in, cached so you sign in once).
- **No clipped starts.** The mic is brought live before you're cued to speak and kept warm for a
  minute between dictations, so back-to-back dictation is instant and the first words aren't lost to
  device cold-start. The mic is fully released when idle or paused.
- **Capsule overlay** at the bottom of the active monitor (multi-monitor aware) with a live,
  RMS-driven waveform and the running transcript; grows smoothly and shows the latest words.
- **Reliable injection** by typing the characters directly (SendInput Unicode), so it lands in
  Chromium/Electron apps too (Microsoft 365 Copilot, Teams) — no clipboard or IME side-effects.
- **Single instance**, **tray-only**, custom mic icon. API keys are DPAPI-encrypted at rest; the
  log never records transcript text unless you turn on diagnostic logging.

## Controls

| Action                   | How                                                                                  |
| ------------------------ | ------------------------------------------------------------------------------------ |
| **Talk**                 | Hold **Right Ctrl** (rebindable: tray → Push-to-talk key), speak, release            |
| **Start**                | Start Menu → **VoiceInput**, or it auto-starts at login                              |
| **Quit**                 | Tray icon → **Quit**                                                                 |
| **Pause / resume**       | Tray → **Pause / Resume listening**                                                  |
| **Context-aware refine** | Tray → **Use surrounding context (UIA)** (off by default; sends app text to the LLM) |
| **Settings**             | Tray → **Settings…** (engine / Azure / LLM)                                          |

## Install

**One line, no clone** (downloads the latest release exe, installs to `%LOCALAPPDATA%`, adds Start
Menu + auto-start, and launches) — in PowerShell:

```powershell
$s="$env:TEMP\vi.ps1"; iwr https://github.com/fafa-npu/VoiceInput/raw/main/scripts/install.ps1 -OutFile $s; powershell -ExecutionPolicy Bypass -File $s
```

Or just download `VoiceInput.exe` from the
[Releases](https://github.com/fafa-npu/VoiceInput/releases) page and double-click it
(it's self-contained — no .NET runtime needed).

Uninstall: `scripts\install.ps1 -Uninstall`.

## Configuration

Tray → **Settings…** for engine + auth and LLM. Each cloud engine offers **Key** or **Microsoft
Entra ID** auth: Azure Speech needs key+region (Key) or endpoint+tenant (Entra); gpt-4o-transcribe
needs the Foundry endpoint + deployment, with an API key (Key) or tenant (Entra). LLM refinement
takes any OpenAI-compatible Base URL / Key / Model (default `gpt-4.1-mini`). To customize the refine
prompt, set `LlmPrompt` in `%APPDATA%\VoiceInput\settings.json` (secret fields are DPAPI-encrypted
per-user).

## Build (developers)

Needs the **.NET 10 SDK**.

```bash
make run        # run from source
make install    # build + install to %LOCALAPPDATA% + auto-start + launch
make release VERSION=vX.Y.Z   # build the versioned exe + publish a release (scripts/release.ps1)
```

The app shows its version in the tray and offers **Update to vX.Y.Z…** when a newer release exists
— downloaded and applied only when you choose it (never automatic).

## Notes

- **Windows on-device dictation** is web-service-backed: it may need _Online speech recognition_ on
  and the zh-CN speech pack installed. For reliable zh-CN, use Azure Speech.
- **gpt-4o-transcribe** is batch: it transcribes after you release (~0.5–2 s), so there are no live
  partials — but accuracy is highest (zh-CN homophones, tech terms). Needs an Azure AI Foundry
  resource with a `gpt-4o-transcribe` deployment (e.g. in eastus2 / swedencentral).
- Context reading works for Windows Terminal, most input boxes, and Copilot/Teams; it can't read
  VS Code's editor (Monaco) — there it just falls back to plain refinement.
- The overlay is a custom translucent capsule, not OS acrylic (WPF can't have both transparency and
  a DWM backdrop).
