> **Install:** Give Copilot this one-line request: `Install gujiguji on this Windows PC from https://github.com/fafa-npu/VoiceInput. Preserve my existing settings and downloaded models, use the repository's supported installation flow, resolve PowerShell execution-policy and startup-shortcut permission issues, verify any downloaded release against its GitHub SHA-256 digest (or build from source), launch the app, and ask me only when Windows requires a security confirmation.`

# gujiguji (Windows)

A system-tray voice input method for Windows. Hold a key while speaking, or press once to start
and again to stop. gujiguji transcribes the recording and inserts it only if the original window
and input control still have focus.

Built with **C# / .NET 10 + WPF**, targeting **Windows 10 1903+ / Windows 11**.

## Install

The recommended installation is to give Copilot the first-line request above. Copilot can inspect
the repository, choose the supported release or source-build path, handle PowerShell and shortcut
permissions, preserve an existing installation, and stop only for Windows security confirmations.

Manual one-line install in PowerShell:

```powershell
$s="$env:TEMP\vi.ps1"; iwr https://github.com/fafa-npu/VoiceInput/raw/main/scripts/install.ps1 -OutFile $s; powershell -ExecutionPolicy Bypass -File $s
```

The installer verifies the official GitHub Release asset against the SHA-256 digest returned by
GitHub before installing it to `%LOCALAPPDATA%\VoiceInput`, creating Start Menu and auto-start
shortcuts, and launching it. Reinstalling preserves `%APPDATA%\VoiceInput` settings and downloaded
models under `%LOCALAPPDATA%\VoiceInput\FunASR`. Current release executables are not Authenticode
signed, so Windows may still show a security confirmation.

