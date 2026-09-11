# CR-20260911-07 - Pipeline Timing Metrics

**Date:** 2026-09-11 **Status:** Implemented
**Area:** Encode metrics, decode metrics, CLI summaries, performance planning

## Context

The parallel pipeline plan needs measured bottlenecks before changing encode/decode execution
order. The current serial pipeline mixes packet construction, frame rendering, RGB conversion,
FFmpeg pipe writes, frame reads, packet decoding, and ordered aggregation, making it hard to
know where concurrency would help or simply oversubscribe the machine.

## Decision

Add coarse timing fields to `EncodeMetrics` and `DecodeMetrics`. Encode records total elapsed
time, packet build/parity time, frame render time, RGB conversion time, and FFmpeg pipe write
time. Decode records total elapsed time, FFmpeg frame read time, packet decode time, and
ordered aggregation/recovery time. Surface these timings in the CLI encode/decode summary
output.

## Consequences

Normal CLI runs now provide a baseline for choosing the next optimization step. The metrics do
not change pipeline ordering or codec behavior, so they are low-risk. The timing buckets are
coarse and may overlap slightly in small fake-wrapper tests, but they are enough to identify
large real-world bottlenecks before introducing bounded channels or worker pools.
