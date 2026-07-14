# Local FunASR Models and Setup Hub Design

## Status

Approved direction from the user on 2026-07-14. This document records the design before implementation. It is not a git commit authorization.

## Goals

- Add fully local speech recognition without requiring Python, PyTorch, Docker, or administrator access.
- Support multiple FunASR GGUF models with on-demand downloads.
- Default the FunASR model selection to SenseVoiceSmall.
- Replace the current scrolling settings form with the approved persistent Setup Hub.
- Preserve every existing speech engine, credential, LLM, tray, update, and input-safety behavior.
- Make downloads cancellable, resumable, integrity-checked, and recoverable after restart.

## Non-goals

- GPU runtimes, CUDA, Qwen3-ASR, GLM-ASR, or Whisper models.
- Streaming partial transcription from FunASR.
- User-supplied model URLs or arbitrary executables.
- Silent model updates or automatic model downloads.
- A background HTTP server or a network-accessible local endpoint.

## Supported Models

The first release exposes three official FunASR llama.cpp/GGUF models:

| ID | Display name | Artifacts | Published size | Intended use |
| --- | --- | --- | ---: | --- |
| `sensevoice-small-q8` | SenseVoiceSmall | `sensevoice-small-q8.gguf` | about 242 MB | Default balanced CPU model |
| `paraformer-zh-q8` | Paraformer Chinese | `paraformer-q8.gguf` | about 217 MB | Fast Chinese and English dictation |
| `fun-asr-nano-q4` | Fun-ASR Nano | `funasr-encoder-f16.gguf` and `qwen3-0.6b-q4km.gguf` | about 954 MB | Hard vocabulary and broader language coverage |

All models share `fsmn-vad.gguf` (about 1.7 MB). SenseVoiceSmall is the selected model before any model is installed. Existing users keep their current speech engine until they explicitly install and select FunASR.

The catalog records each model's stable ID, display name, description, supported VoiceInput language codes, download files, pinned source revisions, expected byte sizes, SHA-256 hashes, runtime executable, and command-line arguments. Catalog changes ship only with a VoiceInput update.

## Native Runtime

Use the official baseline Windows x64 asset from FunASR llama.cpp runtime `v0.1.5`:

- Asset: `funasr-llamacpp-windows-x64.zip`
- Published size: about 4.4 MB
- SHA-256 verified during the design spike: `2398192C1DD965A3D6C150833757A55047FA616A8B3561DD4D674259A913AFBD`

The archive contains the three required executables:

- `llama-funasr-sensevoice.exe`
- `llama-funasr-paraformer.exe`
- `llama-funasr-cli.exe`

The baseline x64 build is used for maximum compatibility. AVX2-specific runtime selection can be added later only if profiling shows that process startup or inference is a material bottleneck.

## Storage Layout

All managed files live under `%LOCALAPPDATA%\VoiceInput\FunASR`:

```text
FunASR/
  runtime/v0.1.5/
  models/sensevoice-small-q8/
  models/paraformer-zh-q8/
  models/fun-asr-nano-q4/
  shared/fsmn-vad.gguf
  downloads/*.part
  installation.json
```

`installation.json` is written atomically after successful verification. It records installed catalog IDs, artifact sizes and hashes, and runtime version. It contains no credentials or transcript data.

## Download and Installation Flow

1. The user clicks `Set up FunASR` or `Download and use` for a model.
2. The manager ensures the pinned native runtime and shared VAD are installed.
3. It downloads only the selected model's missing artifacts.
4. Downloads stream directly to `.part` files and report byte progress.
5. An interrupted download resumes with an HTTP range request when the source supports it.
6. Every completed artifact is checked against its pinned size and SHA-256 hash.
7. Verified files are atomically moved into their final versioned locations.
8. A generated one-second silent WAV smoke test runs through the selected executable.
9. Only after the smoke test passes is the model marked installed and eligible for selection.

Only one installation operation runs at a time. Closing the Setup Hub does not cancel an active download; an explicit Cancel command does. Exiting VoiceInput cancels safely and leaves resumable `.part` files.

Model removal is available only from the FunASR page, requires confirmation, and resolves the target under the managed FunASR root before deleting it. The active model cannot be removed until another speech engine or installed model is selected.

## Runtime State

The setup manager exposes these states to the UI:

- `NotInstalled`
- `Downloading`
- `Verifying`
- `Testing`
- `Installed`
- `Failed`

Progress includes the current stage, artifact name, downloaded bytes, total bytes when known, and a user-facing error. A failure never replaces a previously verified runtime or model.

## Transcription Flow

FunASR remains a batch engine, matching the existing gpt-4o-transcribe lifecycle:

1. Push-to-talk starts the existing 16 kHz, 16-bit, mono PCM capture.
2. PCM is buffered while the key is held.
3. Release wraps the buffer as a temporary WAV file.
4. The selected native executable runs hidden with separate stdout and stderr capture.
5. The executable receives the selected model, shared VAD, and WAV paths.
6. The final transcript is read from stdout and enters the existing refinement and target-safe injection pipeline.
7. The temporary WAV is removed in a `finally` block.

