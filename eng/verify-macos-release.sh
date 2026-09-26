#!/bin/sh
set -eu

# @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#distribution

release_root=${1:?release root is required}
repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
app_root="$release_root/isTranscribe.app"
manifest="$release_root/release-manifest.json"

test -d "$app_root/Contents/MacOS"
test -f "$manifest"
test "$(plutil -extract CFBundleIdentifier raw "$app_root/Contents/Info.plist")" = "com.istranscribe.app"
test "$(plutil -extract LSMinimumSystemVersion raw "$app_root/Contents/Info.plist")" = "14.2"
# The early per-user coordinator must receive a real secondary launch so it can
# activate a hidden primary window. LaunchServices-level prohibition bypasses it.
if plutil -extract LSMultipleInstancesProhibited raw "$app_root/Contents/Info.plist" >/dev/null 2>&1; then
  test "$(plutil -extract LSMultipleInstancesProhibited raw "$app_root/Contents/Info.plist")" != "true"
fi
test "$(jq -r '.targetRid' "$manifest")" = "osx-arm64"
test "$(jq -r '.architecture' "$manifest")" = "arm64"
test "$(jq -r '.localRuntimeInventoryStage' "$manifest")" = "verified-before-mach-o-rewrite-and-signing"
test -x "$app_root/Contents/MacOS/IsTranscribe.Transcription.Worker"
test -f "$app_root/Contents/MacOS/native/istranscribe_whisper_v1.dylib"
test -f "$app_root/Contents/MacOS/speaker-licenses/SOURCE-AND-NOTICES.md"
codesign --verify --deep --strict "$app_root" >/dev/null 2>&1

expected=$(mktemp)
actual=$(mktemp)
trap 'rm -f "$expected" "$actual"' EXIT
jq -r '.files[].path' "$manifest" | LC_ALL=C sort > "$expected"
find "$app_root" -type f -print | sed "s#^$app_root/##" | LC_ALL=C sort > "$actual"
cmp "$expected" "$actual"

jq -c '.files[]' "$manifest" | while IFS= read -r entry; do
  relative=$(printf '%s' "$entry" | jq -r '.path')
  case "$relative" in /*|*../*) echo "Unsafe manifest path: $relative" >&2; exit 3;; esac
  file="$app_root/$relative"
  test -f "$file"
  expected_sha=$(printf '%s' "$entry" | jq -r '.sha256')
  actual_sha=$(shasum -a 256 "$file" | awk '{print $1}')
  test "$expected_sha" = "$actual_sha"
done

allowed="$repo_root/packaging/macos/native-library-allowlist.txt"
found="$release_root/native-found.txt"
find "$app_root/Contents/MacOS" -maxdepth 1 -type f -name '*.dylib' -exec basename {} \; | LC_ALL=C sort > "$found"
cmp "$allowed" "$found"
rm "$found"
test "$(jq -r '.nativeLibraries[]' "$manifest" | LC_ALL=C sort)" = "$(cat "$allowed")"

for native in "$app_root/Contents/MacOS/IsTranscribe.App.MacOS" \
              "$app_root/Contents/MacOS/IsTranscribe.Transcription.Worker" \
              "$app_root/Contents/MacOS/createdump" \
              "$app_root"/Contents/MacOS/native/*.dylib \
              "$app_root"/Contents/MacOS/*.dylib; do
  test "$(lipo -archs "$native")" = "arm64"
  # The main executable's signature belongs to the enclosing .app and seals its
  # resources, so it is verified through the bundle check above. Other native
  # images remain independently verifiable.
  if test "$(basename "$native")" != "IsTranscribe.App.MacOS"; then
    codesign --verify "$native" >/dev/null 2>&1
  fi
  otool -L "$native" | tail -n +2 | awk '{print $1}' | while IFS= read -r dependency; do
    case "$dependency" in
      /System/Library/*|/usr/lib/*|@loader_path/*|@rpath/*|@executable_path/*) ;;
      *) echo "Non-app-relative native dependency in $native: $dependency" >&2; exit 4 ;;
    esac
  done
  otool -l "$native" | awk '$1=="cmd" && $2=="LC_RPATH"{found=1;next} found && $1=="path"{print $2;found=0}' \
    | while IFS= read -r runpath; do
        case "$runpath" in @loader_path*|@executable_path*) ;; *) echo "Unsafe native runpath in $native: $runpath" >&2; exit 4;; esac
      done
done

notice="$app_root/Contents/Resources/THIRD-PARTY-NOTICES.txt"
inventory="$app_root/Contents/Resources/third-party-dependency-inventory.json"
test -f "$notice"
test -f "$inventory"
test "$(jq -r '.notices' "$manifest")" = "Contents/Resources/THIRD-PARTY-NOTICES.txt"
test "$(jq -r '.dependencyInventory' "$manifest")" = "Contents/Resources/third-party-dependency-inventory.json"
test "$(jq -r '.schemaVersion' "$inventory")" = "infra-010-macos-dependencies-v1"
test "$(jq -r '.redistributedPackageCount' "$inventory")" = "$(jq -r '.packages | length' "$repo_root/packaging/macos/third-party-notice-policy.json")"
test "$(jq -r '.notice.sha256' "$inventory")" = "$(shasum -a 256 "$notice" | awk '{print $1}')"
test "$(jq -r '.notice.policySha256' "$inventory")" = "$(shasum -a 256 "$repo_root/packaging/macos/third-party-notice-policy.json" | awk '{print $1}')"
test -f "$app_root/Contents/Resources/third-party/lame-3.100.tar.xz"
test -f "$app_root/Contents/Resources/third-party/LAME-COPYING.LGPL-2.1.txt"
test -L "$release_root/dmg-root/Applications"
test "$(readlink "$release_root/dmg-root/Applications")" = "/Applications"
test -f "$release_root/dmg-root/FIRST-LAUNCH.txt"
test -n "$(find "$release_root" -maxdepth 1 -name '*.dmg' -print -quit)"
dmg=$(find "$release_root" -maxdepth 1 -name '*.dmg' -print -quit)
hdiutil imageinfo "$dmg" >/dev/null
(cd "$release_root" && shasum -a 256 -c SHA256SUMS >/dev/null)
