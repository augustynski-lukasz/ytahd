# CR-20260911-08 - Encode Pipe Flush Cleanup

**Date:** 2026-09-11 **Status:** Implemented
**Area:** Encoder FFmpeg pipe writes, performance instrumentation, fake FFmpeg tests

## Context

The serial encoder wrote each physical RGB frame to FFmpeg stdin and then flushed the stream.
That preserves correctness, but per-frame flushing prevents the pipe and FFmpeg process from
buffering naturally. For large 4K streams this can add avoidable synchronization overhead
before any larger parallel pipeline work begins.

## Decision

Keep frame writes strictly ordered but remove `FlushAsync()` from each physical frame write.
Flush stdin once in the existing cleanup path before closing it. Include that final flush in
the FFmpeg write timing bucket. Extend the fake FFmpeg stream with a flush counter and assert
the simple encode path performs one final flush.

## Consequences

The encoder still writes frames in the same order and closes the FFmpeg input cleanly, while
allowing the pipe to buffer consecutive frames. This is a low-risk performance cleanup before
bounded render/decode workers are introduced. If a specific FFmpeg build ever requires more
frequent flushing, that behavior should be added behind a targeted compatibility option and
real-codec regression.
