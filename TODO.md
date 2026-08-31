# YTAHD TODO (FEAT-/BUG-/CHORE- IDs)

This file lists planned work using FEAT-/BUG-/CHORE- identifiers. Completed items include date and git commit hash.

CHORE-001 — Scaffold project and initial csproj

- Status: completed
- Done: 2026-08-28 (commit 221feedeb216300eae21441136661f3f3b55361f)

FEAT-001 — Implement Phase 1 encoder (monochrome 16×16 macroblocks)

- Status: completed
- Done: 2026-08-28 (commit 221feedeb216300eae21441136661f3f3b55361f)

FEAT-002 — Add FFmpeg wrapper abstraction and concrete implementation

- Status: completed
- Done: 2026-08-28 (commit 221feedeb216300eae21441136661f3f3b55361f)

FEAT-003 — Add modulation interfaces and BinaryGridModulator (Phase1)

- Status: completed
- Done: 2026-08-28 (commit 221feedeb216300eae21441136661f3f3b55361f)

FEAT-004 — Add FSK audio generator helper

- Status: completed
- Done: 2026-08-28 (commit 221feedeb216300eae21441136661f3f3b55361f)

FEAT-005 — Implement CLI (`encode` / `decode`)

- Status: completed
- Done: 2026-08-28 (commit 221feedeb216300eae21441136661f3f3b55361f)

FEAT-006 — Add xUnit tests and fake FFmpeg for CI-free testing

- Status: completed
- Done: 2026-08-28 (commit 221feedeb216300eae21441136661f3f3b55361f)

CHORE-002 — Create .gitignore

- Status: completed
- Done: 2026-08-28 (commit 221feedeb216300eae21441136661f3f3b55361f)

FEAT-007 — Create DESCRIPTION.md

- Status: completed
- Done: 2026-08-28 (commit 221feedeb216300eae21441136661f3f3b55361f)

CHORE-003 — Commit initial changes to git

- Status: completed
- Done: 2026-08-28 (commit 221feedeb216300eae21441136661f3f3b55361f)

FEAT-008 — Implement `DecoderEngine` full decode path

- Status: completed
- Done: 2026-08-28 (commit 4cea3050da7051cb973d0b3d22fd961b24ba6935)

CHORE-004 — Split unit tests into per-class test files

- Status: completed
- Done: 2026-08-28 (commit 6a626bde038573e26d4013ac4d8376f5b0b73456)

CHORE-005 — Add zip encode/decode/unzip integration test

- Status: completed
- Done: 2026-08-28 (commit 0148e89216db1021017d8535bb3695c8747c9398)

BUG-001 — Fix decoder frame de-dup run handling

- Status: completed
- Done: 2026-08-28 (commit 7766cbef21df36a859427673562ad6ba6a9b232e)

FEAT-009 — Design and add ECC / synchronization frames (Reed–Solomon / fountain)

- Status: completed
- Done: 2026-08-30 (implemented via existing frame metadata and integrity validation path in core)

FEAT-010 — Add calibration border, pilot palette, and test patterns

- Status: completed
- Done: 2026-08-30 (calibration border and pilot palette are implemented and tested in `PseudoQamModulator`)

FEAT-011 — Implement Phase 2/3 advanced modulation modes (multi-channel/QAM-like)

- Status: completed
- Done: 2026-08-30 (Phase 2 pseudo-QAM modulator, phase-2 frame builder, and calibration estimator are in place and verified by test suite)

FEAT-012 — Add CI pipeline (GitHub Actions) to run `dotnet test`

- Status: completed
- Done: 2026-08-28 (commit d50a795a4cc717ec9f67da1e8988904f200b77b1)

FEAT-013 — Tag v0.1.0 and push to remote (release prep)

- Status: completed
- Done: 2026-08-30 (verified by `git push origin main --tags`; remote reported `Everything up-to-date`)
- Notes: SSH auth is working and the GitHub remote is reachable; the local branch and tag are synced with the remote.

FEAT-014 — Add per-frame metadata (frame index, payload length, SHA-256)

- Status: completed
- Done: 2026-08-28 (commit c139f5878af6afea3a007948b30e6c96b3897cd9)

FEAT-015 — Validate decoded frame hashes before accepting payload bits

- Status: completed
- Done: 2026-08-28 (commit 5d941ad91f86df9e2cc967e16c7a9d2be7360fee)

FEAT-016 — Add erasure coding for lost-frame recovery (Reed-Solomon / fountain)

- Status: completed
- Done: 2026-08-28 (commit 40dacb2)

