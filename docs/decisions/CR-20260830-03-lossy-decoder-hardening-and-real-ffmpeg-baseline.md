# CR-20260830-03 — Lossy H.264 Decoder Hardening and Real-FFmpeg Baseline

**Date:** 2026-08-30 **Status:** Implemented
**Area:** `YTAHD.Core.Core.DecoderEngine`, `BinaryGridModulator`, CLI, `README.md`

## Context

Real `libx264` output is lossy: a single hard black/white threshold is not reliable, frame
geometry must match the actual decoded RGB stream (not the in-memory RGBA layout), and
short videos caused unreliable `ffprobe` frame counts. Several related fixes and the
explicit ffmpeg-path override were delivered together as the real-codec baseline hardened.

## Decision

- Made the Phase 1 decoder score multiple luminance candidates instead of assuming a single
  threshold under real `libx264` output (formerly BUG-002, "harden" instance).
- Normalized the Phase 1 binary modulator contract to a consistent 16×16 macroblock layout
  shared by encoder and decoder (formerly BUG-003).
- Finalized the real-FFmpeg-based Phase 1 pipeline: corrected ffmpeg path/timeout wiring and
  lossy decoder semantics (formerly BUG-004).
- Added an explicit real-ffmpeg regression test suite for H.264 smoke validation (formerly
  FEAT-035, "regression suite" instance).
- Hardened the lossy decoder with quality-aware packet filtering and duplicate-run scoring
  so the strongest valid member of a repeated run is preferred (formerly FEAT-036, "quality
  scoring" instance).
- Documented the H.264 baseline and ffmpeg override behavior (`--ffmpeg-path`, PATH
  fallback) in the CLI and `README.md` (formerly FEAT-035 "ffmpeg path override" instance,
  and FEAT-037 "doc" instance).
- Added real FFmpeg payload/frame telemetry to encode/decode CLI summaries: payload size,
  per-frame capacity, frames written, actual `ffprobe` frame count, decode totals (formerly
  FEAT-037 "telemetry" instance).
- Fixed short-video `ffprobe` reliability via a shared FFprobe helper used by CLI and encoder
  (formerly FEAT-038 "ffprobe" instance).
- Consolidated codec configuration into `VideoCodecOptions` and shared `ModulatorGeometry`
  (formerly FEAT-036 "config consolidation" instance).
- Added a real-loss regression proving the decoder picks the strongest valid frame from a
  lossy duplicate run (formerly BUG-005).
- Froze the Phase 1 H.264 baseline as the stable production contract that later modulation
  work is validated against (formerly FEAT-040 "freeze baseline" instance).
- Kept `TODO.md` aligned with the real production codec contract as these fixes landed
  (formerly CHORE-009, first instance).

## Consequences

Phase 1 became fully real-FFmpeg-validated: CLI and service-level encode/decode checks pass
against actual `libx264` output, and this baseline is the contract every later modulator
(Phase 2, Phase 3) must not regress.
