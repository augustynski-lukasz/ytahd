# Copilot instructions for YTAHD

## Project overview

- This repo is a .NET 8 video steganography / data-in-video project.
- Main solution: `YTAHD.sln`
- Core library: `YTAHD.Core`
- CLI app: `YTAHD.Cli`
- Tests: `YTAHD.Tests`
- Perf tool: `YTAHD.Perf`

## Agent routing

- Use the `backend-dev` custom agent for backend C# implementation work in `YTAHD.Core`, `YTAHD.Cli`, `YTAHD.Tests`, `YTAHD.Perf`, and `probe` when the task involves feature development, bug fixes, protocol or modulator changes, FFmpeg integration, performance work, or related tests.
- The `backend-dev` agent must follow this repository's real FFmpeg validation, lossy-codec durability, architecture, and ADR requirements.
- Keep lightweight project questions, documentation-only edits, and coordination in the main agent unless delegating to `backend-dev` would improve context isolation or execution.

## Architecture and conventions

- Prefer working through the app/service layer (`YTAHD.Core.Application`) instead of reaching directly into implementation details when possible.
- Keep modulator logic and geometry together. Shared geometry lives in `ModulatorGeometry`; shared app config lives in `VideoCodecOptions`.
- Do not reintroduce long parameter lists for modulator methods when a value object already exists.
- Prefer the strategy abstraction already in place: `IModulator`, `EncoderEngine`, `DecoderEngine`, `YtahdCodecService`, and the FFmpeg wrapper factory.
- Preserve the existing separation between video-container handling and data modulation. FFmpeg is the codec layer; modulation is the data layer.

## Real FFmpeg contract

- The current production baseline is a real H.264 / `libx264` encode/decode path.
- The project must use a real FFmpeg process for end-to-end validation; synthetic-only tests are not enough for final verification.
- Default behavior is to resolve `ffmpeg` from `PATH`.
- If an explicit path is needed, support the explicit override via the CLI and keep the default fallback behavior intact.
- Never assume bit-perfect decode from lossy H.264 output. Decode logic must be tolerant of drift, duplicated frames, weak payloads, and imperfect luminance separation.

## Quality bar for changes

- Add or update a regression test before or alongside a functional fix.
- Prefer small, root-cause-focused fixes over broad refactors.
- When changing decoding logic, validate against real FFmpeg round-trips, not only fake wrapper tests.
- When changing modulator APIs, keep compatibility patterns safe unless a cleaner migration is explicitly intended.

## Validation commands

Run the minimal relevant command before claiming a fix is ready.

### Full project validation

```powershell
dotnet test YTAHD.Tests/YTAHD.Tests.csproj --logger "console;verbosity=minimal"
```

### CLI smoke check

```powershell
dotnet run --project YTAHD.Cli -- encode sample.bin out.mp4 --modulator phase1 --ffmpeg-path "<path-to-ffmpeg>"
dotnet run --project YTAHD.Cli -- decode out.mp4 recovered.bin --modulator phase1 --ffmpeg-path "<path-to-ffmpeg>"
```

## Important project notes

- Lossy compression is the core constraint. Binary assumptions from perfect black/white frames are invalid under real H.264 output.
- The decoder must score several luminance candidates and tolerate a range of values instead of expecting a single threshold.
- Geometry assumptions (row stride, frame width/height, payload size, header size, border width) are part of the protocol contract and must stay aligned with the real decoded FFmpeg frames.
- The CLI and service layer should continue to make the “real ffmpeg + PATH fallback” workflow easy and predictable for future work.

## Preferred change patterns

- Keep data-encoding and decoding contract logic inside the relevant engine or modulator.
- Centralize duplicated config into shared types instead of spreading scalar settings through many methods.
- Prefer object-based configuration for encode/decode operations when multiple values are passed together.
- When refactoring, keep the public API stable unless a specific migration is already underway.

## Working style

- Be explicit about assumptions and validation evidence.
- Write clear, minimal code comments only when they capture a non-obvious protocol reason.
- Keep project documentation and TODO status aligned with the actual code state.

### Markdown file naming

| Type                                            | Case          | Examples                                        |
| ----------------------------------------------- | ------------- | ----------------------------------------------- |
| Well-known standalone docs (root or `docs/`)    | **UPPERCASE** | `README.md`, `PLAN.md`, `BACKLOG.md`            |
| ADR / decision slugs (`docs/decisions/`)        | lowercase     | `F-20260423-01-design-system.md`                |
| Tool-prescribed files (names fixed by the tool) | lowercase     | `.github/copilot-instructions.md`, `*.agent.md` |

