# CR-20260830-01 — Modulator Geometry Contract and Encoder/Decoder Pipeline Extraction

**Date:** 2026-08-30 **Status:** Implemented
**Area:** `YTAHD.Core.Core.EncoderEngine`, `DecoderEngine`, `IModulator`, `ModulatorGeometry`

## Context

`EncoderEngine`/`DecoderEngine` were accumulating duplicated geometry, packet-assembly, and
RGB-conversion logic directly, and `IModulator` implementations took long, error-prone
parameter lists. This needed to be consolidated before adding more modulator types.

## Decision

- Added a modulator-aware payload-capacity contract (`ModulatorGeometry`) and routed
  `EncoderEngine` through it instead of ad hoc parameters (formerly FEAT-026).
- Extracted a frame-rendering strategy so each modulator owns its own frame output path
  (formerly FEAT-027).
- Split packet assembly and frame-header construction into dedicated helpers (formerly
  FEAT-028).
- Centralized RGB/RGBA conversion and buffer handling for encoder path consistency (formerly
  FEAT-029).
- Added a shared frame-packet parser and capacity helper to the decoder path (formerly
  FEAT-030).
- Extracted raw RGB sampling and bit extraction into a dedicated decoder strategy per
  modulator (formerly FEAT-031).
- Split de-duplication, parity recovery, and payload assembly into dedicated helper methods
  (formerly FEAT-032).
- Added a protocol-level validation test matrix checking encoder/decoder packet
  compatibility end-to-end (formerly FEAT-033).
- Extracted `DecodedFrameAccumulator` from `DecoderEngine` and validated its recovery
  contract directly (formerly FEAT-034).

## Consequences

`IModulator` methods now accept a single `ModulatorGeometry` object instead of a long
width/height/macroblock/header/border tuple, and each modulator owns its own frame-render
and decode-strategy path. This is the contract every later modulator (Phase 3, durability
matrix) is built against.
