# CR-20260831-04 — Update README and TODO for the Genuine Phase 3 Algorithm

**Date:** 2026-08-31 **Status:** Implemented
**Area:** `README.md`, `TODO.md`

## Context

Following the genuine Phase 3 DCT rewrite (F-20260831-03), the README's Phase 3 section
still described the old vague "top-left cosine waves" concept without the actual algorithm,
and `TODO.md` needed to reflect the current state.

## Decision

Rewrote the README Phase 3 section with the full algorithm specification: the 8 carrier
positions, the IDCT synthesis formula, the forward DCT decode formula, the coefficient-sign
noise-robustness argument, and the 1-byte-per-block capacity formula with a worked 4K
example. Updated the "Current implementation status" summary to state that Phase 1, 2, and
3 are all production-validated. Updated `TODO.md` accordingly (formerly CHORE-012).

## Consequences

Project documentation now matches the actual production algorithm rather than the original
roadmap-level description. This is the last entry recorded in the retired `TODO.md`; from
this point forward, backlog tracking uses `docs/BACKLOG.md` and per-change ADRs in
`docs/decisions/` (this file included).
