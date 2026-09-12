# CR-20260912-04 — Child-Pipe Thread-Pool Starvation and Child Lifetime

**Date:** 2026-09-12 **Status:** Implemented
**Area:** `YTAHD.Core/Infrastructure` (`ChildPipeStream`, `ChildProcessScope`,
`ChildProcessLifetime`, `ChildProcessPipes`, `FFmpegWrapper`, `FFmpegProbe`),
`YTAHD.Core/Core/DecoderEngine.cs`, `YTAHD.Core/Core/DecodeStreamOrchestrator.cs`,
`YTAHD.Core/Core/FrameBitDecoderFactory.cs`, `YTAHD.Core/Modulation/IModulator.cs`,
`YTAHD.Tests/ChildPipeStreamTests.cs`, `YTAHD.Tests/ChildProcessPipeRealCodecTests.cs`,
`YTAHD.Tests/DecodeSlotOrderingTests.cs`

## Context

After CR-20260912-03 removed the nested blocking `Parallel.For`, the full suite still hung
intermittently: `dotnet test` reported **all 230 tests passed** and then never exited. Two
independent managed-stack dumps of the wedged test host showed:

- no YTAHD frame on any thread and no pending async state machine (`dumpasync` empty);
- six idle xUnit worker threads and the vstest reporter waiting on a completed run;
- the run's final state machine (`VsTestRunner.RunTestsInAssembly`) blocked in
  `WaitHandle.WaitOne()` after the last test had already been reported.

The stall was therefore on the **post-run shutdown path**, not inside any test. The mechanism
was found in the child-process pipe handling introduced by CR-20260912-03:

- `Process.StandardOutput`/`StandardError` are synchronous `FileStream`s over anonymous pipes.
  Windows cannot complete a pipe read on an IO completion port, so .NET emulates `ReadAsync`
  with `AsyncOverSyncWithIoCancellation`, which parks a **thread-pool** thread in a blocking
  `ReadFile` for as long as the child keeps the pipe open.
- Every in-flight ffmpeg child therefore permanently consumed pool threads. The suite spawns
  hundreds of children across the run; concurrent round trips left the pool with no free
  threads, and the pool's starvation detection could not fire because the blocked threads were
  inside emulated-async reads with no visible queued work.
- Separately, `Process.Kill()` only _requests_ termination: it returns while the child is still
  tearing down and still holds its output file, so a cancelled encode raced the child's
  teardown and could see a sharing violation on the video it had just written. A child that was
  never killed at all (failure paths that dropped the `Process` without disposing it) blocked
  forever on a full pipe buffer and leaked one real ffmpeg process per failed operation.

## Decision

**1. Move child pipe reads off the thread pool (`ChildPipeStream`).**

A new internal `Stream` wrapper pumps the child's pipe on one dedicated
(`TaskCreationOptions.LongRunning`) thread into a bounded channel (8 × 64 KiB) and serves
asynchronous reads from that queue. A decode of any duration now costs **zero** thread-pool
threads. A pump failure is surfaced as a read error rather than a silent truncation, so a
decode never mistakes a dead ffmpeg for a short video.

**2. Run the drain helpers on dedicated threads (`ChildProcessPipes`).**

`DrainAsync` and `ReadToEndAsync` now execute their (synchronous, on purpose) read loops on
dedicated background threads instead of emulated-async pool reads. Background threads can
never keep the process alive, which also removes the fire-and-forget drain as an exit-path
risk.

**3. Make child teardown deterministic (`ChildProcessScope`, `ChildProcessLifetime`).**

- `ChildProcessScope.Start` owns a child for the duration of a `using` scope; if the scope ends
  before the child exited, the child and its descendants are killed and the caller waits,
  boundedly (5 s), for the child to actually be gone.
- `ChildProcessLifetime.KillAndWait` replaces bare `Kill()` in `FFmpegWrapper`'s
  `ProcessWrapper.Dispose` and `FFmpegWrapper.Dispose`, so a cancelled encode cannot return
  while the child still holds the output video.
- All `Process.Start` sites (`FFmpegWrapper.IsAvailableAsync`,
  `TryExtractAudioPcmAsync`, `FFmpegProbe.ResolveFfprobePath`,
  `GetVideoFrameCountAsync`, `DecoderEngine.DecodeAsync`, `GetVideoMetadataAsync`) now go
  through the scope, making the failure and cancellation paths self-cleaning.

