# Backlog

Open items only. When an item is resolved, delete it from here and add an ADR in `docs/decisions/`.

## Tidy remaining warnings and dependency advisory cleanup

Nullable-reference warnings remain in a few call sites, and `SkiaSharp` 2.88.3 has a known
high-severity advisory (GHSA-j7hp-h8jx-5ppr). Address both after core production behavior
(Phase 1/2/3 + durability matrix) remains stable. Candidate fix: bump `SkiaSharp` to a patched
version and sweep remaining nullable warnings in `YTAHD.Core`.

## Phase 4 tile-size sweep (tuning follow-up)

`MotionVectorModulator`/`MotionTileBasis`/`MotionFrameBitDecoder` (see ADR
`F-20260903-01-phase4-motion-vector-design.md`, `Status: Implemented`) ship a fixed 8×8
texture. The originally planned 4×4/16×16 tile-size sweep was deferred rather than blocking
the A5 real-codec exit gate: parameterizing `MotionTileBasis`'s currently-hardcoded
texture-size constants is a refactor across already-tested code, not yet done. Not started.

## Phase 4 follow-up research (motion as sync clock, not carrier)

Tracked from `docs/PLAN.md` stage C2: motion-synced Phase 3 (sparse always-moving marker
tiles as a per-frame clock enabling a 1-frame DCT lifespan — the highest-throughput
configuration analysed for Phase 4), sub-pixel offset alphabet (½-px steps via phase
correlation), delta-chained offsets (previous-frame-relative, 1-frame lifespan). Stage C1
(combined clock arbitration) has landed; not started.

## Combined clock: multi-frame-loss repair (follow-up)

Stage C1 (clock arbitration) is implemented: `DecodeMetrics.HasAudioVideoDatagramMismatch()`
detects when the audio FSK clock's datagram count disagrees with what the video pipeline
actually reconstructed (e.g. a whole parity group silently dropped, which XOR-parity alone
cannot catch) — see ADR `F-20260903-02-audio-fsk-clock-design.md`. This is detection only.
Actually _repairing_ such a loss — most plausibly by wiring the standalone durability-matrix
codec in as a stronger multi-erasure repair path once a mismatch is flagged — is a deeper
integration, deferred and not yet started.

## Phase 4 / audio FSK cadence conflict (follow-up)

Confirmed during audio FSK validation: Phase 4's 2-physical-frame-per-datagram cadence (see
ADR `F-20260903-01-phase4-motion-vector-design.md`) leaves no room for a hold segment between
pulses at the standard pulse duration, so only the first datagram boundary in a stream is
audio-detectable (back-to-back pulses merge into one continuous tone). Needs either a shorter
FSK pulse duration or a wider Phase 4 cadence to combine usefully — tracked as part of
`docs/PLAN.md` stage C1. Not started.

## GPU acceleration: codec option model and CLI surface

Tracked by ADR `CR-20260911-03-gpu-acceleration-plan.md` and `docs/PLAN.md` workstream D.
Add typed app options and CLI flags for selecting FFmpeg video encoders and decode-side
hardware acceleration while preserving CPU `libx264` as the default. Include validation for
unsupported option combinations. Not started.

## GPU acceleration: FFmpeg argument generation

Move encoder-specific FFmpeg arguments into a small owned abstraction near `FFmpegWrapper`.
Implement the CPU baseline plus opt-in NVIDIA `h264_nvenc` first, then add Intel `h264_qsv`
and AMD `h264_amf` profiles after capability checks exist. Not started.

## GPU acceleration: hardware capability detection

Add probing for `ffmpeg -encoders` and `ffmpeg -hwaccels` so the CLI can fail fast when the
requested GPU encoder or hwaccel mode is unavailable in the user's FFmpeg build or driver
stack. Surface actionable diagnostics instead of raw FFmpeg startup failures. Not started.

## GPU acceleration: real-codec durability validation

Build a real FFmpeg validation matrix for GPU encoders across phase1, phase2, phase3, and
phase4 payload recovery. Measure speed and output size against the CPU `libx264` baseline;
keep any unreliable GPU/modulator pair experimental until it matches the durability bar. Not
started.

## GPU acceleration: release docs and troubleshooting

Document GPU prerequisites, CLI examples, FFmpeg build requirements, driver/runtime caveats,
and common errors such as missing encoders, unsupported pixel formats, and unavailable GPU
devices. Update release notes once GPU flags ship. Not started.

## Integrity: strict per-frame SHA validation

Tracked by ADR `CR-20260911-05-integrity-verification-plan.md` and `docs/PLAN.md` workstream
E. Enforce the existing per-frame SHA-256 field during packet decode for data and parity
frames. Reject packets with mismatched payload hashes, while preserving v1 oversized-frame
recovery by accepting only wrapped length candidates whose payload hash matches. Not started.

## Integrity: stream manifest packet

Add a protocol manifest frame containing protocol version, total payload bytes, full payload
SHA-256, modulator identity, geometry, data frame count, and durability/parity settings. Emit
manifest data redundantly so decode can recover integrity metadata under frame loss. Not
started.

## Integrity: final payload hash verification

After assembling recovered bytes, compute SHA-256 over the full payload and compare it with
the manifest hash. Surface `integrity=passed` or a loud decode failure in CLI output; legacy
streams without a manifest should report frame-only integrity. Not started.

## Integrity: corruption diagnostics and metrics

Extend `DecodeMetrics` and CLI summary output with counts/status for frame hash mismatches,
manifest packets seen, manifest conflicts, final hash verification, and legacy frame-only
mode. Error messages should distinguish packet corruption from missing frames and final
payload mismatch. Not started.

## Integrity: legacy and real-codec validation matrix

Validate strict frame hashes and whole-payload manifest checks against real FFmpeg output for
phase1, phase2, phase3, and phase4. Include the existing oversized v1 Phase 3 recovery case,
new v2 high-capacity Phase 3 encodes, deliberate frame corruption, and missing-manifest
legacy behavior. Not started.

## Parallel pipeline: degree-of-parallelism controls

Phase 3 render/decode and Phase 4 motion tile search are parallelized (see ADRs
`CR-20260911-09-phase3-parallel-render.md`, `CR-20260911-10-phase3-parallel-decode.md`, and
`CR-20260911-11-phase4-parallel-tile-search.md`). Add conservative degree-of-parallelism
controls and keep a serial fallback for deterministic debugging and low-core machines. Not
started.

## Parallel pipeline: bounded ordered encode pipeline

Split encode into packet producer, bounded render workers, and a single ordered FFmpeg writer.
Maintain logical frame order, repeats, Phase 4 canonical separators, audio-clock cadence, and
bounded memory for 4K frame buffers. Not started.

## Parallel pipeline: bounded ordered decode pipeline

Split decode into sequential FFmpeg frame reader, bounded packet decode workers, and ordered
aggregator. Keep duplicate-run tracking, canonical frame handling, parity recovery, and output
assembly ordered. Not started.

## Parallel pipeline: performance validation matrix

Add serial-vs-parallel benchmarks by modulator, payload size, resolution, and encoder path.
Track throughput, CPU utilization, peak memory, FFmpeg wait time, and byte-for-byte recovery.
Use results to tune default concurrency. Not started.
