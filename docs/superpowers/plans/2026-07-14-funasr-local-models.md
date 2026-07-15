# FunASR Local Models Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an app-managed, no-Python FunASR engine with three on-demand GGUF models and replace the current settings form with the approved Setup Hub.

**Architecture:** A fixed model catalog pins official runtime/model URLs, sizes, revisions, and SHA-256 hashes. `FunAsrRuntimeManager` downloads and verifies artifacts under `%LOCALAPPDATA%\VoiceInput\FunASR`; `FunAsrEngine` invokes the matching native CLI for each completed batch recording and returns stdout through the existing refinement/injection flow. The WPF Setup Hub uses the existing code-behind style and observes the manager for progress.

**Tech Stack:** C# / .NET 10, WPF, `HttpClient`, `System.IO.Compression`, native FunASR llama.cpp v0.1.5 executables, xUnit.

**Git constraint:** Do not create a branch, commit, or push. The user requested a locally testable result and requires separate explicit approval for git operations.

---

## File Map

- Create `src/VoiceInput/Models/FunAsrModelCatalog.cs`: immutable model/runtime artifact metadata and language compatibility.
- Create `src/VoiceInput/Services/FunAsrRuntimeManager.cs`: status, resumable downloads, verification, extraction, smoke tests, and removal.
- Create `src/VoiceInput/Services/PcmWave.cs`: shared PCM-to-WAV encoding currently private in the cloud batch engine.
- Create `src/VoiceInput/Services/FunAsrEngine.cs`: buffered batch engine and native child-process execution.
- Modify `src/VoiceInput/Models/AppSettings.cs`: add `SpeechEngineKind.FunAsr` and selected model ID.
- Modify `src/VoiceInput/Services/SettingsStore.cs`: persist the selected local model without breaking old JSON.
- Modify `src/VoiceInput/Services/OpenAiTranscribeEngine.cs`: use `PcmWave` instead of its private WAV writer.
- Modify `src/VoiceInput/AppController.cs`: own the runtime manager, create the local engine, add tray selection, and pass setup actions to the window.
- Replace `src/VoiceInput/Views/SettingsWindow.xaml`: approved Setup Hub shell and five pages.
- Modify `src/VoiceInput/Views/SettingsWindow.xaml.cs`: page navigation, draft collection/validation, progress, model actions, and existing LLM test.
- Add focused tests under `tests/VoiceInput.Tests` for catalog, settings, downloads, WAV creation, and engine commands/lifecycle.
- Modify `README.md`: local-model behavior, sizes, privacy, and build/test notes.

### Task 1: Model Catalog and Persisted Selection

**Files:**
- Create: `src/VoiceInput/Models/FunAsrModelCatalog.cs`
- Modify: `src/VoiceInput/Models/AppSettings.cs`
- Modify: `src/VoiceInput/Services/SettingsStore.cs`
- Create: `tests/VoiceInput.Tests/FunAsrModelCatalogTests.cs`
- Modify: `tests/VoiceInput.Tests/SettingsStoreTests.cs`

- [ ] **Step 1: Write failing catalog and persistence tests**

Cover these exact behaviors:

```csharp
[Fact]
public void DefaultsToSenseVoiceAndHasThreeUniqueModels()
{
    Assert.Equal("sensevoice-small-q8", FunAsrModelCatalog.Default.Id);
    Assert.Equal(3, FunAsrModelCatalog.Models.Count);
    Assert.Equal(3, FunAsrModelCatalog.Models.Select(x => x.Id).Distinct().Count());
}

[Theory]
[InlineData("sensevoice-small-q8", "vi-VN", false)]
[InlineData("sensevoice-small-q8", "ko-KR", true)]
[InlineData("paraformer-zh-q8", "ja-JP", false)]
[InlineData("fun-asr-nano-q4", "ja-JP", true)]
public void ReportsLanguageCompatibility(string id, string language, bool expected) =>
    Assert.Equal(expected, FunAsrModelCatalog.Get(id).Supports(language));
```

Extend the settings round-trip test with `Engine = SpeechEngineKind.FunAsr` and `FunAsrModelId = "paraformer-zh-q8"`.

