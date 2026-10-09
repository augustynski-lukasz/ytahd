# CR-20261009-01 — Benchmark regression guardrails (RM-15)

**Date:** 2026-10-09
**Status:** Implemented
**Area:** `scripts/check-perf-regression.ps1` (new), `README.md` (Performance section),
`docs/ROADMAP.md` (RM-15 status)

## Context

RM-15's story: CI/local guardrails on benchmark results that detect material
regressions **without fragile fixed-time assertions**. `YTAHD.Perf bench --json`
already emits machine-readable results (pattern: `perf-g1-baseline.json` /
`perf-g1-after.json` from the Workstream G parallelism work), but nothing compared
them automatically — a throughput regression could land unnoticed.

The measured noise floor matters for the policy. Comparing the two committed G1 files
(identical code, identical machine, minutes apart): decode at `jobs=auto` swung
35–60% run-to-run (138 s vs 61 s decode in one pair), while serial encode stayed
within ~25% and serial decode within ~2%. Parallel-decode timing is dominated by
FFmpeg thread scheduling and is demonstrably the noisiest axis.

## Decision

- **Comparison script, not a test.** `scripts/check-perf-regression.ps1` compares two
  `bench --json` files (baseline vs current). It is a standalone PowerShell script
  with exit codes (0 pass / 1 regression / 2 usage error) so it can run locally or in
  CI without coupling benchmark wall-time into the unit-test suite — real-codec
  benchmarks take minutes and would make `dotnet test` unusably slow and flaky.
- **Threshold policy (percentage-based, per run key):**
  - Runs are grouped by `modulator|payloadBytes|requestedJobs`; repeats of the same
    configuration are **summed** (throughput summed across repeats is the noise-robust
    aggregate — repeats exist to average noise).
  - **Serial runs (resolvedJobs ≤ 1): 20%** throughput-loss threshold.
  - **Parallel runs (resolvedJobs > 1): 35%** — sized above the observed FFmpeg
    thread-scheduling noise floor so the gate flags regressions, not weather.
  - **Aggregate gate: 10%** on the mean of all per-run ratios — catches the
    "quietly 12% slower everywhere" pattern that slips under every per-run gate.
  - Only **encode/decode throughput** is gated in v1. Wall-clock seconds, CPU%, and
    memory metrics are reported but not gated: wall-clock is redundant with
    throughput, CPU% is a scheduling artifact, and memory gating needs the RM-10
    peak-RSS follow-up to establish a stable measurement first.
- **Environment guard:** a mismatch in `logicalProcessors` (and any future
  environment key) fails the comparison outright — throughput numbers are not
  comparable across machines, so the correct action is re-baselining, not a
  false regression.
- **Missing/extra run keys** are failures/warnings respectively: the current run must
  cover the baseline's configurations for the comparison to be meaningful.
- **Re-baseline workflow** is printed on failure: re-run `bench --json` and commit the
  new baseline when a slowdown is intentional or environmental.

## Consequences

- Material throughput regressions are now detectable with one command, with
  thresholds grounded in measured run-to-run noise rather than guesswork.
- Known limitation: the policy is calibrated on the G1 dataset (phase3, 1 MB,
  6 logical processors). Wider modulator/payload matrices may need threshold
  retuning — the parameters are exposed (`-ThresholdPercent`,
  `-ParallelThresholdPercent`, `-AggregateThresholdPercent`) so callers can adjust
  without editing the script.
- Known limitation: no CI wiring yet. The repo's CI (`dotnet-test.yml`) runs on
  shared GitHub runners where absolute throughput is not comparable to any committed
  baseline; a CI job would need to baseline on the runner itself (e.g. compare
  against the previous run's artifact) — deferred until the benchmark matrix is
  worth that infrastructure.
- The script is ASCII-only (PS 5.1 em-dash parse hazard) and uses file-tool-safe
  encoding patterns per repo conventions.
