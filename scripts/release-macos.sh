#!/bin/zsh
set -euo pipefail

ROOT="${0:A:h:h}"
: "${SIGN_IDENTITY:?Set SIGN_IDENTITY to a Developer ID Application certificate}"
: "${NOTARY_PROFILE:?Set NOTARY_PROFILE to an xcrun notarytool keychain profile}"
VERSION="${VERSION:-0.2.17}"
TAG="v${VERSION#v}"

CONFIG=release VERSION="$VERSION" SIGN_IDENTITY="$SIGN_IDENTITY" "$ROOT/scripts/build-macos.sh"
APP="$ROOT/dist/gujiguji.app"
NOTARY_ZIP="$(mktemp -t gujiguji-notary).zip"
trap 'rm -f "$NOTARY_ZIP"' EXIT

ditto -c -k --keepParent "$APP" "$NOTARY_ZIP"
xcrun notarytool submit "$NOTARY_ZIP" --keychain-profile "$NOTARY_PROFILE" --wait
xcrun stapler staple "$APP"
xcrun stapler validate "$APP"

ASSET="$ROOT/dist/gujiguji-mac.zip"
ditto -c -k --keepParent "$APP" "$ASSET"
shasum -a 256 "$ASSET"

if [[ "${PUBLISH_GITHUB:-0}" == "1" ]]; then
  if gh release view "$TAG" --repo fafa-npu/VoiceInput >/dev/null 2>&1; then
    gh release upload "$TAG" "$ASSET" --clobber --repo fafa-npu/VoiceInput
  else
    gh release create "$TAG" "$ASSET" --generate-notes --repo fafa-npu/VoiceInput
  fi
fi
