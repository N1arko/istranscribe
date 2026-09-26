#!/bin/sh
set -eu

# @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#distribution

macos_root=${1:?app Contents/MacOS root is required}
repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
policy=${2:-"$repo_root/packaging/macos/third-party-notice-policy.json"}
scratch=$(mktemp -d "${TMPDIR:-/tmp}/istranscribe-native-bundle.XXXXXX")
cleanup() { rm -rf "$scratch"; }
trap cleanup EXIT HUP INT TERM
sources="$scratch/sources.jsonl"
: > "$sources"

while :; do
  copied=0
  for binary in "$macos_root/IsTranscribe.App.MacOS" "$macos_root/createdump" "$macos_root"/*.dylib; do
    test -f "$binary" || continue
    binary_id=$(otool -D "$binary" 2>/dev/null | tail -n +2 | head -1 || true)
    otool -L "$binary" | tail -n +2 | awk '{print $1}' > "$scratch/dependencies"
    while IFS= read -r dependency; do
      test "$dependency" = "$binary_id" && continue
      case "$dependency" in
        /System/Library/*|/usr/lib/*|@*) continue ;;
        /*)
          name=$(basename "$dependency")
          if test ! -f "$macos_root/$name"; then
            test -f "$dependency" || { echo "External native dependency is missing: $dependency" >&2; exit 3; }
            cp "$dependency" "$macos_root/$name"
            jq -cn --arg name "$name" --arg sha256 "$(shasum -a 256 "$dependency" | awk '{print $1}')" \
              '{name:$name,sha256:$sha256}' >> "$sources"
            source_directory=$(dirname "$dependency")
            otool -L "$dependency" | tail -n +2 | awk '{print $1}' | while IFS= read -r nested; do
              case "$nested" in
                @rpath/*|@loader_path/*)
                  nested_name=$(basename "$nested")
                  nested_source="$source_directory/$nested_name"
                  if test ! -f "$macos_root/$nested_name" && test -f "$nested_source"; then
                    cp "$nested_source" "$macos_root/$nested_name"
                    jq -cn --arg name "$nested_name" --arg sha256 "$(shasum -a 256 "$nested_source" | awk '{print $1}')" \
                      '{name:$name,sha256:$sha256}' >> "$sources"
                  fi
                  ;;
              esac
            done
            printf 'copied\n' > "$scratch/copied"
          fi
          ;;
      esac
    done < "$scratch/dependencies"
  done
  if test -f "$scratch/copied"; then
    rm "$scratch/copied"
    copied=1
  fi
  test "$copied" -eq 1 || break
done

jq -s 'unique_by(.name) | sort_by(.name)' "$sources" > "$scratch/actual.json"
jq -S '[.nativeComponents[].files[]] | sort_by(.name)' "$policy" > "$scratch/expected.json"
jq -S '.' "$scratch/actual.json" > "$scratch/actual-sorted.json"
if ! cmp "$scratch/expected.json" "$scratch/actual-sorted.json"; then
  echo "External macOS native dependency inventory changed; review the pinned notice policy." >&2
  cat "$scratch/actual-sorted.json" >&2
  exit 4
fi

for binary in "$macos_root/IsTranscribe.App.MacOS" "$macos_root/createdump" "$macos_root"/*.dylib; do
  test -f "$binary" || continue
  codesign --remove-signature "$binary" 2>/dev/null || true
done

for dylib in "$macos_root"/*.dylib; do
  install_name_tool -id "@rpath/$(basename "$dylib")" "$dylib"
done

for binary in "$macos_root/IsTranscribe.App.MacOS" "$macos_root/createdump" "$macos_root"/*.dylib; do
  test -f "$binary" || continue
  otool -L "$binary" | tail -n +2 | awk '{print $1}' | while IFS= read -r dependency; do
    name=$(basename "$dependency")
    if test -f "$macos_root/$name"; then
      install_name_tool -change "$dependency" "@loader_path/$name" "$binary"
    fi
  done
done

for binary in "$macos_root/IsTranscribe.App.MacOS" "$macos_root/createdump" "$macos_root"/*.dylib; do
  test -f "$binary" || continue
  codesign --force --sign - "$binary"
done
