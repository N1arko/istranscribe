#!/bin/sh
set -eu

# @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#distribution
# @spec spec://modules/platform/INFRA-005.C-deterministic-third-party-notices#decisions.payload
# @spec spec://modules/app/FEAT-017-speaker-aware-transcription#diarization

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
output_root=${1:-"$repo_root/artifacts/release/macos-arm64"}
configuration=${CONFIGURATION:-Release}
version=${VERSION:-0.1.0}
build_number=${BUILD_NUMBER:-1}
publish_root="$output_root/publish"
app_root="$output_root/isTranscribe.app"
contents="$app_root/Contents"
stage_root="$output_root/dmg-root"
manifest="$output_root/release-manifest.json"
dmg="$output_root/isTranscribe-$version-osx-arm64.dmg"

case "$output_root" in
  "$repo_root"/artifacts/*|/private/tmp/*|/tmp/*|/var/folders/*) ;;
  *) echo "Release output must be under repository artifacts or a temporary directory: $output_root" >&2; exit 2 ;;
esac
rm -rf "$output_root"
mkdir -p "$publish_root" "$contents/MacOS" "$contents/Resources/third-party" "$stage_root"

dotnet publish "$repo_root/src/IsTranscribe.App.MacOS/IsTranscribe.App.MacOS.csproj" \
  -c "$configuration" -r osx-arm64 --self-contained true \
  -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false \
  -p:RuntimeFrameworkVersion=10.0.11 \
  -o "$publish_root"

worker_root="$output_root/worker-publish"
dotnet publish "$repo_root/src/IsTranscribe.Transcription.Worker/IsTranscribe.Transcription.Worker.csproj" \
  -c "$configuration" -r osx-arm64 --self-contained true \
  -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false \
  -p:RuntimeFrameworkVersion=10.0.11 \
  -o "$worker_root"
native_root=${TRANSCRIPTION_NATIVE_OUTPUT_ROOT:-"$output_root/transcription-native"}
if [ -z "${TRANSCRIPTION_NATIVE_OUTPUT_ROOT:-}" ]; then
  pwsh -NoProfile -File "$repo_root/eng/transcription/Fetch-WhisperSource.ps1" \
    -DestinationRoot "$output_root/whisper-source"
  whisper_commit=$(jq -r '.source.commit' "$repo_root/native/whisper/runtime-manifest.v1.json")
  pwsh -NoProfile -File "$repo_root/eng/transcription/Build-WhisperNative.ps1" \
    -Rid osx-arm64 -SourceRoot "$output_root/whisper-source/source/$whisper_commit" \
    -SourceReceiptPath "$output_root/whisper-source/source-receipt.v1.json" \
    -BuildRoot "$output_root/whisper-build" -OutputRoot "$native_root" -CMakePath "$(command -v cmake)"
  pwsh -NoProfile -File "$repo_root/eng/transcription/Build-SpeakerNative.ps1" \
    -Rid osx-arm64 -BuildRoot "$output_root/speaker-build" -OutputRoot "$native_root"
fi
pwsh -NoProfile -File "$repo_root/eng/transcription/Compose-LocalTranscriptionPayload.ps1" \
  -Rid osx-arm64 -ApplicationPublishRoot "$publish_root" -WorkerPublishRoot "$worker_root" -NativeOutputRoot "$native_root"
# This receipt verifies the composition inputs. Mach-O load paths and signatures
# change during bundling; the final release manifest seals the resulting bytes.
pwsh -NoProfile -File "$repo_root/eng/transcription/Test-LocalTranscriptionPayload.ps1" \
  -Rid osx-arm64 -PackageRoot "$publish_root"

cp -R "$publish_root/." "$contents/MacOS/"
for universal in libAvaloniaNative.dylib libHarfBuzzSharp.dylib libSkiaSharp.dylib; do
  lipo "$contents/MacOS/$universal" -thin arm64 -output "$contents/MacOS/$universal.thin"
  mv "$contents/MacOS/$universal.thin" "$contents/MacOS/$universal"
done
"$repo_root/eng/bundle-macos-native-dependencies.sh" "$contents/MacOS"
sed -e "s/__VERSION__/$version/g" -e "s/__BUILD__/$build_number/g" \
  "$repo_root/packaging/macos/Info.plist" > "$contents/Info.plist"
cp "$repo_root/packaging/macos/isTranscribe.entitlements" "$contents/Resources/isTranscribe.entitlements"
cp "$repo_root/packaging/macos/FIRST-LAUNCH.txt" "$stage_root/FIRST-LAUNCH.txt"
"$repo_root/eng/generate-macos-third-party-notices.sh" \
  "$contents/MacOS" \
  "$contents/Resources/THIRD-PARTY-NOTICES.txt" \
  "$contents/Resources/third-party-dependency-inventory.json"
{
  printf '\n\nLocal speech recognition and speaker runtime\n'
  find "$contents/MacOS/speaker-licenses" -type f -print | LC_ALL=C sort | while IFS= read -r license; do
    printf '\n========== %s ==========\n' "$(basename "$license")"
    sed 's/\r$//' "$license"
  done
  for license in "$repo_root"/native/whisper/licenses/*; do
    test -f "$license" || continue
    printf '\n========== %s ==========\n' "$(basename "$license")"
    sed 's/\r$//' "$license"
  done
} >> "$contents/Resources/THIRD-PARTY-NOTICES.txt"
jq --arg sha "$(shasum -a 256 "$contents/Resources/THIRD-PARTY-NOTICES.txt" | awk '{print $1}')" \
  '.notice.sha256 = $sha' "$contents/Resources/third-party-dependency-inventory.json" > "$output_root/notices-inventory.json"
mv "$output_root/notices-inventory.json" "$contents/Resources/third-party-dependency-inventory.json"
cp "$contents/MacOS/third-party/lame-3.100.tar.xz" "$contents/Resources/third-party/"
cp "$contents/MacOS/third-party/LAME-COPYING.LGPL-2.1.txt" "$contents/Resources/third-party/"
rm -rf "$contents/MacOS/third-party"

iconset="$output_root/isTranscribe.iconset"
mkdir -p "$iconset"
source_icon="$repo_root/src/IsTranscribe.Desktop/Assets/isTranscribe.png"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$source_icon" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$source_icon" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$contents/Resources/isTranscribe.icns"
rm -rf "$iconset" "$publish_root"
chmod +x "$contents/MacOS/IsTranscribe.App.MacOS"

if [ -n "${MACOS_SIGNING_IDENTITY:-}" ]; then
  codesign --force --deep --options runtime --entitlements "$contents/Resources/isTranscribe.entitlements" \
    --sign "$MACOS_SIGNING_IDENTITY" "$app_root"
else
  # Apple Silicon requires every native image to carry a valid signature. Sign
  # the completed bundle as well: the main executable's signature seals the app
  # resources and therefore must be created after Info.plist and the icon exist.
  codesign --force --deep --entitlements "$contents/Resources/isTranscribe.entitlements" \
    --sign - "$app_root"
fi

files_json="$output_root/files.jsonl"
: > "$files_json"
find "$app_root" -type f -print | LC_ALL=C sort | while IFS= read -r file; do
  relative=${file#"$app_root/"}
  sha=$(shasum -a 256 "$file" | awk '{print $1}')
  bytes=$(stat -f '%z' "$file")
  jq -cn --arg path "$relative" --arg sha256 "$sha" --argjson bytes "$bytes" \
    '{path:$path,sha256:$sha256,bytes:$bytes}' >> "$files_json"
done

native_libraries=$(find "$contents/MacOS" -maxdepth 1 -type f -name '*.dylib' -exec basename {} \; \
  | LC_ALL=C sort | jq -R -s 'split("\n") | map(select(length > 0))')

jq -s --arg version "$version" --arg bundle_id "com.istranscribe.app" \
  --arg minimum_os "14.2" --arg target "osx-arm64" --argjson native_libraries "$native_libraries" \
  '{schemaVersion:"infra-010-macos-release-v1",version:$version,bundleId:$bundle_id,targetRid:$target,architecture:"arm64",minimumOS:$minimum_os,permissions:["microphone","system_audio","accessibility","screen_capture","notifications"],nativeLibraries:$native_libraries,notices:"Contents/Resources/THIRD-PARTY-NOTICES.txt",dependencyInventory:"Contents/Resources/third-party-dependency-inventory.json",localRuntimeInventoryStage:"verified-before-mach-o-rewrite-and-signing",files:.}' \
  "$files_json" > "$manifest"
rm "$files_json"

cp -R "$app_root" "$stage_root/"
ln -s /Applications "$stage_root/Applications"
hdiutil create -quiet -fs HFS+ -volname isTranscribe -srcfolder "$stage_root" "$dmg"
(cd "$output_root" && shasum -a 256 "$(basename "$dmg")" "$(basename "$manifest")" > SHA256SUMS)

"$repo_root/eng/verify-macos-release.sh" "$output_root"
echo "$dmg"
