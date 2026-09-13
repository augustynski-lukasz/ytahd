# CR-20260913-05 — Resolve `DecodeThresholds`: wire as advisory decode verdict

**Date:** 2026-09-13 **Status:** Accepted
**Area:** `YTAHD.Core/Core/DecodeMetrics.cs` (`DecodeThresholds`), `YTAHD.Cli/Program.cs`,
`YTAHD.Tests` (`DecoderMetricsTests`)

## Context

`docs/REVIEW.md` Review 1 finding F3: `DecodeThresholds.IsSatisfiedBy`
(`MaxInvalidPacketRatio` / `MinDuplicateRunQuality` / `MaxRecoveredGroups`) is referenced
only by its own definition and `DecoderMetricsTests`. Neither `DecoderEngine`,
`DecodeStreamOrchestrator`, nor the CLI consults it — decode results are not gated on these
thresholds. Dead enforcement-looking code in a durability-critical area is worse than no
code: a future reader can reasonably assume invalid-packet ratio is enforced somewhere when
it is not. Review 1's direction: decide, don't leave it ambiguous.

## Decision

Wire it as an **advisory verdict in the CLI decode summary** — not as a hard gate:

- After aggregation, the orchestrator (or CLI summary builder) evaluates
  `DecodeThresholds.IsSatisfiedBy(metrics)` and surfaces the result in the decode summary,
  e.g. `quality=within-thresholds` / `quality=degraded (invalidPacketRatio=…, …)`.
- It must **not** fail the decode: integrity (`integrity=passed/failed`) remains the sole
  authority for output acceptance. The thresholds encode operational experience about
  _quality_, and a stream can legitimately decode with degraded quality metrics while still
  being byte-exact (parity recovery legitimately produces high recovered-group counts).
- Keep and extend `DecoderMetricsTests` for the advisory path; add a CLI-summary assertion.

Rationale for wiring over deleting: the metrics already exist, the thresholds encode real
operational experience, and an explicit advisory verdict turns misleading dead code into
honest diagnostics — consistent with the project's diagnostics bar.

## Consequences

- `DecodeThresholds` becomes live, clearly-advisory code; no ambiguity about enforcement.
- No decode behavior change for intact streams; degraded streams gain an explicit
  `quality=` line without any gating change.
- Risk: threshold defaults may flag legitimate heavy-parity-recovery decodes as "degraded";
  if that proves noisy in practice, tune the defaults in a follow-up commit under this ADR
  (advisory output only, so mis-tuning cannot break decodes).
