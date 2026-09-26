#!/bin/sh
set -eu

# @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#distribution

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
test_root=$(mktemp -d "${TMPDIR:-/tmp}/istranscribe-release-test.XXXXXX")
cleanup() { rm -rf "$test_root"; }
trap cleanup EXIT HUP INT TERM

"$repo_root/eng/build-macos-release.sh" "$test_root/first" >/dev/null
"$repo_root/eng/build-macos-release.sh" "$test_root/second" >/dev/null
jq -S 'del(.version)' "$test_root/first/release-manifest.json" > "$test_root/first.json"
jq -S 'del(.version)' "$test_root/second/release-manifest.json" > "$test_root/second.json"
cmp "$test_root/first.json" "$test_root/second.json"

cp -R "$test_root/first" "$test_root/mutated"
truncate -s 1 "$test_root/mutated/isTranscribe.app/Contents/MacOS/IsTranscribe.Core.dll"
if "$repo_root/eng/verify-macos-release.sh" "$test_root/mutated" >/dev/null 2>&1; then
  echo "Verifier accepted a mutated managed payload." >&2
  exit 5
fi

cp "$test_root/first/isTranscribe.app/Contents/MacOS/libistranscribe_audio.dylib" \
  "$test_root/first/isTranscribe.app/Contents/MacOS/libunknown.dylib"
if "$repo_root/eng/verify-macos-release.sh" "$test_root/first" >/dev/null 2>&1; then
  echo "Verifier accepted an unknown native library." >&2
  exit 6
fi

cp -R "$test_root/second/isTranscribe.app/Contents/MacOS" "$test_root/notice-audit"
mkdir -p "$test_root/notice-audit/third-party"
cp "$test_root/second/isTranscribe.app/Contents/Resources/third-party/LAME-COPYING.LGPL-2.1.txt" \
  "$test_root/notice-audit/third-party/"
jq '.targets[.runtimeTarget.name]["Unknown.Package/1.0.0"] = {runtime:{"lib/net10.0/Unknown.Package.dll":{}}} | .libraries["Unknown.Package/1.0.0"] = {type:"package",sha512:"sha512-unknown"}' \
  "$test_root/notice-audit/IsTranscribe.App.MacOS.deps.json" > "$test_root/notice-audit/deps-mutated.json"
mv "$test_root/notice-audit/deps-mutated.json" "$test_root/notice-audit/IsTranscribe.App.MacOS.deps.json"
cp "$test_root/notice-audit/IsTranscribe.Core.dll" "$test_root/notice-audit/Unknown.Package.dll"
if "$repo_root/eng/generate-macos-third-party-notices.sh" "$test_root/notice-audit" \
  "$test_root/unknown-notices.txt" "$test_root/unknown-inventory.json" >/dev/null 2>&1; then
  echo "Notice generator accepted an unknown redistributed dependency." >&2
  exit 7
fi
