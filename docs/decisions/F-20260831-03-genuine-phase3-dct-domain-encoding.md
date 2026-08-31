# F-20260831-03 — Genuine Phase 3 DCT-Domain Encoding via IDCT Synthesis

**Date:** 2026-08-31 **Status:** Implemented
**Area:** `YTAHD.Core.Modulation.DctModulator`, `DctCarrierBasis`,
`YTAHD.Core.Core.FrameBitDecoderFactory.DctFrameBitDecoder`, `YTAHD.Tests.Phase3DctCarrierTests`

## Context

The Phase 3 implementation from F-20260830-03 was not genuine DCT-domain encoding — it wrote
binary 0xE0/0x20 pixel levels per bit, effectively a smaller-block variant of Phase 1. This
produced the same high-frequency sharp edges that H.264 quantization discards, and failed
the real `libx264` round-trip (see `PROBLEM.md` and CR-20260831-03). The roadmap requires
synthesizing smooth 8×8 blocks from low-frequency cosine waves via IDCT.

## Decision

- Added `CosTable` (precomputed `cos((2n+1)kπ/16)`), `CarrierPositions` (8 lowest-frequency
  non-DC AC positions ordered by `u+v`), `DcCoeff=1024`, `CarrierAmplitude=128`, and `C(k)`
  normalisation to `DctCarrierBasis`.
- Rewrote `DctModulator.CreatePhase3Frame` / `Encode` to synthesize each 8×8 block via IDCT:
  `pixel = 128 + Σ C(u)C(v)/4 · (bit?+128:−128) · cos[px,u] · cos[py,v]`, producing smooth
  organic gradients instead of binary blocks. Capacity is now 1 byte per 8×8 block (8 AC
  carriers), down from 2 bytes/block.
- Rewrote `DctFrameBitDecoder.Decode` to apply a forward 2-D DCT per block and recover each
  bit from the sign of `F(u,v) = C(u)C(v)/4 · Σ luma(x,y)·cos[x,u]·cos[y,v]`.
- Updated `Phase3DctCarrierTests` for the new 1-byte-per-block capacity, single-block
  round-trip semantics, and IDCT-based pixel assertions.

## Consequences

H.264 quantization noise on spectrally smooth blocks is approximately zero-mean and
uncorrelated; since `Σ cos((2x+1)kπ/16) = 0` for `k>0`, AC coefficient errors average to near
zero across a 64-pixel block. Coefficient signs survive lossy compression reliably, unlike
per-pixel thresholding. All 105 tests pass, including the real-FFmpeg Phase 3 round-trip
(`RealFfmpeg_DctModulator_SingleFrame_RoundTrip_DoesNotHang`,
`RealFfmpeg_LargerPayloadMatrix_RoundTrips_For_Phase3`). Phase 3 is now a production-validated
modulator alongside Phase 1 and Phase 2. `PROBLEM.md` was updated to mark the investigation
resolved.
