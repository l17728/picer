# Picer WebRTC AEC3 native bridge

This directory contains the small, versioned C ABI used by the WPF client. The
build script copies the bridge into a separately checked-out WebRTC tree and
builds `picer_aec3.dll`; WebRTC itself is not vendored into this repository.

The bridge accepts mono signed 16-bit PCM in exact 10 ms frames. Render audio
must be submitted before the corresponding capture frame. The client loads the
DLL opportunistically and retains its playback-aware fallback if the runtime
component is unavailable.

The bridge implementation and optional runtime loading do not establish room
echo-cancellation quality. The real microphone/speaker, ten-minute full-duplex
acceptance remains paused in the [current development plan](../../docs/DEVELOPMENT_PLAN.md).
See the [historical AEC3 implementation](../../docs/FULL_DUPLEX_AEC3_IMPLEMENTATION_2026-09-15.md)
for its original build and test evidence; do not treat those results as a new
hardware acceptance. Current release/runtime installation instructions are in
the [release guide](../../docs/RELEASE_GUIDE.md).