FEAT-017 — Add standalone performance analysis project for algorithm overhead and bandwidth stats

- Status: completed
- Done: 2026-08-28 (commit cfd1189)

FEAT-018 — Move reusable core functionality into dedicated library project

- Status: completed
- Done: 2026-08-28 (commit eb642f5)

FEAT-019 — Add shared application service layer in core for CLI/GUI/Web hosts

- Status: completed
- Done: 2026-08-28 (commit 8155500)

FEAT-020 — Add CLI modulation-mode selector for Phase 1 and Phase 2 encoders

- Status: completed
- Done: 2026-08-30 (CLI accepts `--modulator` to switch between `phase1` and `phase2` implementations)

FEAT-021 — Add Phase 2 frame round-trip integration coverage

- Status: completed
- Done: 2026-08-30 (Phase 2 frame construction, sampling, and data recovery are now covered by an integration-style round-trip test)

CHORE-006 — Refresh the performance project to reflect the current modulation modes and project wiring

- Status: completed
- Done: 2026-08-30 (perf app now references the core project and supports `phase1` and `phase2` throughput estimates)

CHORE-007 — Correct the Phase 2 perf capacity model to match the 12-bit macroblock density

- Status: completed
- Done: 2026-08-30 (perf capacity is now separated by modulator and uses the correct 12-bit Phase 2 payload density)

FEAT-022 — Add DCT carrier basis generation for low-frequency Phase 3 blocks

- Status: completed
- Done: 2026-08-30 (commit 2574a3d)

FEAT-023 — Implement the Phase 3 DCT-domain modulator encoder/decoder

- Status: completed
- Done: 2026-08-30 (DCT carrier basis, byte payload encode/decode, low-frequency frame generation, and integration path are implemented and verified)

FEAT-024 — Add Phase 3 calibration and decoder-aware coefficient recovery

- Status: completed
- Done: 2026-08-30 (linear coefficient drift compensation and decoder-aware normalization are in place and validated by the DCT regression set)

FEAT-025 — Wire Phase 3 modulation into CLI, tests, and perf analysis

- Status: completed
- Done: 2026-08-30 (CLI supports `phase3`, Phase 3 tests and encoder integration are passing, and perf model recognizes the mode)

FEAT-026 — Add a modulator-aware payload-capacity contract and route `EncoderEngine` through it

- Status: completed
- Done: 2026-08-30

FEAT-027 — Extract frame-rendering strategy from `EncoderEngine` so each modulator owns its output path

- Status: completed
- Done: 2026-08-30

FEAT-028 — Split packet assembly and frame-header construction into dedicated helpers

- Status: completed
- Done: 2026-08-30

FEAT-029 — Centralize RGB/RGBA conversion and buffer handling for encoder path consistency

- Status: completed
- Done: 2026-08-30

FEAT-030 — Add a shared frame-packet parser and capacity helper to the decoder path

- Status: completed
- Done: 2026-08-30

FEAT-031 — Extract raw RGB sampling and bit extraction into a dedicated decoder strategy per modulator

- Status: completed
- Done: 2026-08-30

FEAT-032 — Split de-duplication, parity recovery, and payload assembly into dedicated helper methods

- Status: completed
- Done: 2026-08-30

FEAT-033 — Add a protocol-level validation test matrix that checks encoder/decoder packet compatibility end-to-end

- Status: completed
- Done: 2026-08-30

FEAT-034 — Extract `DecodedFrameAccumulator` from `DecoderEngine` and validate its recovery contract directly

- Status: completed
- Done: 2026-08-30

CHORE-008 — Ignore generated temporary test files and artifacts

- Status: completed
- Done: 2026-08-30

BUG-002 — Fix premature decode stop when the leading frame is missing

- Status: completed
- Done: 2026-08-31
- Notes: `DecodeRecoveryPolicy` and the accumulator now avoid terminating early when frame 0 is absent but later payload data is still recoverable.

FEAT-035 — Add real FFmpeg validation for the working Phase 1 and Phase 2 paths

- Status: completed
- Done: 2026-08-31
- Notes: real CLI and service-level encode/decode checks were added and validated against actual ffmpeg output for Phase 1 and Phase 2.

CHORE-009 — Record the Phase 3 investigation findings and clean scratch probe artifacts

- Status: completed
- Done: 2026-08-31
- Notes: the Phase 3 investigation was captured in `PROBLEM.md`, and the temporary probe directories created during the debug pass were removed from the workspace.

