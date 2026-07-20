> **Install:** Give Copilot this one-line request: `Install gujiguji on this Windows PC from https://github.com/fafa-npu/VoiceInput. Preserve my existing settings and downloaded models, use the repository's supported installation flow, resolve PowerShell execution-policy and startup-shortcut permission issues, verify any downloaded release against its GitHub SHA-256 digest (or build from source), launch the app, and ask me only when Windows requires a security confirmation.`

# gujiguji (Windows + macOS)

A native tray/menu-bar voice input method. Hold a key while speaking, or press once to start and
again to stop. gujiguji transcribes the recording and inserts it only if the original window and
input control still have focus.

The existing Windows client remains **C# / .NET 10 + WPF**. The macOS client is a separate native
**Swift + AppKit** target with the same profiles, settings, onboarding, overlay, engines, recovery,
and privacy behavior.

## Install on Windows

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

On first launch, the Windows guide offers to download **Qwen3-ASR 0.6B int8** (about **987 MB**).
The download is resumable and does not require the separate FunASR runtime or VAD. Windows
dictation remains available as a lower-accuracy fallback.

Uninstall:

```powershell
powershell -File "$env:LOCALAPPDATA\VoiceInput\uninstall.ps1" -Uninstall
```

This also removes `%APPDATA%\VoiceInput` (settings, logs, and encrypted correction samples). Add
`-KeepUserData` to retain it.

## Build and run on macOS

The first macOS build targets **macOS 15+ on Apple Silicon**. It uses the official universal Azure
Speech SDK; the pinned local FunASR runtime is currently arm64-only.

```bash
swift test --package-path src/VoiceInputMac
scripts/build-macos.sh
open dist/gujiguji.app
```

On first launch, allow Microphone, Accessibility, and Input Monitoring. The two-page guide then
downloads the resumable, SHA-256-verified Qwen3-ASR 0.6B model (about **850 MB**) or lets you
explicitly choose macOS Speech. Settings and models live under
`~/Library/Application Support/gujiguji`; secrets are stored in Keychain and logs under
`~/Library/Logs/gujiguji`.

For a signed release, configure a Developer ID certificate and notarytool profile, then run:

```bash
SIGN_IDENTITY="Developer ID Application: …" NOTARY_PROFILE=gujiguji scripts/release-macos.sh
```

## What makes it different

gujiguji defaults new Windows and macOS users to **Qwen3-ASR 0.6B**. An
optional **speech-aware LLM refinement layer** can be configured for:

- **Sound-error correction.** The built-in refine prompt knows the input was _spoken_, so it fixes the
  errors speech recognition actually makes — Chinese homophones / near-homophones, and tech terms
  misheard into phonetics (配森 → Python, 杰森 → JSON, 瑞克特 → React) — instead of treating them
  as typos. It also adds punctuation, strips filler words (嗯 / 呃 / um / uh / you know), and
  **never translates** or rewrites correct text.
- **Context-aware refinement.** Opt in and it reads the text around your cursor — including your
  **terminal buffer** — via UI Automation, and feeds it to the LLM as context. Say a branch name
  it mishears and it corrects to the real one on screen (`瑞法克特` → `query-inspector-refactor`).
- **Fully customizable prompt.** A custom prompt can intentionally translate or rewrite the
  transcript. Leave it blank for the conservative speech-aware default.
- **Bring your own model.** Any OpenAI-compatible endpoint (Base URL / Key / Model).

## Features

- **Two input profiles.** **Desktop** defaults to Right Ctrl, hold to talk, and a bottom overlay;
  **Mobile** defaults to Left Ctrl, press to start/stop, and a top overlay. Both names, keys,
  activation behaviors, and overlay positions are configurable. `Alt+Shift+G` switches profiles
  while idle, and the tray menu provides direct selection. The last active profile is restored.
- **Two activation behaviors.** Hold the active key and release to transcribe, or press once to
  listen and again to transcribe. Both are chord-aware, so shortcuts such as Right-Ctrl+C still
  work; a watchdog recovers a missed key-up after UAC, lock screen, or another hook interruption.
  macOS additionally offers **Fn / Globe** and Right Option as configurable keys.
- **Guided first run.** The setup window recommends Qwen3-ASR 0.6B on both platforms, shows
  package and byte progress, then teaches the real focused-text-box workflow. Users
  can explicitly fall back to the platform built-in speech engine after an accuracy warning.
