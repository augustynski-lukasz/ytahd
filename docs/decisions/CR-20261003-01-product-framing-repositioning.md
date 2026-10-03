# CR-20261003-01 — Reposition YTAHD as a research transport experiment (RM-05/RM-06)

**Date:** 2026-10-03 **Status:** Accepted
**Area:** `README.md`, `DESCRIPTION.md`, `docs/ROADMAP.md` (RM-05, RM-06)

## Context

External review findings RM-05/RM-06 (`docs/ROADMAP.md`, epic E2 "Product framing"): the
project title "YouTube as a Hard Drive" is memorable but the term **hard drive** carries
expectations about durability, stable access, predictable cost, privacy, and recoverability
that YouTube cannot guarantee. The README currently makes claims the evidence does not
back:

- The "System Architecture Principles" section opens with _"To guarantee 100% data
  integrity upon decoding…"_ — but the repo's own ADR
  `F-20260914-03-long-stream-regression-coverage.md` documents that the plain decode path
  silently truncated a 256 KB phase-2 payload with exit 0, and README's own Reliability
  section says integrity is only **verified** (and repairable) under `--durability`.
- The one-paragraph description under "Current implementation status" says the stream is
  "optimized to survive YouTube's lossy compression … allowing files to be archived and
  retrieved directly from video hosting platforms" — yet no YouTube transit validation
  exists (ROADMAP epic E1 is entirely open); the validated claim is local real-FFmpeg
  round-trips only.
- Nothing tells a user what the channel is *un*suitable for.

An unqualified "storage" framing invites the exact failure the project prides itself on
avoiding elsewhere (honest diagnostics): users entrusting irreplaceable data to an
experimental channel. This is a credibility risk, not a criticism of the experiment.

## Decision

Reframe positioning as a **research-grade, self-verifying binary-over-video
transport/archive experiment designed for lossy video-transcoding platforms** — keep the
memorable name, qualify the claim. Documentation changes only; no code, protocol, or CLI
behavior change.

1. **README top section (RM-05):** introduce a short positioning block directly under the
   title: one-sentence framing (the "research-grade… experiment" language), followed by an
   explicit research-status caveat — validated today means _local real-FFmpeg round-trips_;
   YouTube transit is not yet validated (`docs/ROADMAP.md` E1 tracks that evidence).
2. **README "What it is NOT for" section (RM-06):** an explicit unsuitability list, placed
   immediately after the positioning block:
   - confidential or private data (the payload is hidden only by obscurity inside a
     public video; anyone with the video URL and this decoder can recover it — there is no
     encryption and no access control);
   - the only copy of valuable data;
   - data requiring guaranteed retention or retrieval (videos can be removed, region-
     blocked, or re-processed by the platform at any time);
   - content that could violate platform rules (loss of the video loses the payload);
   - storage with predictable effective cost, retrieval latency, or bandwidth.
3. **Fix the two over-claims:**
   - "To guarantee 100% data integrity…" → reworded to state what actually holds: the
     layout constraints are what make integrity _achievable and verifiable_; byte-exact
     recovery is **verified** via `integrity=passed` under `--durability`, and is not
     guaranteed on the plain path for long streams.
   - "…allowing files to be archived and retrieved directly from video hosting platforms"
     → reworded to the experiment framing with the not-yet-validated-transit caveat.
4. **`DESCRIPTION.md` (RM-05):** the repository description keeps the project name but
   gains the experiment qualifier and drops the stale "SkiaSharp-based renderer" sentence
   (SkiaSharp was removed in TD-20260913-01).
5. **Scope guard:** `docs/SUGGESTIONS.md` (external review snapshot) and historical ADRs
   are append-only records and are **not** edited. ADR-internal mentions of "archive" in
   the technical sense (e.g. archival-scale payloads) are untouched.

## Consequences

- The README's first screen sets honest expectations before any command examples; the
  positioning distinguishes the project from a storage recommendation and places it as
  media-channel-coding / resilient steganographic transport research.
- Consistency with existing honesty mechanisms: the framing caveat points at the same
  reality the Reliability section and `integrity=` verdicts already surface; no statement
  contradicts `F-20260914-03`.
- Known limitation: the CLI banner/`RootCommand` description ("resilient video frames") is
  already neutral and needs no change; the GitHub repo "About" text is managed outside the
  repo and must be updated manually by the owner (suggest: the DESCRIPTION.md first line).
- Follow-on: ROADMAP RM-02/RM-03 will place the recommended command chain and the
  plain-path caveat nearby; this ADR only fixes positioning and claims.

## Validation

Docs-only change: `dotnet build` not required; validation is a README/DESCRIPTION review
against the acceptance criteria in `docs/ROADMAP.md` RM-05/RM-06 — positioning block
present, unsuitability list present, no unqualified "storage guarantee" sentences remain
(grep-verified for `100% data integrity`, `archived and retrieved`, `guarantee`).
