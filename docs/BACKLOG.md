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
correlation), delta-chained offsets (previous-frame-relative, 1-frame lifespan). Blocked on
the audio FSK combined-clock work (stage C1) landing first. Not started.

## Audio FSK clock: erasure-recovery wiring (follow-up)

The audio FSK datagram clock is implemented and validated (ADR
`F-20260903-02-audio-fsk-clock-design.md`, `Status: Implemented`): `DecodeMetrics` exposes
`AudioDatagramCount` for comparison against the video-decoded logical frame count. Wiring an
actual disagreement into erasure indices consumed by the parity/durability layer (the
original "recover missing datagrams via the audio clock" ambition) is a deeper integration,
deferred and not yet started.

## Phase 4 / audio FSK cadence conflict (follow-up)

Confirmed during audio FSK validation: Phase 4's 2-physical-frame-per-datagram cadence (see
ADR `F-20260903-01-phase4-motion-vector-design.md`) leaves no room for a hold segment between
pulses at the standard pulse duration, so only the first datagram boundary in a stream is
audio-detectable (back-to-back pulses merge into one continuous tone). Needs either a shorter
FSK pulse duration or a wider Phase 4 cadence to combine usefully — tracked as part of
`docs/PLAN.md` stage C1. Not started.