- **Default Simplified Chinese (zh-CN)**, switchable to English / 繁體中文 / 日本語 / 한국어 / Tiếng Việt.
- **Four speech engines:** app-managed local **FunASR and Qwen3-ASR** models (both platforms default
  to Qwen3-ASR 0.6B; local recognition is batch), the platform built-in
  engine (**Windows dictation** or **macOS Speech**),
  **Azure Speech** (streaming), and **gpt-4o-transcribe** via **Azure AI Foundry** (batch). Azure
  Speech and gpt-4o-transcribe each support **account-key** or **Microsoft Entra ID** auth.
- **No clipped starts.** The mic is brought live before you're cued to speak and kept warm for a
  minute between dictations, so back-to-back dictation is instant and the first words aren't lost to
  device cold-start. The mic is fully released when idle or paused.
- **Capsule overlay** at the configured top or bottom of the active monitor (multi-monitor aware)
  with a live, RMS-driven waveform and the running transcript; grows smoothly and shows the latest
  words. Switching profiles briefly displays the newly active profile.
- **Target-safe injection.** gujiguji records the original process, window, and best available focus
  anchor, refuses to type after a known focus change, and delivers text through OS keyboard input
  events so native, Chromium, and Electron editors receive their normal input notifications. macOS
  verifies the final value through AX when the target exposes it and labels opaque editor delivery as
  dispatched rather than falsely confirmed. Failed confirmed delivery is preserved, copied to the
  clipboard, and available for retry from the tray/menu bar. Windows integrity levels and macOS
  Secure Input can still block injection.
- **Single instance**, **tray/menu-bar only**, custom mic icon. API keys use DPAPI on Windows and
  Keychain on macOS; the log never records transcript text unless you enable diagnostic logging.

## Controls

| Action                   | How                                                                                  |
| ------------------------ | ------------------------------------------------------------------------------------ |
| **Talk**                 | Use the activation key and behavior configured for the active Profile                |
| **Switch profile**       | **Alt+Shift+G** (Windows) / **Option+Shift+G** (macOS), or use the tray/menu bar       |
| **Start**                | Start Menu → **gujiguji**, or it auto-starts at login                               |
| **Quit**                 | Tray icon → **Quit**                                                                 |
| **Pause / resume**       | Tray → **Pause / Resume listening**                                                  |
| **Context-aware refine** | Settings → Language intelligence (off by default)                                    |
| **Setup**                | Tray → **Settings…**                                                                 |

## Local models

On first launch, Windows and macOS select **Qwen3-ASR 0.6B**. After setup, open
**Settings → Model Selection** to install, switch, or remove local models on demand.
Qwen3-ASR 1.7B is optional on both platforms and is never downloaded automatically.

