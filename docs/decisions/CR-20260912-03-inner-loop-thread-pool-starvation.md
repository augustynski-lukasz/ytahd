# CR-20260912-03 — Inner-Loop Thread-Pool Starvation Deadlock

**Date:** 2026-09-12 **Status:** Implemented
**Area:** `YTAHD.Core/Modulation/InnerLoopParallelism.cs`, `YTAHD.Core/Infrastructure`
(`ChildProcessPipes`, `FFmpegWrapper`, `FFmpegProbe`), `YTAHD.Core/Core/DecoderEngine.cs`,
`YTAHD.Tests/InnerLoopParallelismStarvationTests.cs`

## Context

While adding the F6 real-codec parallelism matrix (CR-20260912-02), the full test suite began to
**deadlock** rather than fail. The symptom was precise and reproducible:

- `dotnet test` never returned. Sampling showed **0 s CPU delta on both `testhost.exe` and the
  child `ffmpeg.exe`** over 15 s — a genuine stall, not slow work.
- `--blame-hang --blame-hang-timeout 120s` reported 225 tests already passed, then named
  `ParallelPipelineRealCodecTests.RealFfmpeg_RoundTrip_Survives_Every_Degree_Of_Parallelism`
  as the test running at hang time.
- The stuck child process was a **decode** invocation at the new 640x480 geometry.
- The new test class **passed 6/6 in isolation (16 s)** and the rest of the suite passed
  (222 tests) without it. The deadlock only appeared when the real-codec tests ran concurrently.

That differential — fine alone, stalls under concurrent load — is the signature of nested
blocking work on a saturated thread pool.

The pipeline structure makes this easy to hit: `DecodeStreamOrchestrator` runs N packet-decode
workers as pool tasks consuming a bounded channel, and each worker calls into a modulator or
frame bit decoder whose inner loop was parallelised with
`Parallel.For(0, rowCount, new ParallelOptions { MaxDegreeOfParallelism = degree }, body)`.

`Parallel.For` with an **explicit** `MaxDegreeOfParallelism` partitions the range and the calling
thread blocks until every partition completes. It will not run more than `degree` rows at once,
and critically it does not present the pool with queued work items it can grow for. When several
concurrent pipelines each park their worker thread inside such a loop, the pool has no free
threads, no visible queued work, and no reason to inject any: the process parks forever with all
threads blocked and nothing on the CPU.

This was confirmed by isolation experiment, not by inference: with the pipe-handling hardening
below left in place but `InnerLoopParallelism` reverted to `Parallel.For`, the suite hung again
under `--blame-hang`.

## Decision

**1. Replace the blocking bounded parallel loop in `InnerLoopParallelism.ForEachRow`.**

Rows are partitioned into at most `min(degreeOfParallelism, rowCount)` contiguous chunks and each
chunk is dispatched as an ordinary `Task.Run` work item; the caller then blocks on
`Task.WaitAll`. Behaviour is otherwise unchanged: `degreeOfParallelism <= 1` still runs serially
on the calling thread, `rowCount <= 0` still does nothing, a null body still throws, and
exceptions still surface as an `AggregateException`.

The important difference is visibility. The chunked work items are _queued on the thread pool_,
so the pool's starvation detection can observe blocked threads with pending work and inject
additional threads. The stall degrades from "never completes" to "completes slower", which is
the correct failure mode for a throughput feature.

**2. Harden every redirected child-process pipe (defence in depth).**

The same investigation found inconsistent pipe handling on the FFmpeg call sites, each a latent
deadlock of the classic "child blocks writing a full pipe while the parent blocks reading the
other" shape:

- `DecoderEngine.DecodeAsync` and `GetVideoMetadataAsync` redirected stderr and never read it
  while blocking on stdout.
- `FFmpegProbe.ResolveFfprobePath` used a synchronous `WaitForExit()` with both pipes redirected
  and neither consumed; `GetVideoFrameCountAsync` left stderr unread.
- `FFmpegWrapper.IsAvailableAsync` waited for exit before consuming either redirected pipe.
- `FFmpegWrapper.StartAsync` drained stderr with `StreamReader.EndOfStream`, which is a
  _blocking_ read and therefore parked a pool thread for the entire duration of every encode —
  feeding directly back into the starvation problem. It also redirected stdout and never read it.

A new internal `ChildProcessPipes` helper provides `DrainAsync` (consume to end) and
`ReadToEndAsync` (consume and capture). All named call sites now use it. The encoder no longer
redirects stdout at all, since media goes to the output file and a redirected-but-unread pipe is
only a liability.

Child stderr is echoed only when this process owns an interactive stderr
(`!Console.IsErrorRedirected`). Under a test host the child output is discarded instead of being
written to `Console`, whose writer is globally locked — one more blocking point that is
unacceptable inside a pipeline.

**3. Regression coverage.**

`YTAHD.Tests/InnerLoopParallelismStarvationTests.cs` asserts that `ForEachRow` completes within a
bounded budget under 24 concurrent pipelines, and that every row is visited exactly once for
degrees that do not divide the row count (1, 2, 3, 5, 17 over 17 rows), covering the chunking
arithmetic.

## Consequences

- The full suite is green and stable: **228 passed, 0 failed**, reproduced twice
  (1 m 5 s and 56 s). Before the fix, the same suite hung indefinitely.
- Encodes no longer park an extra pool thread for their whole duration, and no FFmpeg call site
  can block on a full pipe buffer.
- `ForEachRow` keeps its public signature, so no modulator or decoder call site changed.
- The chunked scheduler is not further parallel than before: at most `min(degree, rowCount)`
  chunks run, so modulator frame output remains deterministic and the "serial and parallel
  renders are identical" tests still hold.
- Chunking assumes rows are independent, which is already a precondition of the parallel path
  (each row is a disjoint region of the frame buffer).
- Under extreme over-subscription the new scheduler is slower than an ideal one because the pool
  injects threads gradually. This trades worst-case latency for liveness, which is the intended
  direction for a pipeline that must not strand callers.
- The starvation regression test is a **guard, not a reproduction**: an attempt to reproduce the
  stall synthetically with 24 blocking outer tasks did _not_ starve, because queued continuations
  let the pool's detection fire. The authoritative evidence is the full-suite `--blame-hang`
  differential described above. The synthetic test is documented as such in its own remarks so
  it is not mistaken for proof.
- Known limitation: the drain helper discards child stderr under a test host, so diagnosing a
  malformed encode inside a test run still requires reproducing it from the CLI.
