# CR-20261006-01 — Precise durability-guarantee claims: remove `Fountain` placeholder, document XOR-parity contract (RM-09)

**Date:** 2026-10-06 **Status:** Implemented
**Area:** `YTAHD.Core/Core/DurabilityRedundancyMode.cs`, `YTAHD.Core/Core/DurabilityMatrixOptions.cs`,
`README.md`, `docs/ARCHITECTURE.md` (§8.2, §12.3), `docs/ROADMAP.md` (RM-09),
`YTAHD.Tests` (`DurabilityMatrixTests`)

## Context

ROADMAP RM-09 (from the external review): the durability layer's terminology invites
recovery-guarantee inferences the transport cannot provide. Verified code facts
(2026-10-06, re-confirmed from the 2026-09-16 survey):

- `DurabilityRedundancyMode` (`YTAHD.Core/Core/DurabilityRedundancyMode.cs`) declares three
  values: `Repeat`, `XorParity`, `Fountain`.
- `DurabilityMatrixOptions.RedundancyMode` defaults to `XorParity`; **no code path, test,
  or CLI option ever sets `Repeat` or `Fountain`** — the property is written nowhere and
  read nowhere (grep: only the declaration and the enum definition reference it).
- `DurabilityMatrixCodec` implements exactly one scheme: XOR parity with
  `ParitySymbolsPerGroup` parity symbols per group. There is no fountain code, no
  Reed–Solomon, and no repeat-mode implementation behind this enum.
- `F-20260830-04` (the durability ADR) says the design was kept "replaceable for future
  fountain-style strategies" — the enum values are that *anticipation*, not
  implementations.

The review's concern: a user seeing `DurabilityRedundancyMode.Fountain` (or a future CLI
exposing it) may infer fountain-code recovery guarantees (arbitrary erasure tolerance,
rateless repair) that the transport does not have. Dead enum values in a
durability-critical area are the same class of problem as Review 1's F3 (dead
enforcement-looking code): worse than absence, because they look like capability.

## Decision

**Remove the unimplemented enum values; keep the seam; document the actual contract.**

1. **`DurabilityRedundancyMode` is deleted** along with the
   `DurabilityMatrixOptions.RedundancyMode` property. Rationale for removal over
   keep-and-guard: the property is *already* non-functional — no setter, no reader, no CLI
   surface — so there is nothing to guard; keeping a write-only property whose values
   promise schemes that do not exist preserves the misleading surface RM-09 targets. The
   extension seam (`IDataDurabilityCodec`, §12.3) is unaffected: a future fountain or
   Reed–Solomon implementation re-adds a mode selector **in the same commit as its
   implementation**, with tests — the repo's standard rule.
2. **XOR parity's actual guarantee is documented** where the durability layer is described:
   - README durability section: states the plain-path and matrix guarantees explicitly —
     the matrix tolerates up to `ParitySymbolsPerGroup` missing symbols per group
     (default 1), wholly-lost groups become zero-filled holes with `integrity=failed`
     (never silent acceptance), and whole-payload correctness is *proven* by the manifest
     SHA-256, not assumed.
   - ARCHITECTURE §8.2: adds a "Guarantee contract" block distinguishing
     **implemented-and-proven** (XOR parity, per-symbol hashes, manifest verification —
     all under real-codec tests) from **not implemented** (fountain codes, Reed–Solomon,
     general erasure coding — extension paths only).
   - ARCHITECTURE §12.3: reworded — `RedundancyMode` no longer "anticipates modes beyond
     `XorParity`"; the seam is `IDataDurabilityCodec` itself, and a new scheme must land
     with its own mode selector + tests + ADR.
3. **No wire-format change.** `RedundancyMode` was never serialized (symbols carry
   `RedundancyLevel`, an unrelated per-symbol quality field that stays). Decode of
   existing streams is unaffected.

## Consequences

- The durability API now describes only what exists: options are `SymbolSize`,
  `GroupSize`, `ParitySymbolsPerGroup` — all live, all tested. No placeholder values.
- **Breaking API change** (source-level, minor): any external code setting
  `RedundancyMode` fails to compile. Nothing in-repo does (verified by grep); the CLI
  never exposed it. Acceptable at this project stage; called out here per convention.
- Future fountain/RS work starts from a clean slate: add the enum value (or a strategy
  type) together with the implementation, not before.
- Docs now state the guarantee precisely, satisfying RM-09's acceptance criterion:
  "docs state exactly what is proven; `Fountain` is either gone or explicitly
  non-functional" — it is gone.

## Validation

- `DurabilityMatrixTests` extended: options without `RedundancyMode` construct and
  compute the recovery threshold as before (`GroupSize - ParitySymbolsPerGroup`,
  clamped to ≥ 1); the existing threshold test stays green unchanged.
- Full build 0 errors; focused durability suites green.
- Grep sweep: no `Fountain`/`Repeat` references remain in source; docs mention fountain
  only as a *future extension path* (§12.3), never as a capability.
