# CR-20260831-05 — Migrate TODO.md to BACKLOG.md + ADR Structure

**Date:** 2026-08-31 **Status:** Implemented
**Area:** `TODO.md`, `docs/BACKLOG.md`, `docs/decisions/`

## Context

The project's customization instructions mandate tracking history and decisions via
`docs/decisions/` ADR files (one per resolved feature/fix/refactor) and open items via
`docs/BACKLOG.md`, rather than a single flat `TODO.md` with FEAT-/BUG-/CHORE- IDs.

## Decision

Converted the ~90 entries in `TODO.md` into dated, thematically-grouped ADR files under
`docs/decisions/` (`F-`/`TD-`/`CR-` prefixes per the ADR naming convention), each retaining
references to the original ID(s) for traceability. Moved the two still-open items
(warnings/SkiaSharp advisory cleanup, Phase 4 design) into `docs/BACKLOG.md`. Retired
`TODO.md` in favor of this structure.

## Consequences

Historical entries that shared an ID across unrelated changes (e.g. `BUG-002`, `FEAT-035`
were each reused 2–3 times in the original file for different work) were disambiguated by
date and content when mapping to ADRs; some closely related same-day items were grouped
into a single ADR rather than one-per-original-ID, to keep the ADR set navigable. All future
work must add a new ADR in the same commit as the code change, per the project's
customization instructions.
