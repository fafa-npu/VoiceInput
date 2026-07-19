# Repository Guidelines

## Project Structure & Module Organization

`src/VoiceInput/` contains the .NET 10 WPF client, organized into `Models/`, `Services/`, `Interop/`, `Views/`, and `Assets/`. `src/VoiceInputMac/` contains the Swift/AppKit client, with code in `Sources/`, resources in `Resources/`, and XCTest coverage in `Tests/`. Windows xUnit tests live in `tests/VoiceInput.Tests`. Automation belongs in `scripts/`; design notes belong in `docs/`.

Do not commit generated directories such as `bin/`, `obj/`, `.build/`, `dist/`, `publish/`, `artifacts/`, or downloaded model/runtime files. When changing shared behavior—profiles, settings, overlays, model management, or text delivery—review both clients for parity.

## Build, Test, and Development Commands

```bash
make build              # Windows Release build
make run                # Run the WPF client in Debug
dotnet test tests/VoiceInput.Tests/VoiceInput.Tests.csproj
make mac-test           # Run Swift/XCTest tests
make mac-build          # Build dist/gujiguji.app
make mac-run            # Build and launch the macOS app
make publish            # Produce the Windows single-file executable
```

Windows work requires the .NET 10 SDK. macOS work requires Swift 6/Xcode and targets macOS 15 on Apple Silicon. Before submitting, run the tests for every platform affected and `git diff --check`.

## Coding Style & Naming Conventions

Use four-space indentation and follow the surrounding file; no repository-wide formatter is enforced. In C#, use PascalCase for types and members, `_camelCase` for private fields, and the `Async` suffix for asynchronous methods. In Swift, use UpperCamelCase for types, lowerCamelCase for functions/properties, and keep UI mutations on `@MainActor`. Keep builds warning-free.

## Testing Guidelines

Windows tests use xUnit (`[Fact]`, descriptive PascalCase method names). macOS tests use XCTest (`XCTestCase`, methods beginning with `test`). There is no numeric coverage threshold, but every bug fix or behavior change should add a focused regression test. Mock network, speech, and native inference boundaries; unit tests must not download large models or require credentials.

## Commit & Pull Request Guidelines

Prefer concise imperative subjects, for example `feat: add native macOS client` or `fix: preserve focused input target`. Pull requests should explain behavior, platform impact, and verification commands; link issues and include screenshots for settings or overlay changes. Document new permissions, dependencies, model revisions, sizes, and SHA-256 values.

## Security & Configuration

Never commit API keys, signing certificates, tokens, transcripts, or local absolute paths. Windows secrets use DPAPI; macOS secrets use Keychain. Keep external binaries and model artifacts pinned and checksum-verified. Signed macOS releases must use `scripts/release-macos.sh`; local ad-hoc builds can require Accessibility and Input Monitoring to be re-approved.