You can also download `VoiceInput.exe` from
[Releases](https://github.com/fafa-npu/VoiceInput/releases) and run it directly. It is self-contained;
the .NET runtime is not required.

On first launch, the guide offers to download SenseVoiceSmall, the CPU runtime, and VAD (about
**260.8 MB** total). The download is resumable. Windows dictation remains available as a
lower-accuracy fallback.

Uninstall:

```powershell
powershell -File "$env:LOCALAPPDATA\VoiceInput\uninstall.ps1" -Uninstall
```

This also removes `%APPDATA%\VoiceInput` (settings, logs, and encrypted correction samples). Add
`-KeepUserData` to retain it.

## What makes it different

gujiguji defaults new users to the app-managed **FunASR SenseVoiceSmall** local model. An
optional **speech-aware LLM refinement layer** can be configured for:

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

- **Two input profiles.** **Desktop** defaults to Right Ctrl, hold to talk, and a bottom overlay;
  **Mobile** defaults to Left Ctrl, press to start/stop, and a top overlay. Both names, keys,
  activation behaviors, and overlay positions are configurable. `Alt+Shift+G` switches profiles
  while idle, and the tray menu provides direct selection. The last active profile is restored.
- **Two activation behaviors.** Hold the active key and release to transcribe, or press once to
  listen and again to transcribe. Both are chord-aware, so shortcuts such as Right-Ctrl+C still
  work; a watchdog recovers a missed key-up after UAC, lock screen, or another hook interruption.
  > macOS uses **Fn**; on Windows Fn is firmware-handled and invisible to software, so a standard
  > key is used.
- **Guided first run.** The setup window recommends and downloads SenseVoiceSmall with visible
  package and byte progress, then teaches the real focused-text-box workflow. Users can explicitly
  fall back to Windows dictation after an accuracy warning.
- **Default Simplified Chinese (zh-CN)**, switchable to English / 繁體中文 / 日本語 / 한국어 / Tiếng Việt.
- **Four speech engines:** **FunASR** with app-managed local GGUF models (the new-user default,
  batch), **Windows dictation** (lower accuracy; may use Microsoft's online speech service),
  **Azure Speech** (streaming), and **gpt-4o-transcribe** via **Azure AI Foundry** (batch). Azure
  Speech and gpt-4o-transcribe each support **account-key** or **Microsoft Entra ID** auth.
- **No clipped starts.** The mic is brought live before you're cued to speak and kept warm for a
  minute between dictations, so back-to-back dictation is instant and the first words aren't lost to
  device cold-start. The mic is fully released when idle or paused.
- **Capsule overlay** at the configured top or bottom of the active monitor (multi-monitor aware)
  with a live, RMS-driven waveform and the running transcript; grows smoothly and shows the latest
  words. Switching profiles briefly displays the newly active profile.
- **Target-safe injection.** gujiguji records the original window, process, and focused control,
  refuses to type after focus changes, and checks every `SendInput` result. Uninserted text is
  preserved, copied to the clipboard, and available for retry from the tray. Windows security
  boundaries (for example an elevated app) can still block injection.
- **Single instance**, **tray-only**, custom mic icon. API keys are DPAPI-encrypted at rest; the
  log never records transcript text unless you turn on diagnostic logging.

## Controls

| Action                   | How                                                                                  |
| ------------------------ | ------------------------------------------------------------------------------------ |
| **Talk**                 | Use the activation key and behavior configured for the active Profile                |
| **Switch profile**       | Press **Alt+Shift+G**, or select a Profile from the tray                              |
| **Start**                | Start Menu → **gujiguji**, or it auto-starts at login                               |
| **Quit**                 | Tray icon → **Quit**                                                                 |
| **Pause / resume**       | Tray → **Pause / Resume listening**                                                  |
| **Context-aware refine** | Settings → App (off by default; sends app text only to your configured LLM)           |
| **Setup**                | Tray → **Settings…**                                                                 |

## Local FunASR

On first launch, the guide selects **SenseVoiceSmall** and offers to download it directly. After
setup, open **Settings → Model Selection** to install, switch, or remove local models on demand.

| Model | Download | Languages | Intended use |
| --- | ---: | --- | --- |
| [SenseVoiceSmall q8](https://huggingface.co/FunAudioLLM/SenseVoiceSmall-GGUF) | 254 MB | English, Chinese, Japanese, Korean | Default balanced CPU model |
| [Paraformer q8](https://huggingface.co/FunAudioLLM/Paraformer-GGUF) | 237 MB | English, Chinese | Faster Chinese/English dictation |
| [Fun-ASR Nano q4](https://huggingface.co/FunAudioLLM/Fun-ASR-Nano-GGUF) | 954 MB | English, Chinese, Japanese | Difficult vocabulary and accents |

The first model also downloads the official FunASR llama.cpp Windows x64 runtime (4.7 MB) and a
shared FSMN-VAD model (1.7 MB). Downloads are resumable and SHA-256 verified before activation.
The runtime and models live under `%LOCALAPPDATA%\VoiceInput\FunASR` and can be removed from Setup.

FunASR runs as a hidden native child process. It does not require Python, PyTorch, Docker, or a
local HTTP server, and it does not open a listening port. Recorded audio is written only to a
temporary local WAV for the native batch command and deleted when transcription finishes. Local
GGUF models in this release do not support Vietnamese; use one of the cloud engines for `vi-VN`.
Model weights use the licenses linked from their Hugging Face model cards; the pinned artifacts in
this release link to the [Apache 2.0 license](https://www.apache.org/licenses/LICENSE-2.0).

## Configuration

Tray → **Settings…** opens the Setup Hub. **Model Selection** contains speech engines, cloud
authentication, and local-model downloads; **Profiles** configures the two activation and overlay
presets; **Vocabulary** manages recognition terms; **Refinement** contains the OpenAI-compatible
Base URL, key, model, and custom prompt; **App** contains language, privacy, startup, update, and
logging controls.
Secret fields are DPAPI-encrypted per Windows user in `%APPDATA%\VoiceInput\settings.json`.

## Build (developers)

Needs the **.NET 10 SDK**.

```bash
make run        # run from source
make install    # build + install to %LOCALAPPDATA% + auto-start + launch
make release VERSION=vX.Y.Z SIGN_PFX=publisher.pfx  # prompts securely for the PFX password

dotnet test tests/VoiceInput.Tests/VoiceInput.Tests.csproj
dotnet build src/VoiceInput/VoiceInput.csproj -p:EnableWindowsTargeting=true
```

The app shows its version in Settings and offers **Update to vX.Y.Z…** when a newer release exists.
Updates are user-initiated, verified against a pinned Authenticode publisher when signed or the
GitHub Release SHA-256 digest otherwise, atomically replaced, and rolled back if the new process
does not stay running.

## Notes

- **Windows dictation** generally has lower recognition accuracy than FunASR, especially for
  Chinese, accents, and technical vocabulary. It may also need _Online speech recognition_ and the
  matching Windows speech pack. Prefer FunASR for local use or Azure Speech for streaming.
- **gpt-4o-transcribe** is batch: it transcribes after you release (~0.5–2 s), so there are no live
  partials — but accuracy is highest (zh-CN homophones, tech terms). Needs an Azure AI Foundry
  resource with a `gpt-4o-transcribe` deployment (e.g. in eastus2 / swedencentral).
- **FunASR** is also batch and CPU-only in this release. Startup and recognition time depend on the
  selected model, recording length, and the local CPU; no cloud fallback occurs after a local error.
- Context reading works for Windows Terminal, most input boxes, and Copilot/Teams; it can't read
  VS Code's editor (Monaco) — there it just falls back to plain refinement.
- UI Automation context is untrusted input. gujiguji constrains refined output length, rejects
  control characters and large semantic drift, and falls back to the original transcript.
- Edit learning is off by default, bound to the original control for two minutes, capped at 100
  samples, and encrypted per Windows user with DPAPI.
- The overlay is a custom translucent capsule, not OS acrylic (WPF can't have both transparency and
  a DWM backdrop).
