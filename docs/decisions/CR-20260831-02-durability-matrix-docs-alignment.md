# CR-20260831-02 — Durability Matrix Documentation Alignment

**Date:** 2026-08-31 **Status:** Implemented
**Area:** `README.md`, `TODO.md`, probe `README.md`

## Context

The README and TODO needed to accurately describe the actual implementation stage of the
Data Durability Matrix (service-integrated, real-FFmpeg-verified) rather than an outdated
"prototype-only" description.

## Decision

Updated `README.md` and the probe `README.md` to describe the current smoke-test workflow,
and aligned `TODO.md` with the verified service-integration status. Re-ran the full test
suite against the final state before commit (formerly CHORE-011).

## Consequences

Documentation now matches the real implementation status: the durability matrix is
integrated into the service path and verified by the real FFmpeg regression, not merely a
standalone codec prototype.
