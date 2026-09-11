# CR-20260911-02 - Decode Progress Output

**Date:** 2026-09-11 **Status:** Implemented
**Area:** CLI decode output, app decode options, decoder stream processing

## Context

CLI decode can take long enough that users need a simple indication of how far the video
scan has progressed. The decoder already counts frames as they are consumed, and FFprobe can
report the total video frame count before the raw RGB decode stream starts.

## Decision

Add an optional decode progress callback to `DecodeOptions`. `DecoderEngine` probes the input
video frame count and `DecodeStreamOrchestrator` reports progress as each decoded RGB frame is
read. The CLI prints decode progress at coarse percentage increments and includes the final
completion percentage in the decode summary.

## Consequences

CLI users get visible decode progress without parsing FFmpeg logs or changing service callers
that do not need progress. If FFprobe cannot determine the total frame count, percentage output
is omitted and the final completion value is reported as `n/a`.
