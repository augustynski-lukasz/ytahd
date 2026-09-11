# CR-20260911-09 - Phase 3 Parallel Render

**Date:** 2026-09-11 **Status:** Implemented
**Area:** Phase 3 DCT modulator, frame rendering, parallel pipeline workstream

## Context

Phase 3 frame synthesis renders one independent 8x8 DCT block per payload byte. At 4K this
means more than 100,000 independent block calculations per frame. The serial loop is a good
candidate for thread-pool parallelism because each block writes to a distinct region of the
RGBA frame and does not depend on neighboring blocks.

Decode-side Phase 3 and Phase 4 loops are also independent at the block/tile level, but the
current decoder interfaces receive `ReadOnlySpan<byte>`/`Span<byte>`. Capturing those spans in
parallel worker lambdas is not allowed by C#, and copying whole 4K frames just to parallelize
would undermine the performance goal. Those paths need a separate decode buffer/API slice.

## Decision

Parallelize Phase 3 frame rendering across block rows inside `DctModulator.CreatePhase3Frame`.
The method copies the packet span into a worker-safe byte array once, then uses `Parallel.For`
over block rows. Each worker computes payload byte index from `(blockRow, blockColumn)` and
writes only the pixels belonging to that 8x8 block.

## Consequences

Phase 3 encode can use multiple CPU cores during frame synthesis while preserving byte order
and output frame layout. The change adds one packet-sized copy per rendered Phase 3 frame to
avoid unsafe span capture. Remaining F3 work should address decode-side buffer ownership and
degree-of-parallelism controls before parallelizing Phase 3 decode or Phase 4 tile search.
