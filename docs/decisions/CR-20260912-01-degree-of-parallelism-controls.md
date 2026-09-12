# CR-20260912-01 — Degree-of-Parallelism Controls

**Date:** 2026-09-12 **Status:** Implemented
**Area:** `YTAHD.Core/Application` (`ParallelismPolicy`, `VideoCodecOptions`), `YTAHD.Core/Modulation`
(`InnerLoopParallelism`, `IParallelismConfigurable`, `DctModulator`), `YTAHD.Core/Core`
(`FrameBitDecoderFactory`, `MotionFrameBitDecoder`, `EncoderEngine`, `DecoderEngine`,
`DecodeStreamOrchestrator`), `YTAHD.Cli`, `YTAHD.Tests`

## Context

Workstream F replaced the serial encode/decode loops with bounded parallel pipelines, and
the inner modulator loops (Phase 3 block rendering, Phase 4 tile search) were parallelised
with `Parallel.For`. Two gaps remained:

1. **No operator control.** `VideoCodecOptions.MaxDegreeOfParallelism` existed since
   CR-20260911-06, but nothing in the CLI surfaced it. There was no way to run
   deterministically serial for debugging, and no way to pick a worker count by hand.
2. **No budget sharing.** The frame-level pipeline and the inner loops each independently
   claimed `Environment.ProcessorCount` workers. On a machine with _N_ logical processors that
   could put _N_ × _N_ runnable threads in flight, oversubscribing the CPU that FFmpeg/libx264
   already saturates internally. A worker count of `2` with a fully parallel phase-3 inner loop
   was strictly worse than a serial pipeline in practice.

## Decision

Introduce one place that turns a requested degree of parallelism into an effective worker
count, and make that number a shared CPU budget.

`ParallelismPolicy` (new, `YTAHD.Core/Application`) resolves requests against the logical
processor count:

| Request       | Effective frame workers                                       |
| ------------- | ------------------------------------------------------------- |
| `0` (default) | `1` — serial pipeline, inner loops keep the whole machine     |
| `1`           | `1` — deterministic debug mode, inner loops forced serial too |
| `Auto` (`-1`) | `max(1, min(4, ProcessorCount - 1))`                          |
| `n > 1`       | `min(n, 64)`                                                  |

`ResolveInnerDegree(requested, frameWorkers)` derives the inner-loop degree as
`max(1, ProcessorCount / frameWorkers)`, so the frame-level workers and the inner loops split
the machine instead of both taking all of it. `0` and `1` as the frame-level request leave the
inner degree at the full processor count, preserving the pre-existing behaviour for every
caller that does not opt in.

The inner degree is now an instance property (`IParallelismConfigurable.InnerDegreeOfParallelism`)
rather than ambient state:

- `DctModulator` and `MotionFrameBitDecoder` implement the interface; their static frame
  factories take the degree as an explicit parameter and the serial value `1` remains
  supported for deterministic tests.
- `FrameBitDecoderFactory` copies the modulator's inner degree onto the decoder it creates, so
  the encode and decode sides of a round trip agree without the caller wiring it manually.
- `EncoderEngine` and `DecodeStreamOrchestrator` assign the property from
  `ParallelismPolicy.ResolveInnerDegree` _after_ the real frame-worker count is known —
  `EncoderEngine` clamps that count to the frame budget so a short encode still gets a
  parallel inner loop.

`YTAHD.Cli` exposes the request as a `--jobs` option on both `encode` and `decode`, accepting
`auto`, `0`/`1`, or a positive integer, and writes it to `MaxDegreeOfParallelism`.

## Consequences

- Operators can force a serial, reproducible run with `--jobs 1`, which is what the lossy-decode
  debugging workflow needed.
- Total thread count stays near the machine's capacity instead of growing with the product of
  the two loop levels. `Auto` deliberately leaves a processor of headroom for the codec.
- The unconfigured default is unchanged: `MaxDegreeOfParallelism = 0` still selects the serial
  frame pipeline with a fully parallel inner loop, so existing tests and CLI invocations keep
  their old behaviour.
- `ParallelismPolicy` is pure and takes the logical processor count as a parameter, so the
  policy is testable on any machine (`YTAHD.Tests/ParallelismPolicyTests.cs`, 47 tests).
- `AutoWorkerCap = 4` is a conservative starting point, not a measured optimum. Choosing it
  properly is the job of CR-20260912-02 (F6 performance validation); the constant is the single
  place to change once that matrix has data.
- The inner degree is now shared state on a modulator/decoder instance. The property is also
  the knob that pre-existing static frame factories previously read from the ambient processor
  count, so callers of `CreatePhase3Frame(...)` that relied on the old two-argument overload
  now pass the degree explicitly.