- [ ] **Step 2: Run tests and verify RED**

Run:

```powershell
dotnet test tests/VoiceInput.Tests/VoiceInput.Tests.csproj --filter "FullyQualifiedName~FunAsrModelCatalogTests|FullyQualifiedName~SettingsStoreTests"
```

Expected: compilation fails because the catalog, enum value, and setting do not exist.

- [ ] **Step 3: Implement the fixed catalog and settings**

Use these exact artifact values:

```csharp
internal sealed record FunAsrArtifact(string RelativePath, Uri Url, long Size, string Sha256);
internal enum FunAsrRunnerKind { SenseVoice, Paraformer, Nano }
internal sealed record FunAsrModelDefinition(
    string Id, string DisplayName, string Description, FunAsrRunnerKind Runner,
    IReadOnlySet<string> Languages, IReadOnlyList<FunAsrArtifact> Artifacts,
    Uri Source, Uri License)
{
    public bool Supports(string language) => Languages.Contains(language);
    public long DownloadSize => Artifacts.Sum(x => x.Size);
}
```

Use language sets `{ en-US, zh-CN, zh-TW, ja-JP, ko-KR }` for SenseVoice, `{ en-US, zh-CN, zh-TW }` for Paraformer, and `{ en-US, zh-CN, zh-TW, ja-JP }` for Nano. Point `Source` to each pinned Hugging Face GGUF repository and `License` to the license shown by that model card.

Pin:

- Runtime zip: `https://github.com/modelscope/FunASR/releases/download/runtime-llamacpp-v0.1.5/funasr-llamacpp-windows-x64.zip`, 4,663,321 bytes, SHA-256 `2398192c1dd965a3d6c150833757a55047fa616a8b3561dd4d674259a913afbd`.
- VAD: `https://huggingface.co/FunAudioLLM/fsmn-vad-GGUF/resolve/6840bae4c5c92ee8c04faaf4db23dd0105098d7f/fsmn-vad.gguf`, 1,720,512 bytes, SHA-256 `1270f2559c495f4e7b6e739541151027d360761a3fda43fc147034f5719f5479`.
- SenseVoice: `https://huggingface.co/FunAudioLLM/SenseVoiceSmall-GGUF/resolve/90c1c61912018b70ada0fcc024ea24aca62f2e63/sensevoice-small-q8.gguf`, 254,208,320 bytes, SHA-256 `4ae45c94422de949b387e2e0fb10d7e14e4c42c69db30c3444ecc7d4b844b7c5`.
- Paraformer: `https://huggingface.co/FunAudioLLM/Paraformer-GGUF/resolve/de2cbaaa0f30b34f398d7a066fdfefb8e50d902c/paraformer-q8.gguf`, 236,929,024 bytes, SHA-256 `42bf76ea1575a336aaca4c1b7c01a82b79113e6d04d0d6b799561bfcf07ee011`.
- Nano encoder: `https://huggingface.co/FunAudioLLM/Fun-ASR-Nano-GGUF/resolve/c1629cbf83548ea0d92077c09d3541ce407ee643/funasr-encoder-f16.gguf`, 469,331,008 bytes, SHA-256 `f92f91d01a24fbed6c863495b2ee8c6a6788144a02858b75743f0946668de8a2`.
- Nano q4 decoder: `https://huggingface.co/FunAudioLLM/Fun-ASR-Nano-GGUF/resolve/c1629cbf83548ea0d92077c09d3541ce407ee643/qwen3-0.6b-q4km.gguf`, 484,219,776 bytes, SHA-256 `cc5057552aa9dddedcda73ea8889854e8a257eb07d0a561b7234465c1e856f22`.

Add:

```csharp
public string FunAsrModelId { get; set; } = "sensevoice-small-q8";
```

Load unknown/missing IDs as the catalog default without changing other settings.

- [ ] **Step 4: Run focused tests and verify GREEN**

Run the Step 2 command. Expected: all selected tests pass.

### Task 2: Shared WAV Encoding

**Files:**
- Create: `src/VoiceInput/Services/PcmWave.cs`
- Modify: `src/VoiceInput/Services/OpenAiTranscribeEngine.cs`
- Create: `tests/VoiceInput.Tests/PcmWaveTests.cs`

