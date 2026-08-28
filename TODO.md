# YTAHD TODO (FEAT-/BUG-/CHORE- IDs)

This file lists planned work using FEAT-/BUG-/CHORE- identifiers. Completed items include date and git commit hash.

CHORE-001 — Scaffold project and initial csproj

- Status: completed
- Done: 2026-08-28 (commit 221feedeb216300eae21441136661f3f3b55361f)

FEAT-001 — Implement Phase 1 encoder (monochrome 16×16 macroblocks)

- Status: completed
- Done: 2026-08-28 (commit 221feedeb216300eae21441136661f3f3b55361f)

FEAT-002 — Add FFmpeg wrapper abstraction and concrete implementation

- Status: completed
- Done: 2026-08-28 (commit 221feedeb216300eae21441136661f3f3b55361f)

FEAT-003 — Add modulation interfaces and BinaryGridModulator (Phase1)

- Status: completed
- Done: 2026-08-28 (commit 221feedeb216300eae21441136661f3f3b55361f)

FEAT-004 — Add FSK audio generator helper

- Status: completed
- Done: 2026-08-28 (commit 221feedeb216300eae21441136661f3f3b55361f)

FEAT-005 — Implement CLI (`encode` / `decode`)

- Status: completed
- Done: 2026-08-28 (commit 221feedeb216300eae21441136661f3f3b55361f)

FEAT-006 — Add xUnit tests and fake FFmpeg for CI-free testing

- Status: completed
- Done: 2026-08-28 (commit 221feedeb216300eae21441136661f3f3b55361f)

CHORE-002 — Create .gitignore

- Status: completed
- Done: 2026-08-28 (commit 221feedeb216300eae21441136661f3f3b55361f)

FEAT-007 — Create DESCRIPTION.md

- Status: completed
- Done: 2026-08-28 (commit 221feedeb216300eae21441136661f3f3b55361f)

CHORE-003 — Commit initial changes to git

- Status: completed
- Done: 2026-08-28 (commit 221feedeb216300eae21441136661f3f3b55361f)

FEAT-008 — Implement `DecoderEngine` full decode path

- Status: completed
- Done: 2026-08-28 (commit 4cea3050da7051cb973d0b3d22fd961b24ba6935)

CHORE-004 — Split unit tests into per-class test files

- Status: completed
- Done: 2026-08-28 (commit 6a626bde038573e26d4013ac4d8376f5b0b73456)

CHORE-005 — Add zip encode/decode/unzip integration test

- Status: completed
- Done: 2026-08-28 (commit 0148e89216db1021017d8535bb3695c8747c9398)

BUG-001 — Fix decoder frame de-dup run handling

- Status: completed
- Done: 2026-08-28 (commit 7766cbef21df36a859427673562ad6ba6a9b232e)

FEAT-009 — Design and add ECC / synchronization frames (Reed–Solomon / fountain)

- Status: not-started

FEAT-010 — Add calibration border, pilot palette, and test patterns

- Status: not-started

FEAT-011 — Implement Phase 2/3 advanced modulation modes (multi-channel/QAM-like)

- Status: not-started

FEAT-012 — Add CI pipeline (GitHub Actions) to run `dotnet test`

- Status: completed
- Done: 2026-08-28 (commit d50a795a4cc717ec9f67da1e8988904f200b77b1)

FEAT-013 — Tag v0.1.0 and push to remote (release prep)

- Status: not-started

FEAT-014 — Add per-frame metadata (frame index, payload length, SHA-256)

- Status: completed
- Done: 2026-08-28 (commit c139f5878af6afea3a007948b30e6c96b3897cd9)

FEAT-015 — Validate decoded frame hashes before accepting payload bits

- Status: completed
- Done: 2026-08-28 (commit 5d941ad91f86df9e2cc967e16c7a9d2be7360fee)

FEAT-016 — Add erasure coding for lost-frame recovery (Reed-Solomon / fountain)

- Status: completed
- Done: 2026-08-28 (commit 40dacb2)

FEAT-017 — Add standalone performance analysis project for algorithm overhead and bandwidth stats

- Status: completed
- Done: 2026-08-28 (commit cfd1189)

Notes:

- Use FEAT-XXX for feature work, BUG-XXX for bug fixes, CHORE-XXX for maintenance tasks.
- Update this file and the tracked todo list when items progress.
