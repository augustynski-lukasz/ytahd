# F-20260903-01 — Phase 4 Motion-Vector Modulation: Absolute-Displacement Design

**Date:** 2026-09-03 **Status:** Implemented
**Area:** `YTAHD.Core/Modulation` (`MotionTileBasis`, `MotionVectorModulator`,
`IFrameEmissionStrategy`), `YTAHD.Core/Core` (`MotionFrameBitDecoder`, `EncoderEngine`
emission pattern, `DecodeStreamOrchestrator` canonical-frame handling), `YTAHD.Cli`, `probe`

## Context

The README's Phase 4 concept stores data in the direction/velocity of tiles moving between
frames, decoded by optical flow. Analysis against the current architecture found:

- `IModulator` / `IFrameBitDecoder` are per-frame and stateless; an "offset relative to the
  previous frame" scheme needs temporal state on both ends and accumulates drift.
- `EncoderEngine` hard-codes 3× identical frame repetition and the decoder collapses
  duplicate runs — both assume "identical repeats = one datagram", which transitions break.
- Offset `(0,0)` cannot be a data symbol or held/duplicated frames become ambiguous.
- Density bound: one vector per tile yields ~0.03 bits/px vs Phase 3's 0.125 bits/px
  (log-growth alphabet vs quadratic guard-margin cost). Pure motion can never win on raw
  capacity; its value is compression robustness and clock/sync capability.

## Decision

Implement Phase 4 as an **absolute-displacement scheme** rather than previous-frame-relative
optical flow:

- Tile texture is deterministic (fixed protocol seed, band-limited/codec-friendly).
- The stream alternates **canonical frames** (all tiles at home positions; doubles as the
  "no data" marker) with **displaced frames** (each tile shifted by its data offset).
- Decode is single-frame: each displaced frame's tiles are matched against the known
  canonical texture via SAD full search over the finite offset alphabet
  (4 bits X + 4 bits Y, 2 px steps — above H.264 ¼-pel motion precision; `(0,0)` excluded).
- Tile texture size ships as the fixed 8×8 px reliability baseline for this implementation.
- Frame emission is modulator-driven via the new `IFrameEmissionStrategy` opt-in capability
  (canonical/displaced interleave, repeat count); Phase 1–3 keep the literal 3× behavior,
  regression-locked (140/140 suite green throughout, zero changes to their code paths).

Execution plan: `docs/PLAN.md`, workstream A (stages A1–A6).

### Real-codec validation (A5)

At the shipped 8×8 texture / 2 px-step / 16 px-guard defaults, real libx264 (CRF-23) round
trips recover 100% of the payload across single-frame and multi-frame/multi-parity-group
payloads (16/64/256 bytes), including canonical-separator detection with zero false
invalid-packet classifications. See `YTAHD.Tests/YtahdCodecServiceTests.cs`
(`RealFfmpeg_LargerPayloadMatrix_RoundTrips_For_Phase4`,
`RealFfmpeg_MotionVectorModulator_SingleFrame_RoundTrip_DoesNotHang`).

**Scope decision:** the originally planned 4×4/16×16 tile-size sweep is deferred rather than
blocking this ADR. `MotionTileBasis`/`MotionVectorModulator`/`MotionFrameBitDecoder` currently
hardcode the 8×8 texture as constants; parameterizing texture size is a real refactor across
already-tested code, and the 8×8 baseline already clears the A5 exit gate (100% recovery on a
real codec round trip). The sweep is tracked as a follow-up tuning experiment, not a
prerequisite for production use — see `docs/BACKLOG.md`.

## Consequences

- No optical-flow dependency, no drift, no reliance on previous decoded frame quality; fits
  the existing stateless decoder pipeline with modest engine changes.
- Codec still sees exactly the small inter-frame motion Phase 4 exploits (cheap P-frames).
- Raw capacity stays far below Phase 3 (~1.8–90 KB/frame depending on tile size); the
  strategic payoff is the motion channel as a per-frame sync clock — sparse marker tiles
  (~2–4% area) could enable a 1-frame DCT datagram lifespan (~7.1 MB/s theoretical vs
  ~2.47 MB/s today). That hybrid is a follow-up (PLAN.md stage C2), not part of this design.
- Delta-chained offsets (1-frame lifespan, previous-frame-relative) remain a documented
  extension once this baseline survives real libx264.
