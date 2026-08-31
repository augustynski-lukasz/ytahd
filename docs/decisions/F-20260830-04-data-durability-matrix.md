# F-20260830-04 — Data Durability Matrix (Parity-Based Symbol Transport)

**Date:** 2026-08-30 **Status:** Implemented
**Area:** `DurabilityMatrixOptions`, `DurabilityMatrixCodec`, `DurabilitySymbol`,
`DurabilityRecoveryPolicy`, `DurabilityTransportCodec`, `YtahdCodecService`

## Context

The transport layer needed a symbol-based durability model above the existing frame/packet
protocol: split payload bytes into symbol blocks grouped by durability window, and
reconstruct from the highest-quality valid subset using packet metadata, checksum validity,
and frame recovery thresholds — without replacing the existing modulator contract.

## Decision

- Designed and implemented the parity-first Data Durability Matrix: `DurabilityMatrixOptions`,
  `DurabilityMatrixCodec`, `DurabilitySymbol`, `DurabilityRecoveryPolicy`, and
  `DurabilityTransportCodec`, wired into `EncoderEngine` / `DecoderEngine` /
  `YtahdCodecService` behind `UseDurabilityMatrix` (formerly FEAT-042).
- Used per-symbol metadata (`groupId`, `symbolId`, parity flag, source length, hash,
  redundancy level) with a recovery policy that selects the strongest valid subset, keeping
  the design replaceable for future fountain-style strategies (formerly FEAT-043).
- Added the recovery-metrics/operational validation matrix: deterministic reliability checks
  under parity loss and duplicate-symbol scenarios, plus duplicate-packet de-duplication so
  real decoded streams don't inflate expected payload length during reconstruction (formerly
  FEAT-044).

## Consequences

The durability matrix is integrated into the live FFmpeg service path (not just an isolated
prototype): `YtahdCodecService_UsesDurabilityMatrix_WhenEnabled` passes against the real
`libx264` encode/decode path. A payload can be reconstructed from a valid packet subset, and
missing frames are treated as erasures without requiring every frame.
