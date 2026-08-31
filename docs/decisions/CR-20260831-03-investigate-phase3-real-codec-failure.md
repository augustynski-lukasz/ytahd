# CR-20260831-03 — Investigate and Document Phase 3 Real-Codec Failure

**Date:** 2026-08-31 **Status:** Superseded by F-20260831-03
**Area:** `PROBLEM.md`, scratch probe directories

## Context

The initial Phase 3 DCT implementation (F-20260830-03) round-tripped in synthetic tests but
failed under real H.264/`libx264` encode/decode: malformed candidate packet headers,
`Missing frame index 0`, and incomplete payload assembly during recovery.

## Decision

Captured the investigation findings in `PROBLEM.md` (root-cause direction: protocol
contract mismatch between the low-frequency DCT carrier layout, packet offset scanning, and
the raw RGB decode path) and removed the temporary probe directories created during the
debug pass (formerly CHORE-009, "Phase 3 investigation" instance).

## Consequences

`PROBLEM.md` documented the failure mode and hypothesis that ultimately led to discovering
the real root cause: the implementation was a binary-pixel scheme, not genuine DCT-domain
encoding. See
[F-20260831-03-genuine-phase3-dct-domain-encoding](F-20260831-03-genuine-phase3-dct-domain-encoding.md)
for the resolution. `PROBLEM.md` itself has since been marked resolved.
