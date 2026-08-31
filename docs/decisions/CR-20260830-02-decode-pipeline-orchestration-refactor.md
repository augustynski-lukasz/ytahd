# CR-20260830-02 — Decode Pipeline Orchestration Refactor

**Date:** 2026-08-30 **Status:** Implemented
**Area:** `YTAHD.Core.Core.DecodeStreamOrchestrator`, `DecodeRecoveryPolicy`,
`PacketQualityScorer`, `FramePacketCodec`, `FrameProtocolHelpers`, `DuplicateFrameRunTracker`

## Context

`DecoderEngine` had grown a large, monolithic stream-read loop mixing validation,
duplicate-frame handling, quality scoring, and payload assembly, making it hard to test
individual recovery decisions in isolation.

## Decision

- Extracted the frame-packet parser/validator into a dedicated helper with edge-case tests
  (formerly REFACTOR-001).
- Consolidated duplicated codec option objects (`EncodeOptions`/`DecodeOptions` share
  `VideoCodecOptions`) (formerly REFACTOR-002, first instance).
- Split algorithm-specific bit decoding into `FrameBitDecoderFactory`, isolating
  modulator-specific decode strategy for independent testing (formerly REFACTOR-002, second
  instance).
- Extracted a decode pipeline orchestrator (`DecodeStreamOrchestrator`) covering stream
  reading, frame grouping, parity recovery, and output assembly (formerly REFACTOR-003).
- Extracted packet quality scoring into `PacketQualityScorer` (formerly REFACTOR-005).
- Extracted frame packet serialization into `FramePacketCodec` (formerly REFACTOR-006).
- Limited `IModulator` to visual mapping only, moving transport/parity/duplicate-run logic
  out of it (formerly REFACTOR-007).
- Consolidated shared protocol helpers into `FrameProtocolHelpers` (formerly REFACTOR-010).
- Extracted the decode stream loop itself into `DecodeStreamOrchestrator` as a thin
  coordinator (formerly REFACTOR-008).
- Separated recovery policy (`DecodeRecoveryPolicy`: stop condition, target-byte resolution)
  from payload assembly and duplicate selection (formerly REFACTOR-009).
- Extracted frame-layout math into `FrameLayoutCalculator`, used by both the decoder and
  `BinaryGridModulator` (formerly REFACTOR-004).
- Split decoder quality scoring, duplicate-run selection (`DuplicateFrameRunTracker`), and
  recovery into dedicated stages with direct unit coverage (formerly FEAT-038, first
  instance).
- Added explicit decode metrics/thresholds for invalid packets, recovered groups, and
  duplicate-run quality (formerly FEAT-039, first instance).
- Added a codec-and-stream regression set focused on the decode orchestration boundary:
  duplicate-run flush behavior, parity recovery edge cases, invalid-frame handling (formerly
  FEAT-041).

## Consequences

`DecoderEngine` is now a thin coordinator with no direct stream-loop recovery details.
Recovery decisions, duplicate-run selection, quality scoring, and packet serialization each
live in one dedicated, independently testable class. Real H.264 smoke tests continued to
pass unchanged throughout this refactor series.