- [ ] **Step 1: Write a failing WAV header test**

```csharp
[Fact]
public void WrapCreatesCanonicalMonoPcmWave()
{
    byte[] wav = PcmWave.Wrap([1, 2, 3, 4], 16000);
    Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
    Assert.Equal(48, wav.Length);
    Assert.Equal(4, BitConverter.ToInt32(wav, 40));
    Assert.Equal(new byte[] { 1, 2, 3, 4 }, wav[44..]);
}
```

- [ ] **Step 2: Run the test and verify RED**

Run `dotnet test tests/VoiceInput.Tests/VoiceInput.Tests.csproj --filter FullyQualifiedName~PcmWaveTests`.

Expected: compilation fails because `PcmWave` does not exist.

- [ ] **Step 3: Move the existing 44-byte WAV writer into `PcmWave.Wrap`**

Keep the same PCM format and replace `OpenAiTranscribeEngine.BuildWav` with `PcmWave.Wrap`. Remove only the now-unused private method.

- [ ] **Step 4: Run WAV and cloud-engine tests**

Run:

```powershell
dotnet test tests/VoiceInput.Tests/VoiceInput.Tests.csproj --filter "FullyQualifiedName~PcmWaveTests|FullyQualifiedName~OpenAiTranscribeEngineTests"
```

Expected: all selected tests pass.

### Task 3: Verified Runtime and Model Downloads

**Files:**
- Create: `src/VoiceInput/Services/FunAsrRuntimeManager.cs`
- Create: `tests/VoiceInput.Tests/FunAsrRuntimeManagerTests.cs`

- [ ] **Step 1: Write failing state and download tests**

Test through an internal constructor accepting a root path and `HttpClient`. Use a fake `HttpMessageHandler` that supports full and ranged responses. Cover:

```csharp
[Fact] public void MissingFilesReportNotInstalled();
[Fact] public async Task InstallWritesVerifiedArtifactAtomically();
[Fact] public async Task InstallResumesExistingPartFile();
[Fact] public async Task HashMismatchDoesNotActivateArtifact();
[Fact] public async Task CancellationPreservesPartFile();
[Fact] public async Task InsufficientSpaceFailsBeforeDownload();
[Fact] public async Task CorruptRuntimeDoesNotReplaceVerifiedRuntime();
```

Assertions must verify the final file is absent on failure, `.part` remains on cancellation, and progress reaches `Verifying` then `Installed` on success.

- [ ] **Step 2: Run tests and verify RED**

Run `dotnet test tests/VoiceInput.Tests/VoiceInput.Tests.csproj --filter FullyQualifiedName~FunAsrRuntimeManagerTests`.

Expected: compilation fails because the manager and states do not exist.

- [ ] **Step 3: Implement the minimum manager API**

```csharp
internal enum FunAsrInstallStage { NotInstalled, Downloading, Verifying, Testing, Installed, Failed }
internal sealed record FunAsrInstallProgress(
    string ModelId, FunAsrInstallStage Stage, string Artifact,
    long DownloadedBytes, long? TotalBytes, string? Error = null);

internal sealed record FunAsrResolvedModel(
    FunAsrModelDefinition Definition, string ExecutablePath, string VadPath,
    IReadOnlyDictionary<string, string> ArtifactPaths);

internal sealed class FunAsrRuntimeManager : IDisposable
{
    internal FunAsrRuntimeManager(
        string rootPath, HttpClient httpClient, Func<long>? availableBytes = null,
        Func<FunAsrResolvedModel, CancellationToken, Task>? smokeTest = null);
    public event Action<FunAsrInstallProgress>? ProgressChanged;
    public string RootPath { get; }
    public bool IsInstalled(string modelId);
    public Task InstallAsync(string modelId, CancellationToken cancellationToken);
    public void Remove(string modelId);
    public FunAsrResolvedModel Resolve(string modelId);
}
```

Implementation rules:

