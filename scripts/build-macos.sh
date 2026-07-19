#!/bin/zsh
set -euo pipefail

ROOT="${0:A:h:h}"
PACKAGE="$ROOT/src/VoiceInputMac"
CONFIG="${CONFIG:-release}"
APP="$ROOT/dist/gujiguji.app"
CONTENTS="$APP/Contents"

swift build --package-path "$PACKAGE" -c "$CONFIG"
BIN_DIR="$(swift build --package-path "$PACKAGE" -c "$CONFIG" --show-bin-path)"
BIN="$BIN_DIR/gujiguji-mac"

rm -rf "$APP"
mkdir -p "$CONTENTS/MacOS" "$CONTENTS/Resources" "$CONTENTS/Frameworks"
cp "$BIN" "$CONTENTS/MacOS/gujiguji-mac"
cp "$PACKAGE/Resources/Info.plist" "$CONTENTS/Info.plist"
if [[ -n "${VERSION:-}" ]]; then
  VERSION="${VERSION#v}"
  [[ "$VERSION" == <->.<->.<-> ]] || { echo "VERSION must be x.y.z" >&2; exit 2; }
  plutil -replace CFBundleShortVersionString -string "$VERSION" "$CONTENTS/Info.plist"
  plutil -replace CFBundleVersion -string "${BUILD_VERSION:-${VERSION//./}}" "$CONTENTS/Info.plist"
fi
cp "$PACKAGE/Resources/gujiguji.icns" "$CONTENTS/Resources/gujiguji.icns"
cp "$PACKAGE/Resources/ThirdPartyNotices.md" "$CONTENTS/Resources/ThirdPartyNotices.md"
ditto "$BIN_DIR/MicrosoftCognitiveServicesSpeech.framework" \
  "$CONTENTS/Frameworks/MicrosoftCognitiveServicesSpeech.framework"
ditto "$BIN_DIR/CTranscribe.framework" \
  "$CONTENTS/Frameworks/CTranscribe.framework"

# The upstream 0.1.3 zip stores the macOS versioned-framework links as full
# duplicate directories/files. Normalize them before signing; otherwise
# codesign reports an ambiguous bundle and Library Validation rejects it.
TRANSCRIBE_FRAMEWORK="$CONTENTS/Frameworks/CTranscribe.framework"
if [[ ! -L "$TRANSCRIBE_FRAMEWORK/Versions/Current" ]]; then
  rm -rf "$TRANSCRIBE_FRAMEWORK/Versions/Current"
  rm -rf "$TRANSCRIBE_FRAMEWORK/Headers" "$TRANSCRIBE_FRAMEWORK/Modules" \
    "$TRANSCRIBE_FRAMEWORK/Resources"
  rm -f "$TRANSCRIBE_FRAMEWORK/CTranscribe"
  ln -s A "$TRANSCRIBE_FRAMEWORK/Versions/Current"
  ln -s Versions/Current/CTranscribe "$TRANSCRIBE_FRAMEWORK/CTranscribe"
  ln -s Versions/Current/Headers "$TRANSCRIBE_FRAMEWORK/Headers"
  ln -s Versions/Current/Modules "$TRANSCRIBE_FRAMEWORK/Modules"
  ln -s Versions/Current/Resources "$TRANSCRIBE_FRAMEWORK/Resources"
fi
while IFS= read -r rpath; do
  case "$rpath" in
    /Applications/Xcode.app/*|*/.build/*)
      install_name_tool -delete_rpath "$rpath" "$CONTENTS/MacOS/gujiguji-mac"
      ;;
  esac
done < <(otool -l "$CONTENTS/MacOS/gujiguji-mac" | awk '/LC_RPATH/{getline; getline; print $2}')
install_name_tool -add_rpath "@executable_path/../Frameworks" "$CONTENTS/MacOS/gujiguji-mac"

IDENTITY="${SIGN_IDENTITY:--}"
if [[ "$IDENTITY" == "-" ]]; then
  # Ad-hoc signatures have no Team ID. Enabling Hardened Runtime here makes
  # Library Validation reject the embedded Azure Speech framework at launch.
  codesign --force --sign "$IDENTITY" \
    "$CONTENTS/Frameworks/MicrosoftCognitiveServicesSpeech.framework"
  codesign --force --sign "$IDENTITY" \
    "$CONTENTS/Frameworks/CTranscribe.framework"
  codesign --force --deep --entitlements \
    "$PACKAGE/Resources/gujiguji.entitlements" --sign "$IDENTITY" "$APP"
else
  codesign --force --options runtime --sign "$IDENTITY" \
    "$CONTENTS/Frameworks/MicrosoftCognitiveServicesSpeech.framework"
  codesign --force --options runtime --sign "$IDENTITY" \
    "$CONTENTS/Frameworks/CTranscribe.framework"
  codesign --force --deep --options runtime --entitlements \
    "$PACKAGE/Resources/gujiguji.entitlements" --sign "$IDENTITY" "$APP"
fi

echo "$APP"