**4. Regression coverage.**

- `ChildPipeStreamTests` (synthetic, 8 tests): dedicated-thread pumping, ordering, EOF
  semantics, late-data waiting, failure surfacing, dispose-during-read, backpressure, and
  synchronous reads.
- `ChildProcessPipeRealCodecTests` (real ffmpeg, 3 tests): a real child pipe streams to
  completion byte-for-byte; an abandoned child with a full, unread pipe is killed on scope
  disposal; and more concurrent round trips than the pool's base size complete within a
  bounded time — the configuration that stalled the suite before the fix.
- `YTAHD.Core.csproj` gains `InternalsVisibleTo YTAHD.Tests` so the suite can exercise the
  internal infrastructure directly without widening the public API.

**5. Fix the second deadlock: result-slot accounting in the parallel decode orchestrator.**

A live reproduction of the remaining stall (a filtered run of the new tests froze with zero
CPU) and a `dumpasync` analysis of the wedged host exposed a distinct circular wait in
`DecodeStreamOrchestrator.ProcessParallelAsync`:

- Each decode worker acquired a `resultSlots` semaphore slot **after** its frame decoded, but
  slots were released by the aggregator only on **in-order** consumption.
- If the head frame's decode was slower than later frames', later results filled every slot
  and sat in the aggregator's out-of-order buffer. The head worker then blocked waiting for a
  slot that only the head's own consumption could free — a circular wait with no CPU and no
  pending work, which is exactly the "all tests passed, host never exits" signature.

The fix reserves each frame's slot **in the reader, in sequence order, before dispatching the
work item**, so the head item always holds a slot before any later item can; the aggregator can
always consume the head and release. Workers now only produce results. The acquisition moves
from N workers to the single reader, so the semaphore's role (bounding resident decoded frames)
is unchanged.

**6. A minimal decorator seam for decoder resolution.**

The regression test needs to make one frame's decode slower than the rest, and the only
per-frame hook the decode path exposes is `IModulator.GetBorderWidth` (called once per frame
inside `DecoderEngine.TryReadDecodedPacket`, on the worker thread). All modulators are
`sealed`, so the test uses a decorating `IModulator`. To keep such wrappers working,
`FrameBitDecoderFactory.CreateForModulator` now unwraps a new
`IModulatorDecorator` (`IModulator` + `Inner`) and resolves the decoder for the effective
modulator. This is a small, general robustness improvement: decorating modulators previously
failed decoder resolution outright.

**7. Regression coverage for the slot fix.**

`DecodeSlotOrderingTests` gates the first per-frame decode (call 2 — call 1 is the
orchestrator's upfront geometry probe on the caller thread) until every result slot is spoken
for, then releases. Against the pre-fix worker-side acquisition the test **hangs**
(reproduced: the run timed out); against the fixed in-order reservation both tests pass in
under a second. A second test asserts the parallel output still matches the serial output
under the same gating.

## Consequences

- The full suite exits cleanly and deterministically: repeated full runs self-exit with a real
  exit code (246/246 passed, ~41 s each) and zero orphaned ffmpeg children, where the same
  suite previously hung indefinitely after reporting every test as passed.
- Concurrent encodes/decodes no longer consume thread-pool threads proportional to the number
  of in-flight children, so pipeline throughput no longer degrades non-linearly under load.
- Cancelled or failed operations no longer leak ffmpeg processes or race child teardown on
  output files.
- The parallel decode can no longer deadlock on result-slot accounting: the head frame always
  holds a slot before any later frame can, so the aggregator can always make progress.
- The dedicated pump threads are background threads; they add a small fixed thread cost per
  in-flight child (one per drained pipe), which is bounded by the pipeline's own concurrency
  limits.
- `ChildPipeStream` buffers up to ≈ 512 KiB per stream ahead of the consumer; this is negligible
  next to the multi-megabyte frame buffers the pipeline already holds.
- `IModulatorDecorator` is a new public interface; existing modulators are unaffected and the
  factory's behaviour for non-decorating modulators is unchanged.
- The synthetic `ChildPipeStreamTests` cannot by themselves prove the starvation fix — the
  authoritative evidence is the real-child concurrency test, the slot-ordering hang
  differential, and the repeated full-suite runs, mirroring the guard-versus-reproduction
  distinction recorded in CR-20260912-03.
