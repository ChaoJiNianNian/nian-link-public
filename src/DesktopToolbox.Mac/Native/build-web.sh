#!/bin/sh
# Build the owned WebKit bridge; no user screen, clipboard or existing app is accessed.
set -eu
web_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
output=${1:-"$web_dir/../Assets/libDeskLinkWeb.dylib"}
xcrun swiftc -emit-library -parse-as-library -O -target arm64-apple-macosx14.0 \
  -framework AppKit -framework WebKit -o "$output" "$web_dir/DeskLinkWeb.swift"
