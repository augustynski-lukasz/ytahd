# F-20260903-02 — Phase 4 tile-size sweep: parameterized tile profile

**Date:** 2026-09-13 **Status:** Implemented
**Area:** `YTAHD.Core/Modulation` (`MotionTileBasis`, `MotionTileProfile`, `MotionVectorModulator`),
`YTAHD.Core/Core` (`MotionFrameBitDecoder`, `FrameBitDecoderFactory`), `YTAHD.Tests`

## Context

Phase 4 ships a fixed 8×8 tile texture (`MotionTileBasis.TextureSize = 8`, hardcoded
constants for step/levels/guard). The originally planned 4×4/16×16 tile-size sweep
(A `F-20260903-01` scope decision) was deferred: parameterizing the constants was a real
refactor across already-tested code. Open question: which texture size survives real lossy
H.264 best, and what capacity/robustness trade-offs exist.

Capacity at 640×480/border 32 (usable 576×416, header 52 B):
texture 4 → cell 36 px → 176 cells → 124 B; texture 8 → cell 40 → 140 cells → 88 B;
texture 16 → cell 48 → 96 cells → 44 B. Smaller tiles raise capacity but shrink the
discrimination surface per symbol; larger tiles do the reverse.

## Decision

- Introduce `MotionTileProfile( textureSize, offsetStepPx, offsetLevelsPerAxis )` — a value
  object carrying the tile geometry. `MaxAbsOffsetPx` (guard) and `CellSize` are derived.
  `Default` = (8, 2, 16) — byte-identical with today's protocol.
- `MotionTileBasis` keeps its static protocol surface (seeded 8×8 texture, offset table,
  `EncodeOffset`/`TryDecodeOffset`) untouched; add profile-parameterized static builders
  (`BuildOffsetTable(profile)`, `BuildTexture(profile)`, `EncodeOffset(profile, value)`,
  `TryDecodeOffset(profile, dx, dy, out value)`) with the no-arg versions delegating to the
  default profile. Existing tests and call sites keep compiling unchanged.
- `MotionVectorModulator` and `MotionFrameBitDecoder` gain an optional
  `MotionTileProfile` constructor parameter (default = Default profile). All tile
  constants inside both classes come from the profile instead of `MotionTileBasis`
  constants. `FrameBitDecoderFactory` threads the modulator's profile into the decoder so
  encode/decode always agree.
- Evidence: a real-codec sweep test (`Phase4TileSizeSweepTests`) round-trips 4×4/8×8/16×16
  through libx264 with integrity verification, plus synthetic-degradation robustness
  comparisons (Gaussian noise / blur / luma shift) per profile.

## Consequences

- Public default behavior unchanged (8×8 protocol baseline); no CLI surface change —
  profiles are a library/test-level tuning knob until a winner is picked.
- Decode search cost scales with `levels² × texture²`; cell count scales inversely with
  `cell²`, so total decode work grows with texture size (measured in the sweep).
- The sweep's conclusions (winning tile size, robustness deltas) land in this ADR's
  status update and drive any later protocol default change.

## Implementation outcomes (sweep evidence, 640×480 / border 32 / libx264 CRF 23)

| Profile | Texture | Cell | Cells/frame | Capacity/frame | Real-codec round trip | Noise σ=8 recovery | Decode ms/frame |
| ------- | ------- | ---- | ----------- | -------------- | --------------------- | ------------------ | --------------- |
| Small   | 4×4     | 36   | 176         | 124 B          | ✅ integrity=Passed    | ≥90%               | ~137            |
| Default | 8×8     | 40   | 140         | 88 B           | ✅ integrity=Passed    | ≥90%               | ~413            |
| Large   | 16×16   | 48   | 96          | 44 B           | ✅ integrity=Passed    | ≥90%               | ~1112           |

- All three profiles round-trip a full frame of payload through real libx264 with the
  durability matrix and recover the manifest with a matching SHA-256
  (`Phase4TileSizeSweepTests`, 8/8; full suite 295/295).
- Decode cost grows super-linearly with texture size (SAD window area × candidate count):
  16×16 costs ~8× the 4×4 profile per frame. 4×4 buys 41% more capacity per frame than
  the 8×8 baseline at ~1/3 of the decode cost.
- **Decision retained:** the 8×8 baseline stays the protocol default. The 4×4 profile is
  the attractive capacity/cost trade-off, but its smaller discrimination surface warrants
  real-codec multi-frame-loss validation before any default change; that deeper durability
  comparison is tracked as follow-up tuning rather than blocking this parameterization.
