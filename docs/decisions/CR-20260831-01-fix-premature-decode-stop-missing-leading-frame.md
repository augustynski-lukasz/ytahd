# CR-20260831-01 — Fix Premature Decode Stop on Missing Leading Frame

**Date:** 2026-08-31 **Status:** Implemented
**Area:** `YTAHD.Core.Core.DecodeRecoveryPolicy`, `DecodedFrameAccumulator`

## Context

The decoder's stop condition incorrectly treated a missing first required frame (frame
index 0) as terminal, causing premature decode failure even when later valid payload data
was still recoverable.

## Decision

Fixed `DecodeRecoveryPolicy` and the accumulator so recovery no longer exits early when
frame 0 is absent but later payload data is still recoverable (formerly BUG-002, "premature
stop" instance).

## Consequences

Necessary for correct behavior under packet loss/reorder conditions. This fix alone was not
sufficient to make the (then binary-pixel) Phase 3 path production-safe — see the
investigation in `PROBLEM.md`, resolved by
[F-20260831-03-genuine-phase3-dct-domain-encoding](F-20260831-03-genuine-phase3-dct-domain-encoding.md).