- Default root is `%LOCALAPPDATA%\VoiceInput\FunASR`.
- Stream with `HttpCompletionOption.ResponseHeadersRead`.
- Resume `.part` with `RangeHeaderValue(existingLength, null)`; restart only if the server rejects the range.
- Compare remaining expected bytes plus a 64 MB staging margin with `DriveInfo.AvailableFreeSpace` before each install.
- Reject paths that do not resolve under `RootPath`.
- Verify exact byte count and SHA-256 before `File.Move(..., overwrite: true)`.
- Extract the verified runtime zip into a staging directory, verify required executables, then atomically move to `runtime\v0.1.5`.
- Write `installation.json.tmp`, then replace/move atomically.
- Generate a one-second silent PCM WAV with `PcmWave.Wrap` for the installation smoke test; do not add a binary test asset.
- Run only one install/remove operation at a time with `SemaphoreSlim`.

- [ ] **Step 4: Run manager tests and verify GREEN**

Run the Step 2 command. Expected: all manager tests pass.

### Task 4: Native Batch Speech Engine

**Files:**
- Create: `src/VoiceInput/Services/FunAsrEngine.cs`
- Create: `tests/VoiceInput.Tests/FunAsrEngineTests.cs`

- [ ] **Step 1: Write failing command and lifecycle tests**

Cover exact command mappings and lifecycle:

```csharp
[Theory]
[InlineData("sensevoice-small-q8", "llama-funasr-sensevoice.exe")]
[InlineData("paraformer-zh-q8", "llama-funasr-paraformer.exe")]
[InlineData("fun-asr-nano-q4", "llama-funasr-cli.exe")]
public void UsesCatalogExecutable(string modelId, string executable);

[Fact] public async Task EmptyRecordingDoesNotLaunchProcess();
[Fact] public async Task SuccessfulProcessRaisesTrimmedFinalText();
[Fact] public async Task NonzeroExitRaisesServiceFaultWithoutFinalText();
[Fact] public async Task CancelKillsProcessAndRaisesNoFault();
```

Inject a process runner delegate only in the internal test constructor; production uses `Process` directly.

```csharp
internal sealed record FunAsrProcessResult(int ExitCode, string StandardOutput, string StandardError);
```

- [ ] **Step 2: Run tests and verify RED**

Run `dotnet test tests/VoiceInput.Tests/VoiceInput.Tests.csproj --filter FullyQualifiedName~FunAsrEngineTests`.

Expected: compilation fails because `FunAsrEngine` does not exist.

- [ ] **Step 3: Implement the local engine**

Requirements:

- Implement `ISpeechEngine`, `NeedsAudioFeed = true`, `HasInterimResults = false`, and `StopTimeoutMs = 60000`.
- Buffer PCM under a lock, write a WAV with `PcmWave.Wrap`, and create a unique temp path under `%TEMP%\VoiceInput`.
- Build `ProcessStartInfo` with `UseShellExecute = false`, `CreateNoWindow = true`, redirected stdout/stderr, and `ArgumentList` entries.
- Keep stderr in diagnostics and treat trimmed stdout as the transcript.
- Never log stdout/transcript text unless existing diagnostic transcript logging is enabled.
- Kill the entire child process tree on cancellation or timeout.
- Delete only the engine-created temporary WAV in `finally`.
- Never fall back to another engine.

- [ ] **Step 4: Run engine tests and verify GREEN**

Run the Step 2 command. Expected: all engine tests pass.

### Task 5: Controller Integration

**Files:**
- Modify: `src/VoiceInput/AppController.cs`
- Modify: `tests/VoiceInput.Tests/SettingsStoreTests.cs`

- [ ] **Step 1: Add a failing settings compatibility assertion**

Deserialize an old JSON document without `FunAsrModelId` and assert the existing engine remains unchanged while the local model defaults to SenseVoice.

- [ ] **Step 2: Run the focused test and verify RED if compatibility is incomplete**

Run `dotnet test tests/VoiceInput.Tests/VoiceInput.Tests.csproj --filter FullyQualifiedName~SettingsStoreTests`.

- [ ] **Step 3: Wire the runtime manager and engine**

Add one app-lifetime `FunAsrRuntimeManager`. In `CreateEngine`:

```csharp
case SpeechEngineKind.FunAsr:
    if (!_funAsr.IsInstalled(_settings.FunAsrModelId))
        throw new InvalidOperationException("Download the selected FunASR model in Setup before using it.");
    return new FunAsrEngine(_funAsr.Resolve(_settings.FunAsrModelId));
```

