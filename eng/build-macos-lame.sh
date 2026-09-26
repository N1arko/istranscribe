#!/bin/sh
# @spec spec://modules/platform/INFRA-010-macos-platform-parity-and-dmg#recording
set -eu

archive=$1
destination=$2
library="$destination/lib/libmp3lame.0.dylib"

if [ -f "$library" ]; then
    exit 0
fi

mkdir -p "$destination"
work=$(mktemp -d "$destination/build.XXXXXX")
cleanup() {
    rm -rf "$work"
}
trap cleanup EXIT HUP INT TERM

tar -xJf "$archive" -C "$work"
source="$work/lame-3.100"
# LAME 3.100 marks this obsolete API static while retaining it in the exported-symbol list.
sed -i.bak '/^lame_init_old$/d' "$source/include/libmp3lame.sym"

cd "$source"
./configure \
    --disable-static \
    --enable-shared \
    --disable-frontend \
    --prefix="$destination" \
    CFLAGS="-O2 -w -arch arm64 -mmacosx-version-min=14.2"
make -j4
make install
install_name_tool -id "@rpath/libmp3lame.0.dylib" "$library"
