# F-20260830-03 — Phase 3 DCT Modulator (Initial Implementation, Later Superseded)

**Date:** 2026-08-30 **Status:** Superseded by F-20260831-03
**Area:** `YTAHD.Core.Modulation.DctModulator`, `YTAHD.Core.Modulation.DctCarrierBasis`

## Context

The roadmap's Phase 3 called for injecting data into the frequency domain via DCT so frames
stay visually smooth to the video codec. An initial implementation was built and wired into
the CLI/tests/perf tooling.

## Decision

- Added `DctCarrierBasis` low-frequency carrier basis generation (formerly FEAT-022).
- Implemented the first `DctModulator` encoder/decoder (formerly FEAT-023).
- Added calibration and decoder-aware coefficient recovery / drift compensation (formerly
  FEAT-024).
- Wired Phase 3 into the CLI (`--modulator phase3`), tests, and perf analysis (formerly
  FEAT-025).

## Consequences

This implementation synthetically round-tripped but used a binary high/low pixel-level
scheme (not true frequency-domain synthesis) and failed under real H.264 encode/decode — see
the investigation captured in `PROBLEM.md` and its resolution in
[F-20260831-03-genuine-phase3-dct-domain-encoding](F-20260831-03-genuine-phase3-dct-domain-encoding.md).
This ADR is retained for history; the current Phase 3 implementation is the superseding one.
