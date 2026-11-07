#!/bin/sh
# Rebuilds shell/native/linux-x64/Photino.Native.so (Photino's native window library, Apache-2.0).
# The official package binary needs glibc 2.38 (Ubuntu 24.04+). Building it on an older system (Ubuntu 22.04)
# gives a library that runs on 22.04, 24.04 and newer, against WebKitGTK 4.1.
# Run on Ubuntu 22.04:  sh build-photino-native.sh
set -e
sudo apt install -y build-essential git pkg-config libgtk-3-dev libwebkit2gtk-4.1-dev libnotify-dev
OUT="$(cd "$(dirname "$0")" && pwd)/linux-x64"
WORK="$(mktemp -d)"
git clone --branch v4.0.22 --depth 1 https://github.com/tryphotino/photino.Native "$WORK/src"
cd "$WORK/src/Photino.Native"
g++ -std=c++11 -shared -fPIC -DOS_LINUX Photino.Linux.Dialog.cpp Photino.Linux.cpp Exports.cpp \
  -o "$OUT/Photino.Native.so" $(pkg-config --cflags --libs gtk+-3.0 webkit2gtk-4.1 libnotify)
echo "Built $OUT/Photino.Native.so"
rm -rf "$WORK"