Rule: if a Markdown file is a standalone document you would direct someone to, it is UPPERCASE. If it is a slug or tool-managed file, it stays lowercase.

---

## ADR — mandatory for every non-trivial change

All history, decisions, and open items live in:

```
docs/
  BACKLOG.md          ← open items only (features not yet done, tech debt)
  decisions/          ← one ADR file per resolved item or significant change
```

### When to create an ADR

An ADR records a decision **at the moment it is made** — not only after implementation.

- **Design-time ADR (`Status: Accepted`):** when a non-trivial design or architectural
  direction is decided before implementation starts, create the ADR immediately. It may be
  committed on its own. When the implementation lands, update the same ADR in that commit
  (`Status → Implemented`, consequences refreshed with what actually happened).
- **Implementation ADR (`Status: Implemented`):** for work done without a prior design ADR,
  create the ADR in the same commit as the code.

Always have an ADR (design-time or implementation) for:

- Any phase or feature implementation (`F-YYYYMMDD-NN`)
- Any technical debt resolution (`TD-YYYYMMDD-NN`)
- Any non-trivial fix, refactor, or architectural decision (`CR-YYYYMMDD-NN`)

Trivial changes (typo fixes, copy changes, version bumps) do not need an ADR.

Design decisions must not accumulate in `BACKLOG.md` or plan documents — the backlog holds
the open item and links to the ADR; the ADR holds the decision.

### ADR file naming

All ADRs follow the same date + ordinal pattern:

| Source         | Filename pattern               | Example                                   |
| -------------- | ------------------------------ | ----------------------------------------- |
| Feature        | `F-{YYYYMMDD}-{NN}-{slug}.md`  | `F-20260423-01-design-system.md`          |
| Technical debt | `TD-{YYYYMMDD}-{NN}-{slug}.md` | `TD-20260501-01-upgrade-node-20.md`       |
| Change request | `CR-{YYYYMMDD}-{NN}-{slug}.md` | `CR-20260501-01-tailwind-v4-migration.md` |

All files go in `docs/decisions/`. Slug = lowercase-hyphenated short title.

For the ordinal `{NN}`: check the highest existing `{NN}` for the same prefix and today's date in `docs/decisions/` and use the next one.

### ADR file format

```markdown
# {ID} — {Full Title}

**Date:** YYYY-MM-DD **Status:** Accepted / Implemented / Superseded
**Area:** {files / subsystems affected}

## Context

Why this was needed / what problem existed.

## Decision

What was decided and implemented.

## Consequences

Impact, trade-offs, known limitations, follow-up items.
```

### ADR debugging resolution sections (mandatory for bug-fix ADRs)

When an ADR resolves a bug, a failing test, or a non-trivial fix, add these sections
**after "Decision"** (see `docs/PROBLEM.md` for worked examples). A design-only ADR
omits them; a fix ADR must not:

- **Observed failure** — the concrete symptom: failing test name (or CLI error),
  exact assertion/error message, and the confusing red herrings encountered (e.g. an
  earlier run that contradicted the current behavior).
- **Root cause** — the actual defect(s), stated precisely (which file, which code path,
  what the wrong logic was). If there are multiple stacked defects, list each separately
  and explain how they masked each other.
- **Resolution** — what changed for each defect, and why the change is backward-safe
  (or explicitly not). One bullet per defect, mirroring the root-cause list.
- **Lessons** — non-obvious debugging traps worth remembering: wrong assumptions that
  cost cycles, stale-binary pitfalls, discriminator-test coverage gaps. Only include
  lessons that were actually paid for in this session, not generic advice.

These sections record the debugging path, not the design rationale — "Context/Decision/
Consequences" say _what and why_, these say _how it broke and how that was found_.

Status lifecycle: `Accepted` (decision made, code not landed) → `Implemented` (code landed;
update in the same commit as the code) → `Superseded` (link the replacing ADR).

### BACKLOG.md — open items

- When starting new work on a backlog item, **do not modify `BACKLOG.md` yet** — wait until it's resolved.
- When work is complete: **delete the item from `BACKLOG.md`** and create an ADR in `docs/decisions/`.
- When new debt or a new feature idea is discovered: **add it to `BACKLOG.md`** with a problem description.
- `BACKLOG.md` must only ever contain open/unresolved items. Done = deleted from this file.

### ADR must be in the same commit

Implementation ADRs (and the `Accepted → Implemented` status update of a design-time ADR)
**must be staged in the same commit as the code changes** — never as a follow-up commit.
A design-time ADR with `Status: Accepted` may be committed on its own before any code exists.

---
