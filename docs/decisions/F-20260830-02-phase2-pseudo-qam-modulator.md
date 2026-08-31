# F-20260830-02 — Phase 2 Pseudo-QAM Multi-Channel Modulator

**Date:** 2026-08-30 **Status:** Implemented
**Area:** `YTAHD.Core.Modulation.PseudoQamModulator`, `YTAHD.Cli`, `YTAHD.Perf`

## Context

Phase 1 only carried 1 bit per macroblock. The roadmap's Phase 2 called for independent
16-PAM amplitude modulation across R/G/B channels (12 bits/macroblock) with a calibration
pilot palette to correct for codec-induced level drift.

## Decision

- Designed and implemented ECC/synchronization frame support reused by Phase 2 (formerly
  FEAT-009).
- Added the calibration border, pilot palette, and test patterns used by the decoder to
  dynamically recalculate 16-PAM decision thresholds (formerly FEAT-010).
- Implemented `PseudoQamModulator`, the Phase 2 frame builder, and the calibration estimator
  (formerly FEAT-011).
- Added the CLI `--modulator` selector so `phase1`/`phase2` can be chosen at encode/decode
  time (formerly FEAT-020).
- Added an integration-style Phase 2 frame round-trip test covering construction, sampling,
  and data recovery (formerly FEAT-021).
- Updated `YTAHD.Perf` to reference `YTAHD.Core` and report `phase1`/`phase2` throughput
  (formerly CHORE-006), then corrected the Phase 2 capacity model to the true 12-bit/
  macroblock density (formerly CHORE-007).

## Consequences

Phase 2 became the second production-validated modulator (later confirmed against real
FFmpeg — see F-20260831-02). The pilot-palette calibration pattern set the precedent later
reused for Phase 3 drift tolerance.
