# VoiceInput - Windows menu-bar (system tray) voice input method
# Mirrors the macOS Swift Package + Makefile workflow with the .NET toolchain.

PROJECT      := src/VoiceInput/VoiceInput.csproj
CONFIG       := Release
RID          := win-x64
PUBLISH_DIR  := publish

# Optional Authenticode signing. Provide a PFX + password to sign the published exe:
#   make publish SIGN_PFX=mycert.pfx SIGN_PWD=secret
# or a cert subject name from the user store:
#   make publish SIGN_SUBJECT="My Company"
SIGN_PFX     ?=
SIGN_PWD     ?=
SIGN_SUBJECT ?=
TIMESTAMP_URL ?= http://timestamp.digicert.com

.PHONY: build run clean publish sign restore

restore:
	dotnet restore $(PROJECT)

build:
	dotnet build $(PROJECT) -c $(CONFIG)

run:
	dotnet run --project $(PROJECT) -c Debug

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

# Copy the published exe to the per-user app folder and create a Startup shortcut.
install: publish
	powershell -NoProfile -ExecutionPolicy Bypass -File scripts/install.ps1 -Source "$(PUBLISH_DIR)/VoiceInput.exe"
