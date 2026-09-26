#!/bin/sh
set -eu

# @spec spec://modules/platform/INFRA-005.C-deterministic-third-party-notices#decisions.payload
# @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#distribution

publish_root=${1:?publish root is required}
notice_output=${2:?notice output path is required}
inventory_output=${3:?inventory output path is required}
repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
policy=${4:-"$repo_root/packaging/macos/third-party-notice-policy.json"}
deps="$publish_root/IsTranscribe.App.MacOS.deps.json"
worker_deps="$publish_root/IsTranscribe.Transcription.Worker.deps.json"

test -f "$deps"
test -f "$worker_deps"
test -f "$policy"
test "$(jq -r '.schemaVersion' "$policy")" = "infra-010-macos-notices-v1"

scratch=$(mktemp -d "${TMPDIR:-/tmp}/istranscribe-macos-notices.XXXXXX")
cleanup() { rm -rf "$scratch"; }
trap cleanup EXIT HUP INT TERM
actual_lines="$scratch/actual.jsonl"
: > "$actual_lines"

jq -sc '
  map(
  . as $deps
  | .targets[.runtimeTarget.name]
  | to_entries[]
  | .key as $rawIdentity
  | ($deps.libraries[$rawIdentity] // {}) as $library
  | select($library.type == "package" or $library.type == "runtimepack")
  | select($rawIdentity | startswith("IsTranscribe.") | not)
  | {
      identity: ($rawIdentity | sub("^runtimepack\\."; "")),
      contentHashSha512: (($library.sha512 // "") | sub("^sha512-"; "")),
      assets: (
        ([((.value.runtime // {}) | keys[]) | {source: ., resource: false}]
        + [((.value.native // {}) | keys[]) | {source: ., resource: false}]
        + [((.value.resources // {}) | keys[]) | {source: ., resource: true}])
        | map(select(.source | test("\\.(pdb|xml)$"; "i") | not))
        | map(. + {path: (if .resource then (.source | split("/") | .[-2:] | join("/")) else (.source | split("/") | last) end)})
        | unique_by(.path)
        | sort_by(.path)
      )
    }
  | select(.assets | length > 0)
  ) | group_by(.identity)[]
  | if (map(.contentHashSha512) | unique | length) != 1 then error("Conflicting dependency content hashes") else . end
  | {identity: .[0].identity, contentHashSha512: .[0].contentHashSha512, assets: ([.[].assets[]] | unique_by(.path) | sort_by(.path))}
' "$deps" "$worker_deps" | while IFS= read -r entry; do
  identity=$(printf '%s' "$entry" | jq -r '.identity')
  content_hash=$(printf '%s' "$entry" | jq -r '.contentHashSha512')
  assets_file="$scratch/assets"
  printf '%s' "$entry" | jq -r '.assets[].path' | LC_ALL=C sort -u > "$assets_file"
  asset_list_hash=$(awk '{printf "%s%s", separator, $0; separator="\n"}' "$assets_file" | shasum -a 256 | awk '{print $1}')
  tree_file="$scratch/tree"
  : > "$tree_file"
  while IFS= read -r asset; do
    case "$asset" in /*|*../*) echo "Unsafe attributed asset for $identity: $asset" >&2; exit 3;; esac
    test -f "$publish_root/$asset" || { echo "Attributed asset is missing for $identity: $asset" >&2; exit 3; }
    printf '%s\n%s\n' "$asset" "$(shasum -a 256 "$publish_root/$asset" | awk '{print $1}')" >> "$tree_file"
  done < "$assets_file"
  tree_hash=$(shasum -a 256 "$tree_file" | awk '{print $1}')
  assets=$(jq -R -s 'split("\n") | map(select(length > 0))' "$assets_file")
  jq -cn --arg identity "$identity" --arg contentHashSha512 "$content_hash" \
    --arg assetListSha256 "$asset_list_hash" --arg payloadTreeSha256 "$tree_hash" \
    --argjson assets "$assets" \
    '{identity:$identity,contentHashSha512:$contentHashSha512,assetListSha256:$assetListSha256,payloadTreeSha256:$payloadTreeSha256,assets:$assets}' \
    >> "$actual_lines"
done

jq -s 'sort_by(.identity)' "$actual_lines" > "$scratch/actual.json"
jq -S '[.packages[] | {identity,contentHashSha512,assetListSha256,payloadTreeSha256}] | sort_by(.identity)' "$policy" > "$scratch/expected.json"
jq -S '[.[] | {identity,contentHashSha512,assetListSha256,payloadTreeSha256}] | sort_by(.identity)' "$scratch/actual.json" > "$scratch/comparable.json"
if ! cmp "$scratch/expected.json" "$scratch/comparable.json"; then
  echo "macOS redistributed dependency inventory changed; review and update the pinned notice policy." >&2
  jq -S '.' "$scratch/actual.json" >&2
  exit 4
fi

for license in $(jq -r '[.packages[].licenses[]] | unique | sort[]' "$policy"); do
  case "$license" in
    MIT) license_file="$repo_root/packaging/windows/notices/licenses/MIT.txt" ;;
    Apache-2.0) license_file="$repo_root/packaging/windows/notices/licenses/Apache-2.0.txt" ;;
    OFL-1.1) license_file="$repo_root/packaging/windows/notices/licenses/OFL-1.1.txt" ;;
    SourceGear-SQLite) license_file="$repo_root/packaging/macos/licenses/SourceGear-SQLite.txt" ;;
    *) echo "Unknown approved license material: $license" >&2; exit 5 ;;
  esac
  expected=$(jq -r --arg license "$license" '.licenseMaterials[$license].sha256 // empty' "$policy")
  test -n "$expected"
  test "$(shasum -a 256 "$license_file" | awk '{print $1}')" = "$expected"
done

{
  printf 'isTranscribe third-party notices\n\n'
  printf 'Generated from the exact osx-arm64 published dependency graph and reviewed offline policy.\n\n'
  printf 'Package attribution index\n'
  jq -r '.packages | sort_by(.identity)[] | "- \(.identity) | \(.attribution) | licenses: \(.licenses | join(", "))"' "$policy"
  jq -r '.nativeComponents | sort_by(.identity)[] | "- \(.identity) | \(.attribution) | licenses: \(.licenses | join(", ")) | native files: \([.files[].name] | join(", "))"' "$policy"
  printf '\n- LAME/3.100 | The LAME Project | license: LGPL-2.1; dynamically linked as libmp3lame.0.dylib\n'
  printf '\nFull approved license material\n'
  for license in $(jq -r '[.packages[].licenses[]] | unique | sort[]' "$policy"); do
    case "$license" in
      MIT) license_file="$repo_root/packaging/windows/notices/licenses/MIT.txt" ;;
      Apache-2.0) license_file="$repo_root/packaging/windows/notices/licenses/Apache-2.0.txt" ;;
      OFL-1.1) license_file="$repo_root/packaging/windows/notices/licenses/OFL-1.1.txt" ;;
      SourceGear-SQLite) license_file="$repo_root/packaging/macos/licenses/SourceGear-SQLite.txt" ;;
    esac
    printf '\n================================================================================\n\n%s\n\n' "$license"
    sed 's/\r$//' "$license_file"
  done
  printf '\n================================================================================\n\nLGPL-2.1 / LAME 3.100\n\n'
  sed 's/\r$//' "$publish_root/third-party/LAME-COPYING.LGPL-2.1.txt"
} > "$notice_output"

notice_sha=$(shasum -a 256 "$notice_output" | awk '{print $1}')
policy_sha=$(shasum -a 256 "$policy" | awk '{print $1}')
jq -n --arg schemaVersion "infra-010-macos-dependencies-v1" \
  --arg noticeFile "$(basename "$notice_output")" --arg noticeSha256 "$notice_sha" \
  --arg policySha256 "$policy_sha" --argjson packages "$(cat "$scratch/actual.json")" \
  --argjson nativeComponents "$(jq '.nativeComponents' "$policy")" \
  '{schemaVersion:$schemaVersion,notice:{file:$noticeFile,sha256:$noticeSha256,policySha256:$policySha256},redistributedPackageCount:($packages|length),packages:$packages,nativeComponents:$nativeComponents}' \
  > "$inventory_output"
