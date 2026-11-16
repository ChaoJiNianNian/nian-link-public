#!/bin/sh
# Run on an Apple Silicon Mac with Xcode Command Line Tools. The self-test
# uses a named synthetic pasteboard and never reads the screen or general clipboard.
set -eu
media_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
output=${1:-"$media_dir/../Assets/DeskLinkMedia"}
xcrun swiftc -parse-as-library -O -target arm64-apple-macosx14.0 \
  -framework AppKit -framework ScreenCaptureKit -framework ImageIO \
  -framework CoreImage -framework CoreMedia -framework CoreVideo \
  -o "$output" "$media_dir/DeskLinkMedia.swift" \
  "$media_dir/RealtimeCapture.swift" "$media_dir/RealtimeCaptureOutput.swift"
"$output" --self-test
