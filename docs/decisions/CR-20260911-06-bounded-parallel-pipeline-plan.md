# CR-20260911-06 - Bounded Parallel Pipeline Plan

**Date:** 2026-09-11 **Status:** Accepted
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

## Consequences

The project gets a path to better throughput without guessing where threads help. The first
implementation step adds observable timings with little behavioral risk. Later stages add
more complexity through channels, buffer ownership, and ordered aggregation, so each stage
needs focused fake-wrapper tests plus real FFmpeg validation before being treated as safe.