Add `FunASR (local)` to the tray engine menu. Selecting it without an installed compatible model opens Setup and shows a notification instead of persisting a broken selection. Dispose the manager during shutdown.

- [ ] **Step 4: Run the full non-UI test suite**

Run `dotnet test tests/VoiceInput.Tests/VoiceInput.Tests.csproj`.

Expected: all tests pass.

### Task 6: Setup Hub UI

**Files:**
- Replace: `src/VoiceInput/Views/SettingsWindow.xaml`
- Modify: `src/VoiceInput/Views/SettingsWindow.xaml.cs`
- Modify: `src/VoiceInput/AppController.cs`

- [ ] **Step 1: Build the approved shell**

Use a 900x650 window, minimum 720x520, a 184px left navigation column, scrollable content column, and fixed footer. Add five selectable navigation rows: Overview, Speech, FunASR, Refinement, App. Use Segoe UI/Segoe Fluent Icons, neutral surfaces, 6px maximum corner radius, green primary action, amber attention state, and keyboard focus visuals.

- [ ] **Step 2: Move existing controls without changing their semantics**

Pass non-setting app actions through one small code-behind record:

```csharp
internal sealed record SettingsWindowActions(
    Func<bool> IsAutoStartEnabled,
    Action<bool> SetAutoStart,
    Func<Task> CheckForUpdates,
    Action OpenLog);
```

- Speech: engine selector plus all Azure Speech and Foundry fields/auth modes.
- Refinement: enable, base URL, API key, model, custom prompt, and Test LLM.
- App: language, push-to-talk, context, learning, diagnostic logging, start-at-login, check update, and open log.
- Footer: validation/status plus Save changes and Cancel.

Keep the draft clone and current validation model. A dirty draft enables Save changes; downloads are app-level and survive Cancel.

- [ ] **Step 3: Add Overview and FunASR model management**

Overview shows active engine, selected model, local readiness, LLM state, PTT, and language. FunASR shows one row per catalog model with description, published size, compatibility, state, progress, and one applicable command: Download, Use, Cancel, Retry, or Remove.

Disable Use for unsupported languages. `Set up FunASR` installs SenseVoice, selects it after smoke-test success, and marks the draft engine as FunASR.

- [ ] **Step 4: Build to catch XAML and code-behind errors**

Run:

```powershell
dotnet build src/VoiceInput/VoiceInput.csproj -p:EnableWindowsTargeting=true
```

Expected: build succeeds with zero errors.

### Task 7: Documentation, Regression, and Local Installation

**Files:**
- Modify: `README.md`
- Modify if required by build: `src/VoiceInput/VoiceInput.csproj`

- [ ] **Step 1: Update user documentation**

Document the three local models, on-demand sizes, default SenseVoice behavior, GGUF/no-Python runtime, local-only audio path, language limits, Setup Hub, and model source/license links. Do not claim official benchmark speed as local measured speed.

- [ ] **Step 2: Run formatting and full tests**

Run:

```powershell
dotnet format VoiceInput.sln --verify-no-changes
dotnet test tests/VoiceInput.Tests/VoiceInput.Tests.csproj
```

If there is no solution file, run `dotnet format src/VoiceInput/VoiceInput.csproj --verify-no-changes` instead. Expected: formatting check and all tests pass.

- [ ] **Step 3: Build the self-contained application**

Run the repository's existing local install path:

```powershell
make install
```

Expected: the app builds, installs under `%LOCALAPPDATA%\VoiceInput`, starts, and retains current `%APPDATA%\VoiceInput\settings.json`.

- [ ] **Step 4: Perform the live smoke test**

Open Setup, verify all five pages render without overlap, download SenseVoice, select FunASR, and transcribe the existing three WAV samples or equivalent live speech. Confirm cancel/resume, restart persistence, no Python process, and no listening network port.

- [ ] **Step 5: Report the testable result**

Provide the installed executable path, exact automated-test/build results, local model smoke-test latency/text, known limitations, and any step the user must perform. Do not commit or push.
