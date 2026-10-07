# CR-20261007-03 — YouTube transit validation harness scaffolding (RM-01)

**Date:** 2026-10-07 **Status:** Accepted
**Area:** `docs/TRANSIT-HARNESS.md`, `docs/experiments/README.md`, `docs/ROADMAP.md`

## Context

The core claim — _encode → upload → YouTube processing → retrieve → decode, repeatably_ —
is the only thing separating YTAHD from a local round-trip demo, and nothing in the repo
validated it. RM-01 asks for a canonical, repeatable harness whose runs become published,
versioned evidence. The actual uploads are manual (a Google account, unlisted videos);
what the repo can own is the procedure, the record format, and the acceptance bar.

## Decision

- `docs/TRANSIT-HARNESS.md` — the canonical loop (encode → upload → wait → retrieve via
  `yt-dlp` → decode → SHA-256 verify), the exact commands, the experiment-record template
  (`E-YYYYMMDD-NN-<slug>.md`), and the one-axis-at-a-time variation plan (modulator,
  payload size, durability, source codec, rendition, retrieval method, error model).
- `docs/experiments/` — the record directory (created, with a README stating the
  commit-even-when-failed rule). No records yet; the first session is pending.
- Decode steps use the RM-03 exit-code gate (`--expected-bytes`) so a truncated transit
  recovery fails the run automatically instead of relying on eyeballing the summary.

## Consequences

- RM-01's remaining work is running sessions and committing records — user-driven, not
  code. RM-02 (README command chain) and RM-04 (results table) are written from those
  records and stay blocked until at least one record per modulator exists.
- The harness bakes in the current CLI surface (tiers, `--durability`, `--expected-bytes`);
  if the CLI changes, the harness doc updates in the same commit.

## Lessons

- Evidence infrastructure is code-adjacent but not code: the deliverable is a procedure
  someone can follow months later and get comparable numbers, so determinism (fixed
  payloads, recorded options, recorded renditions) matters more than automation.