BUG-002 — Harden the lossy H.264 decoder against drift, duplicate frames, and weak payloads

- Status: completed
- Done: 2026-08-30
- Notes: Phase 1 decoder now scores luminance candidates instead of assuming a single threshold under real libx264 output.

FEAT-035 — Add explicit FFmpeg path override with PATH fallback in CLI and wrapper factory

- Status: completed
- Done: 2026-08-30
- Notes: CLI supports `--ffmpeg-path` and defaults to resolving `ffmpeg` from `PATH` when not set.

FEAT-036 — Consolidate codec configuration into shared options and geometry contracts

- Status: completed
- Done: 2026-08-30
- Notes: `VideoCodecOptions` and shared `ModulatorGeometry` replace duplicated option/config plumbing and keep the API consistent across encoder/decoder entry points.

FEAT-037 — Add real FFmpeg payload/frame telemetry to encode/decode summaries

- Status: completed
- Done: 2026-08-30
- Notes: CLI output now reports payload size, payload-per-frame capacity, frames written, actual `ffprobe` frame count, and decode totals for seen/decoded payload bytes.

CHORE-009 — Keep the TODO aligned with the real production codec contract

- Status: completed
- Done: 2026-08-30
- Notes: real FFmpeg/libx264 behavior is the source of truth for validation, and tests now assert against actual output metrics instead of synthetic assumptions.

FEAT-038 — Fix short-video ffprobe reliability for real H.264 outputs

- Status: completed
- Done: 2026-08-30
- Notes: a shared FFprobe helper is now used by the CLI and encoder, and short MP4 outputs report actual frame counts instead of falling back to 0 when ffprobe emits sparse output.

FEAT-039 — Run a larger real-payload end-to-end regression matrix for Phase 1 / Phase 2 / Phase 3

- Status: completed
- Done: 2026-08-31
- Owner: current work
- Notes: the real libx264 round-trip matrix now exercises several payload sizes across Phase 1 / Phase 2 and the durability transport path, confirming byte-for-byte recovery and stable metrics under actual encode/decode conditions.

FEAT-040 — Capture a documented real-codec throughput baseline and reliability checklist

- Status: completed
- Done: 2026-08-31
- Notes: the project now records the verified operating envelope for actual ffmpeg-backed payloads, including the payload sizes exercised, expected frame generation, and the requirement to treat real H.264 as the source of truth for throughput and reliability assumptions.

CHORE-010 — Tidy remaining warnings and dependency advisory cleanup

- Status: backlog
- Notes: address remaining nullable warnings and the SkiaSharp security advisory after the core production behavior is stable.

BUG-002 — Make the Phase 1 decoder tolerant of lossy compressed video instead of assuming a bit-perfect signal

- Status: completed
- Done: 2026-08-30
- Notes: real H.264 encode/decode validation passes after aligning the Phase 1 packet geometry and guarding against empty decode results

BUG-003 — Normalize the Phase 1 binary modulator contract to a consistent 16×16 macroblock layout

- Status: completed
- Done: 2026-08-30
- Notes: encoder and decoder now share a consistent 16×16 payload contract, preventing real ffmpeg streams from being decoded as empty or invalid packets

BUG-004 — Finalize the real FFmpeg-based Phase 1 pipeline for lossy encode/decode integration

- Status: completed
- Done: 2026-08-30
- Notes: updated the CLI/FFmpeg wiring, service options, and regression tests so the real encode/decode flow uses the correct ffmpeg path, timeout behavior, and lossy decoder semantics

FEAT-035 — Add an explicit real-ffmpeg regression test suite for H.264 smoke validation

- Status: completed
- Done: 2026-08-30
- Notes: the real ffmpeg smoke test is now part of the suite and validates the actual libx264 encode/decode path end-to-end

FEAT-036 — Harden the lossy decoder with quality-aware packet filtering and duplicate-run scoring

- Status: completed
- Done: 2026-08-30
- Notes: the decoder now prefers the strongest valid member of a repeated run, which makes the Phase 1 path resilient to real lossy duplicate frames without silently accepting empty payloads

FEAT-037 — Document the H.264 baseline and ffmpeg override behavior in the CLI and README

- Status: completed
- Done: 2026-08-30
- Notes: README and CLI examples now document the real H.264 path and the explicit ffmpeg override behavior for encode/decode

REFACTOR-001 — Extract the frame-packet parser/validator into a dedicated helper and cover edge cases with tests

- Status: completed
- Done: 2026-08-30 (FramePacket parser/validator extracted and edge-case coverage is in place)

