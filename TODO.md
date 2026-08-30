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

- Status: not-started

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

BUG-002 — Make the Phase 1 decoder tolerant of lossy compressed video instead of assuming a bit-perfect signal

- Status: completed
- Done: 2026-08-30
- Notes: real H.264 encode/decode validation passes after aligning the Phase 1 packet geometry and guarding against empty decode results

BUG-003 — Normalize the Phase 1 binary modulator contract to a consistent 16×16 macroblock layout

- Status: in-progress
- Started: 2026-08-30
- Notes: this is the current project guardrail; the encoder and decoder now share one macroblock-size contract to prevent real ffmpeg streams from decoding as all-zero or invalid packets

FEAT-035 — Add an explicit real-ffmpeg regression test suite for H.264 smoke validation

- Status: not-started
- Notes: keep the command-line encode/decode smoke path in a repeatable test harness so future regressions are caught immediately

FEAT-036 — Harden the lossy decoder with quality-aware packet filtering and duplicate-run scoring

- Status: not-started
- Notes: continue to tolerate real video drift without silently accepting totally empty or corrupt payload streams

FEAT-037 — Document the H.264 baseline and ffmpeg override behavior in the CLI and README

- Status: not-started
- Notes: make the Phase 1 defaults and supported ffmpeg-path options explicit for local and CI users


- Status: completed
- Done: 2026-08-30 (updated `DecoderEngine` to use threshold-based sampling, accept lossy bit drift, and validate logical payload signatures instead of exact raw pixel equality)

BUG-003 — Finalize the real FFmpeg-based Phase 1 pipeline for lossy encode/decode integration

- Status: completed
- Done: 2026-08-30 (updated the CLI/FFmpeg wiring, service options, and regression tests so the real encode/decode flow uses the correct FFmpeg path, timeout behavior, and lossy decoder semantics)

REFACTOR-001 — Extract the frame-packet parser/validator into a dedicated helper and cover edge cases with tests

- Status: completed
- Done: 2026-08-30 (FramePacket parser/validator extracted and edge-case coverage is in place)

REFACTOR-002 — Split algorithm-specific bit decoding from the frame accumulator and parity logic

- Status: completed
- Done: 2026-08-30 (FrameBitDecoderFactory isolates the modulator-specific decode strategy and enables independent algorithm tests)

REFACTOR-003 — Extract a decode pipeline orchestrator for stream reading, frame grouping, parity recovery, and output assembly

- Status: completed
- Done: 2026-08-30 (duplicate-run and payload handling are centralized so the stream loop remains a small coordinator with shared recovery logic)

Notes:

- Use FEAT-XXX for feature work, BUG-XXX for bug fixes, CHORE-XXX for maintenance tasks.
- Update this file and the tracked todo list when items progress.
