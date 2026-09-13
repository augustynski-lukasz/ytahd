# CR-20260911-03 - GPU Acceleration Plan

**Date:** 2026-09-11 **Status:** Superseded by `CR-20260912-06-gpu-acceleration-qsv-scoping.md` (Implemented)
**Area:** FFmpeg wrapper, CLI options, decode pipeline, release documentation

## Context

YTAHD currently uses FFmpeg's CPU `libx264` encoder and no decode-side hardware acceleration.
That path is the real-codec production baseline for payload durability, but large 4K/60 encode
jobs can be slow. FFmpeg can use GPU encoders such as NVIDIA NVENC, Intel Quick Sync, and AMD
AMF when the user's FFmpeg build and drivers support them.

GPU acceleration must be opt-in because hardware encoders can produce different compression
artifacts, rate-control behavior, GOP decisions, and pixel-format constraints. For this
project, successful process execution is not enough: payload recovery after a real encode and
decode round trip is the compatibility bar.

## Decision

Plan GPU acceleration as an optional FFmpeg capability layered behind typed app options and
CLI flags. Keep CPU `libx264` as the default. Add explicit encoder selection first
(`libx264`, `h264_nvenc`, `h264_qsv`, `h264_amf`), then decode-side hwaccel selection
(`none`, `cuda`, `qsv`, `d3d11va`) for experiments where it helps.

Encoder-specific FFmpeg argument generation will live near the FFmpeg wrapper rather than in
the CLI. The implementation will include hardware capability probes based on `ffmpeg
-encoders` and `ffmpeg -hwaccels`, fast failure with actionable diagnostics, fake-wrapper
argument tests, and real FFmpeg payload-recovery validation before any GPU profile is called
production-ready.

## Consequences

Users with supported GPUs can eventually trade larger hardware-specific compatibility surface
for faster encoding. The default release behavior remains stable and CPU-based. GPU support
adds a validation burden: every supported encoder/modulator combination must be tested against
real FFmpeg output, and unreliable combinations must stay documented as experimental rather
than silently replacing the `libx264` baseline.