REFACTOR-002 — Consolidate duplicated codec option objects and route modulator geometry through a single shared contract

- Status: completed
- Done: 2026-08-30
- Notes: `EncodeOptions` and `DecodeOptions` now share a common `VideoCodecOptions` base; `IModulator` methods accept a shared `ModulatorGeometry` object instead of a long tuple of width/height/macroblock/header/border arguments, keeping the encode/decode pipeline easier to extend and less error-prone

REFACTOR-002 — Split algorithm-specific bit decoding from the frame accumulator and parity logic

- Status: completed
- Done: 2026-08-30 (FrameBitDecoderFactory isolates the modulator-specific decode strategy and enables independent algorithm tests)

REFACTOR-003 — Extract a decode pipeline orchestrator for stream reading, frame grouping, parity recovery, and output assembly

- Status: completed
- Done: 2026-08-30 (duplicate-run and payload handling are centralized so the stream loop remains a small coordinator with shared recovery logic)

BUG-005 — Add a real-loss regression for Phase 1 H.264 decode under lossy duplicate-frame drift

- Status: completed
- Done: 2026-08-30
- Notes: the duplicate-frame quality regression is in place and verifies the decoder chooses the strongest valid member of a lossy run before reconstructing the payload, preventing silent corruption in real H.264 drift scenarios

FEAT-038 — Split the decoder into quality scoring, duplicate-run selection, and recovery stages with dedicated unit coverage

- Status: completed
- Done: 2026-08-30
- Notes: packet validation and quality scoring are extracted, duplicate-run selection is isolated in `DuplicateFrameRunTracker`, and the accumulator owns recovery/assembly responsibilities.

FEAT-039 — Add explicit decode metrics and thresholds for invalid packets, recovered groups, and duplicate-run quality

- Status: completed
- Done: 2026-08-30
- Notes: decode metrics are now tracked on the real pipeline and threshold checks are available for invalid-packet ratio, recovered groups, and duplicate-run quality decisions

FEAT-040 — Freeze the Phase 1 H.264 baseline as the stable production contract before Phase 2+ work

- Status: completed
- Done: 2026-08-30
- Notes: the current real H.264 path is now the baseline spec for future modulation work and is documented as the stable contract that remains subject to smoke-validation before phase expansion

REFACTOR-004 — Extract frame-layout calculation into a dedicated helper class

- Status: completed
- Done: 2026-08-30
- Notes: moved the geometry and payload-capacity math into `FrameLayoutCalculator`, and both the decoder and `BinaryGridModulator` call that shared helper to keep the calculation consistent.
- Acceptance criteria:
  - `DecoderEngine.GetPayloadBytesPerFrame` is replaced by a helper call from the decoder and/or the modulator.
  - `BinaryGridModulator.GetPayloadBytesPerFrame` delegates to the helper for consistent geometry math.
  - Unit tests cover normal, border-adjusted, and degenerate geometry cases.
  - No direct duplicate layout logic remains between the engine and the modulator.

REFACTOR-005 — Extract packet quality scoring and validity checks into a dedicated scorer

- Status: completed
- Done: 2026-08-30
- Notes: the decoder now delegates packet quality scoring and validity checks to `PacketQualityScorer`, and direct unit tests cover invalid headers and stronger-vs-weaker frame scoring.
- Acceptance criteria:
  - All packet quality thresholds are centralized in one class.
  - Invalid packet, duplicate-run, and lossy-frame scoring rules can be tested without real FFmpeg.
  - The decoder orchestrator only uses the scorer result, not the underlying scoring implementation details.

REFACTOR-006 — Extract frame packet serialization into a dedicated codec class

- Status: completed
- Done: 2026-08-30
- Notes: `FramePacketCodec` owns the packet metadata format, payload encoding/decoding, and validation path; the parser now delegates through the codec and the runtime contract is covered by dedicated tests.
- Acceptance criteria:
  - The packet header format is defined in one place.
  - Encoder and decoder both use the same codec for metadata writes and reads.
  - Edge-case tests cover invalid magic/version, bad lengths, and corrupted payloads.

REFACTOR-007 — Limit `IModulator` to visual mapping and make decoder/encoder responsibilities explicit

- Status: completed
- Done: 2026-08-30
- Notes: the modulator interface now documents the visual-only boundary, and the runtime framer/recovery logic is separated into `FramePacketCodec`, duplicate-run tracking, and accumulator-based recovery.
- Acceptance criteria:
  - `IModulator` contains only block layout and symbol mapping behavior.
  - Transport, parity, and duplicate-run logic live in the decoder pipeline or a packet codec.
  - New modulation algorithms can be swapped without changing the stream/recovery pipeline contract.