| Model | Platform and download | Languages | Intended use |
| --- | --- | --- | --- |
| [SenseVoiceSmall q8](https://huggingface.co/FunAudioLLM/SenseVoiceSmall-GGUF) | Windows/macOS · 254 MB | English, Chinese, Japanese, Korean | Balanced CPU model |
| [Paraformer q8](https://huggingface.co/FunAudioLLM/Paraformer-GGUF) | Windows/macOS · 237 MB | English, Chinese | Faster Chinese/English dictation |
| [Fun-ASR Nano q4](https://huggingface.co/FunAudioLLM/Fun-ASR-Nano-GGUF) | Windows/macOS · 954 MB | English, Chinese, Japanese | Difficult vocabulary and accents |
| [Qwen3-ASR 0.6B](https://github.com/QwenLM/Qwen3-ASR) | Windows int8 ONNX · 987 MB; macOS Q8 GGUF · 850 MB | English, Chinese, Japanese, Korean, Vietnamese | Default on both platforms; higher-quality multilingual recognition and automatic language detection |
| [Qwen3-ASR 1.7B](https://huggingface.co/Qwen/Qwen3-ASR-1.7B) | Windows int8 ONNX · 2.40 GB; macOS Q5_K_M GGUF · 1.52 GB | English, Chinese, Japanese, Korean, Vietnamese | Optional accuracy-first model; larger memory footprint and slower CPU inference |

The three FunASR models share the official FunASR llama.cpp runtime (Windows x64 or macOS arm64)
and an FSMN-VAD model. Qwen3-ASR uses a separate backend and does not download that runtime or VAD.
All model downloads are resumable and SHA-256 verified before activation, and models can be removed
from Setup without touching other app settings.

FunASR runs as a hidden native child process; its temporary local WAV is deleted after each batch.
Qwen3-ASR stays loaded in-process between dictations: Windows uses sherpa-onnx 1.13.4 on CPU and
macOS uses transcribe.cpp 0.1.3 with Metal/CPU. Both Qwen backends use automatic language detection.
Windows accepts a bounded vocabulary prompt (first 10 terms, up to 96 characters) and limits each
Qwen dictation to 25 seconds so the fixed ONNX context cannot silently truncate it. The macOS
transcribe.cpp backend does not currently expose native hotword prompting. The 1.7B model is best
suited to Apple silicon or a Windows machine with ample RAM; the 0.6B default is substantially more
practical in CPU-only virtual machines.

No local backend requires Python, PyTorch, Docker, a local HTTP server, or a listening port. The
three FunASR GGUF models do not support Vietnamese; Qwen3-ASR does. Model weights and runtimes use
the licenses linked from their source cards; the pinned Qwen and sherpa artifacts are Apache 2.0.

## Configuration

Tray → **Settings…** opens the Setup Hub. **Model Selection** contains speech engines, cloud
authentication, and local-model downloads; **Profiles** configures the two activation and overlay
presets; **Language intelligence** combines recognition terms, the OpenAI-compatible model
connection, live refinement, and reviewed learning from locally encrypted corrections; **App**
contains language, privacy, startup, update, and logging controls. Saving corrections makes no
network request. **Review learning** sends them only to the configured language-model endpoint and
never clears the history automatically.
Secret fields are DPAPI-encrypted per Windows user or stored in macOS Keychain.

On macOS, Azure Speech with Microsoft Entra ID also requires the Speech resource's **Region** and
full **Azure Resource ID** (for example `/subscriptions/.../resourceGroups/.../providers/Microsoft.CognitiveServices/accounts/...`).
The native Speech SDK uses these to construct Microsoft's required `aad#resource-id#token`
authorization token; the custom-domain endpoint and tenant remain configured as on Windows.

## Build (developers)

Windows needs the **.NET 10 SDK**; macOS needs Xcode/Swift 6.

```bash
make run        # run from source
make install    # build + install to %LOCALAPPDATA% + auto-start + launch
make release VERSION=vX.Y.Z SIGN_PFX=publisher.pfx  # prompts securely for the PFX password

dotnet test tests/VoiceInput.Tests/VoiceInput.Tests.csproj
dotnet build src/VoiceInput/VoiceInput.csproj -p:EnableWindowsTargeting=true

swift test --package-path src/VoiceInputMac
scripts/build-macos.sh
```

The app shows its version in Settings and offers **Update to vX.Y.Z…** when a newer release exists.
Updates are user-initiated, verified against a pinned Authenticode publisher when signed or the
GitHub Release SHA-256 digest otherwise, atomically replaced, and rolled back if the new process
does not stay running.

## Notes

- **Windows dictation** generally has lower recognition accuracy than the local models, especially for
  Chinese, accents, and technical vocabulary. It may also need _Online speech recognition_ and the
  matching Windows speech pack. Prefer a local model for offline use or Azure Speech for streaming.
- **gpt-4o-transcribe** is batch: it transcribes after you release (~0.5–2 s), so there are no live
  partials — but accuracy is highest (zh-CN homophones, tech terms). Needs an Azure AI Foundry
  resource with a `gpt-4o-transcribe` deployment (e.g. in eastus2 / swedencentral).
- **Local recognition** is batch. Startup and recognition time depend on the selected model,
  recording length, and local hardware; no cloud fallback occurs after a local error. Qwen uses
  Metal when available on macOS and CPU on Windows.
- Context reading works for Windows Terminal, most input boxes, and Copilot/Teams; it can't read
  VS Code's editor (Monaco) — there it just falls back to plain refinement.
- UI Automation context is untrusted input. gujiguji constrains refined output length, rejects
  control characters and large semantic drift, and falls back to the original transcript.
- Edit learning is off by default, bound to the original control for two minutes, capped at 100
  samples, and encrypted per Windows user with DPAPI.
- The overlay is a custom translucent capsule, not OS acrylic (WPF can't have both transparency and
  a DWM backdrop).
