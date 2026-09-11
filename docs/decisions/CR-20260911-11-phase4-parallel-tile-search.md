# CR-20260911-11 - Phase 4 Parallel Tile Search

**Date:** 2026-09-11 **Status:** Implemented
**Area:** Motion frame decoder, canonical frame classification, decode pipeline

## Context

Phase 4 decodes each payload byte by searching a finite offset alphabet for one motion tile.
Each tile search is independent and relatively CPU-heavy because it evaluates candidate
offsets with a sum-of-absolute-differences score over the tile texture. Canonical separator
classification uses the same tile search to determine whether most cells are at the home
offset.

The decode pipeline already routes byte-array frame buffers through `DecodeMemory` for Phase 3. That same memory-based entry point avoids capturing ref-like spans in parallel worker
lambdas and avoids copying whole 4K frames.

## Decision

Override `DecodeMemory` in `MotionFrameBitDecoder` and use `Parallel.For` across tile rows.
Each worker searches offsets for its own cells and writes only the corresponding packet byte.
Add `IsCanonicalFrameMemory` to the frame decoder abstraction and route the decode
orchestrator's canonical checks through it. `MotionFrameBitDecoder` overrides that method and
parallelizes canonical separator classification across tile rows with per-row local counts.

The existing span-based `Decode` and static `IsCanonicalFrame` methods remain available for
direct tests and simple serial callers.

## Consequences

Phase 4 packet decode and canonical separator detection can use multiple CPU cores without
changing stream ordering or duplicate-run aggregation. The change keeps the serial span APIs
for compatibility. The next tuning step is to expose degree-of-parallelism controls and a
serial fallback so small payloads or already-saturated machines can avoid thread-pool overhead.