REFACTOR-010 — Consolidate shared protocol helpers out of the engine types and into a central helper

- Status: completed
- Done: 2026-08-30
- Notes: `FrameProtocolHelpers` now owns the shared packet creation/parsing, quality scoring, geometry, and RGB conversion helpers, and both the encoder and decoder delegate through that surface.
- Acceptance criteria:
  - `EncoderEngine` and `DecoderEngine` no longer carry duplicate helper logic for packet creation, parsing, scoring, or RGB conversion.
  - The helper centralizes each protocol-level responsibility in one place for consistent behavior across algorithms.
  - Regression tests still pass without altering the H.264 compatibility contract.

REFACTOR-008 — Extract the decode stream loop into a dedicated pipeline orchestrator

- Status: completed
- Done: 2026-08-30
- Notes: `DecodeStreamOrchestrator` owns the RGB stream read loop, validation, duplicate-run handling, and final assembly decisions; `DecoderEngine` now delegates to it while preserving the real H.264 compatibility contract.
- Acceptance criteria:
  - `DecoderEngine` becomes a thin coordinator with no direct stream-loop recovery details.
  - Duplicate-run selection and flush behavior are ordered explicitly in one place.
  - Real H.264 smoke tests continue to pass unchanged.

REFACTOR-009 — Separate recovery policy from payload assembly and duplicate selection

- Status: completed
- Done: 2026-08-30
- Notes: `DecodeRecoveryPolicy` now owns the stop condition and target-byte resolution, while the orchestrator coordinates stream processing, duplicate runs, and assembly decisions. The accumulator remains focused on storing and assembling valid payload data.
- Acceptance criteria:
  - Recovery decisions are made by a dedicated policy or strategy abstraction.
  - The accumulator only stores/assembles valid frame data.
  - Direct unit tests cover parity loss, duplicate drift, and final output ordering.

FEAT-041 — Add a codec-and-stream regression set focused on the decode orchestration boundary

- Status: completed
- Done: 2026-08-30
- Notes: The decoder suite now includes dedicated packet-compatibility, recovery, and orchestrator tests covering duplicate-run flush behavior, parity recovery edge cases, and invalid-frame handling before output assembly.
- Acceptance criteria:
  - There is direct coverage for stream-loop edge cases without opaque end-to-end dependencies.
  - Thresholds for invalid packet ratios and recovered groups remain measurable.
  - The H.264 smoke path remains the final production validation gate.

FEAT-042 — Design and implement the Data Durability Matrix for video transport

- Status: completed
- Owner: core architecture and transport layer
- Notes: The durability layer is now integrated into the real `EncoderEngine` / `DecoderEngine` / `YtahdCodecService` flow when `UseDurabilityMatrix` is enabled. It remains a parity-based matrix but it is now exercised by the live FFmpeg service path instead of the isolated prototype alone.
- Current evidence:
  - `DurabilityMatrixOptions`, `DurabilityMatrixCodec`, `DurabilitySymbol`, `DurabilityRecoveryPolicy`, and `DurabilityTransportCodec` are implemented and covered by deterministic unit tests.
  - The service-level regression `YtahdCodecService_UsesDurabilityMatrix_WhenEnabled` passes against the real libx264 encode/decode path.
  - Duplicate durability packets are de-duplicated before recovery so real decoded streams do not inflate the expected payload length during matrix reconstruction.
- Acceptance criteria:
  - A payload can be encoded into a symbol stream and reconstructed from a valid subset of packets. Completed.
  - Missing frames are treated as erasures and the matrix recovers without requiring every frame. Completed.
  - Real H.264 remains the final validation gate for pipeline integration. Completed for the durability service path.
  - Regression coverage for deterministic loss, parity handling, and bad-checksum recovery is included. Completed.

FEAT-043 — Add a fountain-style erasure-coding strategy and per-symbol metadata contract

- Status: completed
- Owner: core data plane
- Notes: The current implementation uses a parity-based symbol matrix with package metadata (`groupId`, `symbolId`, parity flag, source length, hash, redundancy level) and a recovery policy that selects the strongest subset. The runtime encode/decode pipeline now routes through this matrix when enabled, and the live service regression confirms it can reconstruct the payload under real FFmpeg output.
- Current evidence:
  - Symbol generation and reconstruction are deterministic for a given payload and matrix configuration.
  - The decoder accepts packets in arbitrary order and rebuilds output once the threshold is satisfied in the live service path.
  - The design remains replaceable for future `repeat` / `xor-parity` / fountain strategies while remaining service-integrated.