Commands are fixed by the built-in catalog rather than assembled from user input:

```text
llama-funasr-sensevoice.exe -m <model> -a <wav> --vad <vad>
llama-funasr-paraformer.exe -m <model> -a <wav> --vad <vad>
llama-funasr-cli.exe --enc <encoder> -m <decoder> -a <wav> --vad <vad>
```

The process is cancelled when dictation is cancelled and is killed after a bounded model-specific timeout. A local failure does not silently send audio to a cloud engine.

## Settings Model

Add `FunAsr` to `SpeechEngineKind` and persist a stable FunASR model ID with `sensevoice-small-q8` as its default. Existing settings files deserialize unchanged and preserve all cloud credentials.

Model-language compatibility is validated before saving:

- SenseVoiceSmall: `en-US`, `zh-CN`, `zh-TW`, `ja-JP`, and `ko-KR`.
- Paraformer: `en-US`, `zh-CN`, and `zh-TW`.
- Fun-ASR Nano q4: `en-US`, `zh-CN`, `zh-TW`, and `ja-JP`.

The selected GGUF artifacts do not provide a verified Vietnamese path, so `vi-VN` remains available through the existing cloud engines but is rejected for local FunASR in this release.

If the current language is unsupported, the UI explains the conflict and links to a compatible installed model. It does not silently change language or model.

## Setup Hub

The approved window is a roughly 900 by 650 WPF settings hub with a compact sidebar:

- `Overview`: readiness, active engine, FunASR setup status, refinement, push-to-talk, and language.
- `Speech`: all engine choices and the existing Azure Speech and Foundry authentication fields.
- `FunASR`: runtime status and one row per supported model with size, purpose, install state, progress, Download/Use/Remove actions, and source/license links.
- `Refinement`: LLM enablement, endpoint, key, model, prompt-related settings, and Test action.
- `App`: language, push-to-talk key, context, edit learning, diagnostic logging, start-at-login, update check, and log access.

The implementation stays in WPF and follows the existing code-behind pattern. It does not introduce a UI framework or a new MVVM layer. Navigation swaps bounded page grids. Existing tray items remain as quick actions.

The footer shows saved, unsaved, validation, or operation status. `Save changes` appears when the draft differs from persisted settings. Download state belongs to the app-level setup manager, not the draft, so Canceling settings does not undo an installed model.

## Error Handling

- Network interruption: preserve `.part`, show Retry, and resume later.
- Insufficient disk space: fail before download when content length is known; preserve existing installations.
- Hash or size mismatch: never activate the artifact; show integrity failure and Retry.
- Corrupt runtime archive: keep the current verified runtime and fail the staged installation.
- Smoke-test failure: retain logs, mark the model failed, and do not select it.
- Missing model at dictation time: do not record or fall back to cloud; direct the user to Setup.
- Process crash: report a local transcription failure without inserting partial output.
- Timeout or cancellation: terminate the child process and discard the transcript.

## Security and Privacy

- Download only pinned HTTPS publisher URLs from the built-in catalog.
- Verify SHA-256 before executing runtime files or loading model weights.
- Never bind a local port or expose an unauthenticated transcription API.
- Pass process arguments with `ProcessStartInfo.ArgumentList`, never shell interpolation.
- Keep runtime and model paths under the per-user managed root.
- Show model source and license links before download; model weights have licenses separate from the FunASR toolkit.
- Do not log audio or transcript text unless existing diagnostic transcript logging is enabled.

## Verification

Automated checks:

- Settings migration and round-trip tests for `FunAsr` and the selected model ID.
- Model catalog tests for unique IDs, supported languages, fixed executable mappings, expected hashes, and default SenseVoice selection.
- Download tests for progress, cancellation, range resume, hash mismatch, and atomic activation using a local HTTP test server.
- Runtime command tests for all three models, path quoting, cancellation, timeout, stdout parsing, and stderr-only diagnostics.
- Engine lifecycle tests for empty audio, cancel-before-stop, process failure, and successful final text.
- Existing unit tests remain green.

Manual checks:

- Build and publish the Windows x64 application.
- Install locally without disturbing existing `%APPDATA%\VoiceInput` settings.
- From a clean managed FunASR directory, download SenseVoiceSmall through the Setup Hub.
- Dictate the three existing Chinese benchmark samples and record latency and CER.
- Download and switch to Paraformer and Fun-ASR Nano on demand.
- Restart VoiceInput and confirm installed state and selected model persist.
- Exercise cancel/resume, offline retry, unsupported language, and model removal.
- Inspect the Setup Hub at 900x650 and its minimum supported size without clipped or overlapping controls.

## Acceptance Criteria

- A user can install and use default SenseVoiceSmall entirely from VoiceInput with no external prerequisites.
- Paraformer and Fun-ASR Nano download only when requested and can coexist with SenseVoiceSmall.
- Every downloaded executable and model artifact is integrity-checked before use.
- Local dictation never sends audio over the network.
- Existing Windows, Azure Speech, and Foundry transcription configurations continue to work.
- All existing settings are reachable from the Setup Hub and tray quick actions still work.
- The project builds and all automated tests pass.
