# CR-20260911-06 - Bounded Parallel Pipeline Plan

**Date:** 2026-09-11 **Status:** Implemented
**Area:** Encoder pipeline, decoder pipeline, modulator performance, CLI metrics

## Context

The current encode and decode loops are mostly serial. Encoding builds packet payloads,
renders frames, converts RGBA to RGB, and writes to FFmpeg in one loop. Decoding reads a raw
RGB frame, decodes its packet, updates duplicate-frame state, and accumulates output before
reading the next frame. This is correct and easy to reason about, but it leaves CPU-heavy
Phase 3 and Phase 4 work underutilizing multicore machines.

The main constraint is ordering. FFmpeg stdin/stdout are ordered streams, Phase 4 canonical
separators are temporal markers, and duplicate-run tracking must process frames in stream
order. Memory is also a hard constraint: one 4K RGB frame is about 24 MB and one RGBA frame is
about 33 MB, so unbounded render-ahead or decode-ahead would quickly exhaust memory.

## Decision

Plan parallelism as bounded, ordered pipelines. Start with timing instrumentation so changes
are driven by measured bottlenecks. Then remove low-value per-frame pipe flushes, parallelize
independent Phase 3/Phase 4 inner loops, and only then split encode/decode into producer,
worker, and ordered writer/aggregator stages.

All concurrency must preserve frame order, packet metadata, parity grouping, Phase 4 canonical
separator semantics, audio-clock cadence, decode progress reporting, and real FFmpeg
round-trip correctness. CPU `libx264` and future GPU encoders can shift bottlenecks, so the
pipeline should expose conservative degree-of-parallelism controls and keep serial fallback
behavior for debugging.

The F4 encode stage implements this contract with bounded packet and rendered-frame channels,
parallel render workers, and one ordered FFmpeg writer. F5 applies the same shape to RGB decode:
one sequential reader assigns frame sequence numbers, bounded workers perform only stateless
packet/canonical decoding, and one ordered aggregator owns duplicate runs, canonical separators,
parity recovery, output assembly, metrics, and progress. `MaxDegreeOfParallelism = 0` keeps the
original serial path; positive values bound both queued frame memory and decoded-result memory.
Producer, worker, aggregator, and caller cancellation share a linked token, and the public decode
APIs retain their existing call shape while accepting optional cancellation tokens.

## Consequences

The project gets a path to better throughput without weakening protocol ordering or allowing
unbounded frame accumulation. F4 preserves ordered writes, physical repeats, Phase 4 canonical
separators, audio-clock cadence, and serial fallback behavior. F5 preserves ordered aggregation
and packet acceptance semantics while bounding decode-ahead. Focused regressions cover decode
equivalence, ordered progress, cancellation, canonical sequencing, and recovery; the remaining
performance matrix and default-concurrency tuning remain F6 work.
