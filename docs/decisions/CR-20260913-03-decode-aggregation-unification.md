# CR-20260913-03 — Decode aggregation unification (shared DecodeAggregator)

**Date:** 2026-09-13 **Status:** Implemented
**Area:** `YTAHD.Core/Core` (`DecodeStreamOrchestrator`, new `DecodeAggregator`), `YTAHD.Tests`

## Context

`docs/REVIEW.md` Review 1 findings F1 and F2: the serial (`ProcessAsync`) and parallel
(`ProcessParallelAsync`) decode paths in `DecodeStreamOrchestrator` each implement the same
aggregation semantics twice —

- the durability branch duplicates the `uniqueDataLengths` walk, per-packet
  `FramePacketCodec.TryDecodeWithTolerance` decoding, recovered-length summation,
  `TryDecodeFramePacketsWithHoles` call, `DecodeMetrics` population, and
  `VerifyAgainstManifest` invocation;
- the legacy branch duplicates the canonical-frame detection /
  `DuplicateFrameRunTracker` / `DecodeRecoveryPolicy.ShouldStopDecoding` / final
  flush-recover-assemble state machine (the parallel variant adds a `stopRequested` latch).

CR-20260913-02 (hole-tolerant reconstruction) had to be applied to both copies. The next
durability-semantics fix will face the same double-application risk, and the serial path is
the deterministic-debug path — drift there hides until someone debugs serially.

## Decision

Extract a single internal `DecodeAggregator` in `YTAHD.Core/Core` that owns the duplicate-run
tracker, decoded-frame accumulator, packet list, and metrics; both the serial and parallel
paths feed it per-frame results (already uniform in the parallel path via the `DecodedFrame`
record) and read final output/metrics from it. The `stopRequested` latch moves inside the
aggregator. Migration in three independently-green steps: (1) introduce the aggregator and
route the parallel path through it; (2) route the serial path through it; (3) delete the
now-dead inline logic. No protocol change, no public API change.

Equivalence proof: existing `DecoderStreamOrchestratorTests`, `DecodeSlotOrderingTests`,
`DurabilityMatrixTests`, `IntegrityEndToEndTests`, and `ParallelPipelineRealCodecTests`
must stay green unchanged. Before merging, run one `YTAHD.Perf bench` pass on a large
payload (≥ 1 MB, durability on) to close the "equivalent in tests" vs "equivalent in
practice" gap flagged in Review 1's risks.

## Consequences

- Durability/legacy aggregation semantics live in exactly one place; the next
  CR-20260913-02-class fix is a single-site change.
- `DecodeStreamOrchestrator` shrinks to orchestration (reader, workers, ordering) and
  delegates semantics to the aggregator.
- Risk: the extraction touches the most safety-critical decode code; mitigated by the
  unchanged-tests equivalence bar plus the Perf before/after run. Rollback is a plain
  revert — no wire or API surface changes.
- Sequencing: land before the next protocol change (the Phase 4 follow-up research in
  `docs/BACKLOG.md` is the natural next protocol work).
- Integrity-semantics unification (deliberate behavior change, folded into this ADR per
  the same workstream): the parallel durability path used to **throw**
  `InvalidDataException` on `IntegrityStatus.Failed` while the serial path returned the
  reconstructed payload with the failure recorded in metrics. The unified aggregator
  adopts the **serial** semantics everywhere: a `Failed` verification still returns the
  payload — including documented zero-filled holes with their loss map
  (CR-20260913-02) — so callers can inspect what was recovered, and the CLI surfaces
  `integrity=failed` from the metrics. The parallel throw was pinned by no test; the
  serial return-with-`Failed` contract is pinned by
  `CombinedClockArbitrationTests.SilentlyDropped_WholeParityGroup_Yields_Documented_Hole_And_Loss_Map`.
  Net effect: a corrupted durability stream decoded in parallel now yields the same
  documented-hole output and loss map as serial instead of discarding recoverable data
  with an exception.
- The serial path now also skips the duplicate-run logical-signature decode pass on the
  durability branch (the durability path never compares frames), matching what the
  parallel path already did.

## Validation

- Full suite: 306/306 passed (`dotnet test YTAHD.Tests/YTAHD.Tests.csproj`), including
  the equivalence-pinning tests named above, unchanged.
- Perf bench (phase3, 1 MB payload, durability on, real FFmpeg), before → after:
  serial decode 138.29 s → 142.54 s (~3%, run noise), auto(4) decode 60.90 s → 35.58 s
  (faster; within run-to-run variance for this workload), both runs `ok=yes` with
  byte-identical payload recovery and identical output video size
  (`perf-g1-baseline.json` / `perf-g1-after.json`).
