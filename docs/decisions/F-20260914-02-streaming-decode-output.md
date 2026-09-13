# F-20260914-02 — Streaming decode output (memory-bounded payload assembly)

**Date:** 2026-09-14 **Status:** Accepted
**Area:** `YTAHD.Core/Core` (`DecodeStreamOrchestrator`, `DecodeAggregator`,
`DecodedFrameAccumulator`, `DecodeRecoveryPolicy`), `YTAHD.Cli/Program.cs`

## Context

The real-user-scale CLI validation (10 MB payload, 4K video, 2026-09-14) exposed a
scaling flaw in the decode output path: the orchestrator assembles the **entire payload
in memory** and the CLI writes the output file only once, after the full decode
(`File.WriteAllBytesAsync` at the end). The 10 MB run showed the output file absent for
the whole decode while the CLI process accumulated ~2200 CPU-seconds.

This does not scale:

- **1 TB payload** (the user's stated bound) cannot be buffered in RAM at all — the
  decode would OOM long before finishing, after spending hours of CPU.
- Even at 1 GB the design wastes memory and delays all output until the end, so a
  mid-decode crash loses everything and the user sees no progress.
- The information to do better already exists frame-by-frame: the frame protocol is
  **ordered** (frame index in every packet header), the duplicate-run tracker emits
  logical frames in order, and `DecodeRecoveryPolicy.ShouldStopDecoding` already walks
  the _contiguous prefix_ of the accumulator — the concept of "frames 0..N-1 are final"
  is native to the design.

## Decision

Make the decode output path **streaming and memory-bounded**:

1. **Contiguous-prefix flush.** The aggregator tracks the highest contiguous frame index
   with no gap. Once parity recovery for a group is possible (its parity frame seen, or
   the group is complete), frames whose position is final are handed to the consumer and
   dropped from the accumulator. Only the _tail window_ (frames after the last gap, plus
   groups still awaiting parity) stays in memory.
2. **Consumer abstraction.** The orchestrator accepts an output sink
   (`Stream`/`Func<Stream>`/callback) instead of returning one `byte[]`. The CLI passes a
   `FileStream` and bytes land on disk as they become final; the in-memory API
   (`ProcessAsync` returning `byte[]`) remains as a thin wrapper over a
   `MemoryStream` sink for compatibility.
3. **Expected-length handling.** `expectedOutputBytes` already bounds the legacy path's
   stop condition; the streaming flush uses it to know when the payload is complete. The
   durability path's manifest declares `TotalPayloadBytes` — the same bound applies once
   the manifest is recovered (manifest frames arrive early by design).
4. **Integrity unchanged.** SHA-256 verification is inherently whole-payload; it becomes
   a _streaming hash_ over the sink (incremental `IncrementalHash`), so `integrity=`
   semantics are preserved without re-reading the file. Hole-tolerant reconstruction
   (CR-20260913-02) interacts with this: a wholly-missing group is a _gap_ that blocks
   the contiguous prefix — its zero-filled hole can only be materialized once the loss
   map is known at end-of-stream, so holes keep the tail-window behavior (bounded by the
   hole size, not the payload size).
5. **CLI progress.** With streaming output, the CLI can report bytes-written progress
   during decode instead of a silent wait.

## Consequences

- Memory becomes O(tail window + hole size), not O(payload). A 1 TB payload decodes in
  constant memory (disk-bound).
- Output is durable incrementally: a crash keeps everything decoded so far.
- The duplicate-run/parity-recovery ordering logic must be re-examined carefully: the
  current `DecodedFrameAccumulator` recovers missing frames _retroactively_ from parity
  after all frames are seen; streaming requires per-group incremental recovery (recover
  group g as soon as its parity frame arrives and ≤1 member is missing). This is the
  main implementation risk and needs its own design pass on `DecodedFrameAccumulator`.
- `AssembleOutput`/`RecoverMissingPayloadFrames` public API changes shape; the
  `DecodedFrameAccumulatorTests` reflection-based tests will need rework.
- Sequencing: this is a protocol-adjacent change to the decode contract; it should ride
  with (or before) the Phase 4 follow-up work rather than be squeezed into the cleanup
  batch. Tracked in `docs/BACKLOG.md` until started.

## Alternatives considered

- **Memory-mapped sparse file:** write frames at their file offset as they arrive.
  Rejected as the primary design because parity recovery and duplicate-run arbitration
  still need in-memory grouping, and sparse-file semantics complicate the hole model;
  but the sink abstraction keeps this open as an implementation detail.
- **Temp-file spill of the accumulator:** keeps the current two-pass shape but moves the
  buffer to disk. Rejected: still two-pass, still full rewrite at the end, no incremental
  durability.
