# Picer WebRTC AEC3 native bridge

This directory contains the small, versioned C ABI used by the WPF client. The
build script copies the bridge into a separately checked-out WebRTC tree and
builds `picer_aec3.dll`; WebRTC itself is not vendored into this repository.

The bridge accepts mono signed 16-bit PCM in exact 10 ms frames. Render audio
must be submitted before the corresponding capture frame. The client loads the
DLL opportunistically and retains its playback-aware fallback if the runtime
component is unavailable.
