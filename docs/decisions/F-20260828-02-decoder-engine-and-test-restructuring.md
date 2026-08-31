# F-20260828-02 — Decoder Engine, Test Restructuring, and Frame De-Dup Fix

**Date:** 2026-08-28 **Status:** Implemented
**Area:** `YTAHD.Core.Core.DecoderEngine`, `YTAHD.Tests`

## Context

The encoder existed but there was no full decode path, and all unit tests lived in one file,
making regressions hard to isolate. A duplicate-frame handling bug was also found during
this work.

## Decision

- Implemented the full `DecoderEngine` decode path (formerly FEAT-008).
- Split unit tests into dedicated per-class test files (formerly CHORE-004).
- Added a zip encode/decode/unzip integration test to validate real payload round-trips
  (formerly CHORE-005).
- Fixed the decoder's frame de-duplication run handling, which had been mishandling
  repeated identical frames from the video container (formerly BUG-001).

## Consequences

Established per-class test organization used for the rest of the project, and produced the
first end-to-end proof (zip round-trip) that encode → video → decode preserved binary data.