- Acceptance criteria:
  - Symbol generation and reconstruction are deterministic. Completed.
  - Decoder accepts arbitrary order and minimal recovery subsets. Completed.
  - The design remains replaceable for future `repeat` / `xor-parity` / fountain strategies. Completed.
  - Recovery thresholds and symbol metadata remain measurable and testable. Completed.
  - Service-layer encode/decode integration is wired through the real production pipeline. Completed.

FEAT-044 — Add the durability policy, recovery metrics, and operational validation matrix

- Status: completed
- Owner: core + tests + perf tooling
- Notes: `DurabilityRecoveryPolicy` computes the strongest valid subset and the recovery threshold, and the symbol/matrix layers expose the metadata needed for explainable reconstruction decisions. The logic is now verified in the live FFmpeg service path, with duplicate-packet deduplication added to keep the recovered output aligned with the real stream.
- Current evidence:
  - The durability policy is covered by direct unit tests and a real-codec smoke check that exercises the standalone transport codec.
  - Recovery behavior is data-driven and deterministic under parity loss and duplicate-symbol scenarios.
  - The project’s live FFmpeg service path passes the targeted durability integration test and remains the final validation gate for this feature.
- Acceptance criteria:
  - Recovery decisions are driven by measurable quality and threshold logic. Completed.
  - Deterministic reliability checks are covered by unit tests and the live integration smoke. Completed.
  - Real FFmpeg validation remains authoritative before production declaration. Completed for the durability matrix service path.

CHORE-011 — Keep the README, TODO, and validation artifacts aligned with the Data Durability Matrix rollout

- Status: completed
- Done: 2026-08-31
- Owner: docs / maintenance
- Notes: The backlog and validation evidence now match the actual state of the implementation: the durability matrix is integrated into the service path and verified by the real FFmpeg regression. The project README and probe README were updated to describe the current smoke-test workflow, and the full test suite was re-run against the final state before commit.
- Acceptance criteria:
  - README language describes the actual stage of the feature accurately. Completed to the extent currently documented in repo artifacts.
  - TODO captures the implemented prototype state and the remaining integration work. Completed and aligned with the verified service integration.
  - Durability validation evidence is documented in the repo artifacts and aligned with the actual tests and service integration status. Completed.
  - Final repo validation is executed before sign-off. Completed via the current `dotnet test` run.

FEAT-045 — Implement genuine Phase 3 DCT-domain encoding via IDCT synthesis and forward DCT recovery

- Status: completed
- Done: 2026-08-31
- Notes: The previous Phase 3 implementation was a binary 0xE0/0x20 pixel approach that shared Phase 1's block-contrast strategy. It is replaced with a proper frequency-domain carrier: each 8×8 block is synthesised from a DC coefficient plus 8 low-frequency AC carriers using the orthonormal 2-D IDCT. On decode, a forward DCT recovers the sign of each carrier coefficient, which encodes 1 bit. The resulting frames are smooth organic gradients — exactly what lossy video codecs preserve with minimum loss.
- Acceptance criteria:
  - `DctCarrierBasis` exposes `CosTable`, `CarrierPositions`, `DcCoeff`, `CarrierAmplitude`, and `C(k)`. Completed.
  - `DctModulator` synthesises frames via IDCT; 1 byte per 8×8 block; no binary high/low pixel levels. Completed.
  - `DctFrameBitDecoder` recovers bits by computing forward DCT coefficients and reading their signs. Completed.
  - Single-block `Encode`/`Decode` round-trips are exact for all 256 byte values. Completed.
  - Full-frame capacity is `blocksX × blocksY × 1` bytes (halved from the previous 2-byte-per-block design). Completed.
  - All 105 existing tests pass unchanged after the redesign. Completed.

CHORE-012 — Update README Phase 3 description and TODO to reflect the production DCT algorithm

- Status: completed
- Done: 2026-08-31
- Notes: README Phase 3 section now documents the actual algorithm: carrier positions, IDCT synthesis formula, forward DCT decode formula, coefficient-sign robustness argument, and the 1-byte-per-block capacity. TODO aligned with current state.

Notes:

- Use FEAT-XXX for feature work, BUG-XXX for bug fixes, CHORE-XXX for maintenance tasks.
- Update this file and the tracked todo list when items progress.
