# gujiguji - Windows menu-bar (system tray) voice input method
# Mirrors the macOS Swift Package + Makefile workflow with the .NET toolchain.

PROJECT      := src/VoiceInput/VoiceInput.csproj
CONFIG       := Release
RID          := win-x64
PUBLISH_DIR  := publish

# Optional Authenticode signing for local publish:
#   make publish SIGN_PFX=mycert.pfx SIGN_PWD=secret
# or a cert subject name from the user store:
#   make publish SIGN_SUBJECT="My Company"
SIGN_PFX     ?=
SIGN_PWD     ?=
SIGN_SUBJECT ?=
TIMESTAMP_URL ?= http://timestamp.digicert.com

# Release (GitHub Enterprise) settings
VERSION  ?= v0.2.16
GHE_HOST ?= microsoft.ghe.com
GHE_REPO ?= Zhao-Hua/VoiceInput

.PHONY: build run clean publish sign restore install release mac-build mac-test mac-run

restore:
	dotnet restore $(PROJECT)

build:
	dotnet build $(PROJECT) -c $(CONFIG)

run:
	dotnet run --project $(PROJECT) -c Debug

mac-test:
	swift test --package-path src/VoiceInputMac

mac-build:
	scripts/build-macos.sh

mac-run: mac-build
	open dist/gujiguji.app

clean:
	dotnet clean $(PROJECT) -c $(CONFIG) || true
	rm -rf $(PUBLISH_DIR)
	rm -rf src/VoiceInput/bin src/VoiceInput/obj

# Self-contained, single-file exe. WPF cannot be trimmed, so the runtime is bundled.
publish:
	dotnet publish $(PROJECT) -c $(CONFIG) -r $(RID) \
		-p:SelfContained=true \
		-p:PublishSingleFile=true \
		-p:IncludeNativeLibrariesForSelfExtract=true \
		-p:EnableCompressionInSingleFile=true \
		-o $(PUBLISH_DIR)
	@echo "Published to $(PUBLISH_DIR)/VoiceInput.exe"
ifneq ($(strip $(SIGN_PFX)),)
	signtool sign /fd SHA256 /tr $(TIMESTAMP_URL) /td SHA256 /f "$(SIGN_PFX)" /p "$(SIGN_PWD)" "$(PUBLISH_DIR)/VoiceInput.exe"
else ifneq ($(strip $(SIGN_SUBJECT)),)
	signtool sign /fd SHA256 /tr $(TIMESTAMP_URL) /td SHA256 /n "$(SIGN_SUBJECT)" "$(PUBLISH_DIR)/VoiceInput.exe"
else
	@echo "[sign] skipped - provide SIGN_PFX/SIGN_PWD or SIGN_SUBJECT to Authenticode-sign the exe."
endif

# One-click: build, copy to %LOCALAPPDATA%, enable auto-start, and launch.
install: publish
	powershell -NoProfile -ExecutionPolicy Bypass -File scripts/install.ps1 -Source "$(PUBLISH_DIR)/VoiceInput.exe" -AllowUnsignedDevelopmentBuild

# Build a versioned self-contained exe and publish it as a GHE release (asset uploaded via REST,
# since older gh mishandles this instance's upload endpoint). The version is baked into the exe so
# the in-app update check compares correctly. One-time: gh auth login --hostname $(GHE_HOST).
release:
	@test -n "$(SIGN_PFX)" || (echo "release requires SIGN_PFX" && exit 1)
	powershell -NoProfile -ExecutionPolicy Bypass -File scripts/release.ps1 -Version $(VERSION) -SignPfx "$(SIGN_PFX)"
