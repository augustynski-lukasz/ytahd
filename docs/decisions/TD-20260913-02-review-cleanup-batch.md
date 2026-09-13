# TD-20260913-02 — Review cleanup batch (ffprobe resolution, tile-basis cache, NormalizeModulator, test-output.txt)

**Date:** 2026-09-13 **Status:** Implemented
**Area:** `YTAHD.Core/Core` (`DecoderEngine`, `EncoderEngine`),
`YTAHD.Core/Modulation/MotionTileBasis.cs`, repo root

## Context

`docs/REVIEW.md` Review 1 minor/nit findings that are individually trivial but share one
theme: small duplication or hygiene debt with no design content. Batched per Review 1's
plan (R6); F11 (decoder resolution) is excluded — it has design content and its own ADR
(`CR-20260913-07`); F6 (manifest copy ordinal) is excluded — it is a wire-format change that
must ride with the next protocol revision (tracked in `docs/BACKLOG.md`).

## Decision

Four micro-changes, each independently revertable, shipped as one batch commit:

1. **F8 — single ffprobe resolution.** Delete
   `DecoderEngine.ResolveFfprobeExecutablePath` and call
   `FFmpegProbe.ResolveFfprobePath` (it already handles the explicit-path and PATH cases,
   including the CR-20260911-01 directory-override handling the engine's copy can miss).
2. **F10 — cache per-profile tile tables.** In `MotionTileBasis`, cache
   `BuildOffsetTable(profile)`/`BuildTexture(profile)` results in a
   `ConcurrentDictionary<MotionTileProfile, …>` (the profile is a small readonly record
   struct, safe as a key) so `GetAxisOffsets(profile)`/`GetTexture(profile)` stop rebuilding
   tables per frame/cell-search on the Phase 4 hot path; make the legacy static consts
   delegate to `MotionTileProfile.Default` so there is one source of truth.
3. **F12 — hoist `NormalizeModulator`.** The identical `private static NormalizeModulator`
   in `EncoderEngine` and `DecoderEngine` moves to one shared internal helper next to
   `FrameBitDecoderFactory`.
4. **F13 — remove `test-output.txt`** from the repo root (captured CLI output, referenced
   by no doc).

Test strategy: full build + full suite green; `MotionVectorModulatorTests` /
`MotionFrameBitDecoderTests` cover the tile-basis cache (determinism must be unchanged —
cached tables must be read-only); `FFmpegCapabilitiesTests` and path-resolution tests cover
the ffprobe consolidation.

## Consequences

- One source of truth for ffprobe resolution, tile tables, and modulator normalization;
  Phase 4 hot path stops reallocating lookup tables per frame.
- No protocol, wire-format, or public-API change; rollback is a plain revert.
- The tile-basis cache is the only behavioral-risk item (shared mutable state); mitigated by
  exposing the cached tables as read-only and the existing determinism tests.

## Validation

- ADR-named suites green: `MotionVectorModulatorTests`, `MotionFrameBitDecoderTests`,
  `FFmpegCapabilitiesTests`, `Phase4TileSizeSweepTests` (33/33 focused) — tile-table
  determinism unchanged under the cache.
- Full suite: 331/331 passed in 1 m 08 s.
- Implementation notes: `test-output.txt` was never git-tracked (already ignored), so F13
  was a disk deletion only. The cached tables are exposed only through cloning getters
  (`GetAxisOffsets`) or as the shared read-only `Texture` array, preserving the
  determinism contract; `GetTexture(profile)` returns the cached array by reference —
  consumers treat it as read-only, matching the pre-existing `Texture` static's contract.
