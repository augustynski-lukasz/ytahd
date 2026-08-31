# F-20260831-01 — Real-FFmpeg Validation Matrix for Phase 1 / Phase 2 / Durability

**Date:** 2026-08-31 **Status:** Implemented
**Area:** `YTAHD.Tests.YtahdCodecServiceTests`, `README.md`

## Context

Phase 1 and Phase 2 needed explicit real-FFmpeg (not just fake-wrapper) validation across
multiple payload sizes to confirm byte-for-byte recovery and stable metrics under actual
`libx264` encode/decode, plus a documented operating envelope for real-codec throughput and
reliability.

## Decision

- Added real CLI and service-level encode/decode checks validated against actual ffmpeg
  output for Phase 1 and Phase 2 (formerly FEAT-035, "real ffmpeg Phase 1/2" instance).
- Ran a larger real-payload end-to-end regression matrix across Phase 1 / Phase 2 / the
  durability transport path at several payload sizes (formerly FEAT-039).
- Captured a documented real-codec throughput baseline and reliability checklist: verified
  payload sizes, expected frame generation, and the requirement to treat real H.264 as the
  source of truth (formerly FEAT-040, "throughput baseline" instance).

## Consequences

Phase 1 and Phase 2 are confirmed production-validated under real `libx264` at multiple
payload sizes. This regression matrix is the baseline the later genuine Phase 3 rewrite
(F-20260831-03) was required to pass as well.
