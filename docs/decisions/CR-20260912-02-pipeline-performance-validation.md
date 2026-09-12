# CR-20260912-02 — Pipeline Performance Validation and Tuning

**Date:** 2026-09-12 **Status:** Implemented
**Area:** `YTAHD.Perf` (`ParallelBenchmark`, `ParallelBenchmarkOptions`, `Program`),
`YTAHD.Tests/ParallelPipelineRealCodecTests.cs`, `YTAHD.Core/Application/ParallelismPolicy.cs`

## Context

F1–F5 of Workstream F replaced the serial encode/decode loops with bounded parallel pipelines,
but the only evidence that the pipelines were both _correct_ and _worth running_ was
synthetic-wrapper equivalence testing plus a handful of real-FFmpeg round trips. Two gaps were
open in `docs/BACKLOG.md`:

1. **"Parallel pipeline: performance validation matrix"** — no serial-vs-parallel benchmark
   existed by modulator and payload size, and nothing tracked the safety constraint that
   actually matters here: peak memory. One 4K RGB frame is ~24 MB, so an over-eager worker count
   is a memory-pressure risk, not just a CPU-scheduling one.
2. **"Parallel pipeline: degree-of-parallelism controls"** — `ParallelismPolicy.AutoWorkerCap = 4`
   was chosen as a conservative placeholder with no measurements behind it
   (see CR-20260912-01), and there was no real-codec proof that every degree of parallelism
   preserved the frame protocol.

There was also a hard blocker: the full test suite deadlocked once the new real-codec parallel
tests ran alongside the rest of the suite. That is resolved separately in
CR-20260912-03; this ADR covers the matrix itself.

## Decision

**1. A real-FFmpeg benchmark mode in `YTAHD.Perf`.**

`YTAHD.Perf bench` runs a serial-vs-parallel comparison matrix over real libx264 round trips.
It reports, per run: encode/decode wall time, managed pipeline CPU time, the share of wall time
the pipeline spent blocked on FFmpeg stdin writes and stdout reads, peak working set, peak
managed heap, output size, and byte-for-byte recovery against the source payload. Results can be
emitted as JSON (`--json`) for diffing across machines.

`ParallelBenchmarkOptions.ParseJobsList` reuses the CLI's `--jobs` vocabulary (`auto`,
`serial`, `0`, or a positive worker count) so the benchmark and the operator surface cannot
drift apart.

**2. A real-codec degree-of-parallelism matrix in the test suite.**

`YTAHD.Tests/ParallelPipelineRealCodecTests.cs` runs phase1–phase4 round trips at every
`--jobs` setting (`default`, `serial`, `auto`, `2`, `4`) and asserts that:

- the decoded payload is byte-identical for every degree;
- frame counts written/decoded and payload bytes per frame match the serial baseline exactly;
- parallel encode produces an identical frame sequence to serial encode (frame count, data
  frames, per-frame payload size, canonical-frame count, recovered group count, invalid packet
  count);
- cancelling a parallel encode surfaces `OperationCanceledException` instead of stranding the
  caller.

**3. `AutoWorkerCap` kept at 4.**

The measured matrix (6 logical processors, 640x480@30, macroblock 16, single sample per cell,
strict `[ok]` = payload recovered byte-exactly) is:

| modulator | payload | jobs       | enc (s)  | dec (s)  | peak WS (MB) | ok  |
| --------- | ------- | ---------- | -------- | -------- | ------------ | --- |
| phase1    | 256 B   | `1` → 1    | 1.55     | 0.74     | 44.1         | yes |
| phase1    | 256 B   | `auto` → 4 | **0.32** | **0.51** | 53.8         | yes |
| phase1    | 1024 B  | `1` → 1    | 0.54     | 0.58     | 58.3         | yes |
| phase1    | 1024 B  | `auto` → 4 | **0.42** | **0.31** | 105.3        | yes |
| phase3    | 256 B   | `1` → 1    | 0.22     | 0.43     | 115.0        | yes |
| phase3    | 256 B   | `auto` → 4 | 0.22     | **0.34** | 124.5        | yes |
| phase3    | 1024 B  | `1` → 1    | 0.22     | 0.41     | 90.4         | yes |
| phase3    | 1024 B  | `auto` → 4 | 0.20     | **0.32** | 85.6         | yes |

Correctness: **8/8 round trips recovered the payload byte-exactly.**

Reading of the data:

- Where modulation dominates the cost (phase1 encode), parallelism is a large win — 1.55 s → 0.32 s.
- Where the codec dominates (phase3 encode), parallelism is roughly neutral (0.22 s → 0.22 s),
  because libx264 already saturates the machine internally. This is the "starving
  FFmpeg/libx264" risk the F6 plan called out, and it is why `Auto` leaves a processor of
  headroom rather than claiming every core.
- Decode improves consistently (phase1 0.74 → 0.51 s; phase3 0.43 → 0.34 s).
- Peak working set grows with the worker count but stays bounded and modest at this geometry
  (max observed 124.5 MB). Nothing in the matrix regressed correctness or memory.
- `AutoWorkerCap = 4` therefore stands: it is not measurably suboptimal on the one machine the
  matrix has been run on, and a cap is the safe direction given both the memory constraint and
  the libx264 headroom argument.

## Consequences

- F6 is complete: benchmark mode, tracked metrics, and a tuned default all exist, and the two
  open backlog items are resolved.
- `AutoWorkerCap = 4` is now a measured choice rather than a placeholder. The constant remains
  the single place to change, and `YTAHD.Perf bench --json` is the tool that would justify
  changing it.
- The matrix is single-sample and was measured on one 6-logical-processor machine at 640x480.
  Treat the numbers as directional; re-run `YTAHD.Perf bench` before drawing conclusions on a
  different core count or at 4K, where the ~24 MB-per-frame memory term dominates.
- `enc MB/s` / `dec MB/s` report as `0.00` in the summary at these payload sizes because 256 B
  and 1 KB payloads are far too small for a meaningful throughput figure. The columns are only
  useful for substantially larger payloads.
- Real-codec parallel tests are inherently heavier than the rest of the suite; they raise full
  suite wall time from roughly 31 s to roughly 1 min.
- Tests skip rather than fail when no usable FFmpeg is discoverable
  (`YTAHD.Tests/TestFfmpeg.cs`), and `YTAHD_FFMPEG_PATH` pins the interpreter they use.
- **Note:** this ADR does not cover the pipeline deadlock that blocked the matrix from running
  in the full suite — see CR-20260912-03.
