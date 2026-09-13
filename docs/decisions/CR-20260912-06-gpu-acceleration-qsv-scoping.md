# CR-20260912-06 — GPU Acceleration Scoped to QSV (Hardware Evidence)

**Date:** 2026-09-12 **Status:** Implemented (2026-09-13)
**Area:** `YTAHD.Core/Application` (`VideoCodecOptions`, `EncodeOptions`, `DecodeOptions`),
`YTAHD.Core/Infrastructure` (`FFmpegWrapper`, `FFmpegCapabilities`), `YTAHD.Cli`,
`YTAHD.Tests`

## Context

Workstream D (`CR-20260911-03`, `Accepted`) plans opt-in GPU acceleration behind typed options.
Before implementing, the local hardware and FFmpeg build were probed:

- **NVENC (NVIDIA GTX 1060):** the build ships `h264_nvenc`, but a real encode fails with
  `OpenEncodeSessionEx failed: unsupported device` — the NVENC session is unavailable on this
  driver/build. NVENC cannot be validated here.
- **QSV (Intel UHD 630):** `h264_qsv` works. A 30-frame 640x480 encode completed in 367 ms vs
  616 ms for `libx264` (~1.7x faster on a tiny sample).
- **AMF:** encoder present in the build, but no AMD GPU exists on this machine.
- hwaccels present: cuda, qsv, d3d11va, amf, vulkan, and others.

This matches the original plan's intent: _"Intel/AMD profiles remain opt-in after capability
detection confirms support."_ The evidence scopes the first production target to QSV.

## Decision

Implement D1–D5 in stages, one commit with its ADR per stage, with QSV as the only
hardware-validated profile:

**Stage 1 — D1, codec option model and CLI surface.** Typed options instead of stringly-typed
argument injection: `VideoEncoder` (`libx264` default, `h264_qsv`; `h264_nvenc`/`h264_amf`
declared but experimental) and `HardwareAcceleration` (`none` default, `qsv`). CLI flags
`--video-encoder` and `--hwaccel` on encode; defaults preserved exactly. Invalid combinations
rejected at option-parse time.

**Stage 2 — D2, FFmpeg argument generation.** Encoder-specific arguments move into a small
owned abstraction near `FFmpegWrapper` (`FFmpegEncoderArguments`). CPU baseline keeps the
current `libx264` arguments; the QSV profile uses `-c:v h264_qsv -global_quality 23
-pix_fmt yuv420p`, matched as closely as possible to the CPU CRF-23 durability baseline.

**Stage 3 — D3, capability detection and diagnostics.** A probe helper checks
`ffmpeg -encoders` / `ffmpeg -hwaccels` before a long encode/decode and fails fast with an
actionable message when the requested encoder is missing or the device is unavailable (the
NVENC `OpenEncodeSessionEx` failure is the motivating example). CLI output shows the selected
encoder/hwaccel and whether GPU support was detected.

**Stage 4 — D4, real-codec durability validation (QSV only).** Every modulator (phase1–4)
must round-trip through a real QSV encode with byte-exact payload recovery and
`integrity=passed` (the Workstream E machinery is the durability bar). Speed and output size
are compared against the CPU baseline. NVENC/AMF stay documented as experimental/unavailable
on this machine.

**Stage 5 — D5, documentation.** GPU prerequisites, CLI examples, and a troubleshooting
section for the common failures (unknown encoder, missing device, unsupported pixel format).

## Consequences

- Users with Intel Quick Sync can trade a larger hardware-compatibility surface for faster
  encodes; the default release behavior remains CPU `libx264` and is unchanged.
- Every supported encoder/modulator combination must pass the real-codec durability bar before
  being called production-ready; QSV is the only profile that can be validated on this machine.
- NVENC and AMF remain declared-but-experimental: selecting them fails fast with actionable
  diagnostics rather than a raw FFmpeg startup error.
- Decode-side hwaccel is exposed for experiments only; raw RGB24 must still reach the decoder,
  so decode gains are expected to be smaller than encode gains.

## Implementation outcomes (2026-09-13)

- D1–D3 landed as designed: option model + CLI flags, `FFmpegEncoderArguments`,
  `FFmpegCapabilities` probe with fail-fast diagnostics, with unit tests for each stage.
- D4 initially failed for phase4 with `IntegrityStatus=FrameOnly`. Root cause was
  codec-independent (reproduced through the fake FFmpeg wrapper): the serialized stream
  manifest (95 bytes) exceeds phase4's per-frame payload capacity (88 bytes), so both
  manifest copies were truncated and rejected at packet decode. Data and parity symbols are
  32 bytes and fit; that is why only phase4 lost the manifest.
- Fix: manifest chunking. `DurabilityTransportCodec.EncodeToFramePackets` takes
  `maxPayloadBytesPerFrame` and splits the serialized manifest across manifest frames;
  chunk index/count ride in the otherwise-unused frameIndex/groupCount header fields, so
  single-chunk streams stay byte-identical with the pre-chunking wire format. Decode
  reassembles chunks per stream copy (start copy = manifest frames before any data/parity
  frame) so a conflicting end copy still fails loudly at reconciliation.
- This fix also removed the last real-world defect for tiny payloads: a 64-byte payload is
  one partial final group (2 of 4 symbols); combined with the earlier partial-group
  recovery fix (real `GroupSize` carried on symbols instead of parity SymbolId), phase4 now
  round-trips 64 bytes through real QSV with `integrity=passed`.
- Validation: full suite 287/287 green (incl. 5 QSV real-codec cases: phase1/256 B,
  phase2/256 B, phase3/128 B, phase4/64 B — all `integrity=passed`, payload SHA verified).
