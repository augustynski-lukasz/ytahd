# CR-20260911-10 - Phase 3 Parallel Decode

**Date:** 2026-09-11 **Status:** Implemented
**Area:** Frame bit decoder abstraction, Phase 3 DCT packet decode, decode pipeline

## Context

Phase 3 packet decode applies a forward DCT to each independent 8x8 block and recovers one
packet byte per block. This is a good parallelism target, but the original `IFrameBitDecoder`
entry point accepted `ReadOnlySpan<byte>` and `Span<byte>`. Those ref-like types cannot be
captured by worker lambdas, and copying whole 4K frames just to use workers would undermine
the performance goal.

## Decision

Add an explicit `DecodeMemory` entry point to `IFrameBitDecoder`. The default implementation
bridges to the existing span-based `Decode` method, so existing decoders and direct callers keep
their serial behavior. `DctFrameBitDecoder` overrides `DecodeMemory` and uses `Parallel.For`
across block rows. Each worker computes one packet byte per 8x8 block and writes only to its
own output byte, avoiding shared byte-level bit updates.

Route the decode pipeline's byte-array frame buffers through `DecodeMemory` in packet
extraction and accumulation paths.

## Consequences

Phase 3 decode can use multiple CPU cores without copying whole decoded frames or changing
stream ordering. The span-based `Decode` method remains available as a serial fallback for
direct tests and simple callers. Other decoders can opt into `DecodeMemory` later; Phase 4
motion tile search is the next candidate.
