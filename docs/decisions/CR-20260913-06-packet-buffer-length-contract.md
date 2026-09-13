# CR-20260913-06 — Align `GetPacketBufferLength` to a bytes contract (Phase 2 fix)

**Date:** 2026-09-13 **Status:** Implemented
**Area:** `YTAHD.Core/Modulation/PseudoQamModulator.cs`, `YTAHD.Tests`

## Context

`docs/REVIEW.md` Review 1 finding F9: `PseudoQamModulator.GetPacketBufferLength` returns
`Math.Max(geometry.BitsPerFrame, geometry.HeaderBytes)` — a **bit** count when
`BitsPerFrame` dominates — while the method's name and every sibling modulator
(`BinaryGridModulator`, `DctModulator`, `MotionVectorModulator`) return a **byte** count
(`HeaderBytes + payloadBytesPerFrame`). Callers allocate `packet = new byte[framePacketBytes]`
from this value, so Phase 2 allocates ~8× more than needed and the buffer tail is
meaningless. It works today only because decode writes exactly `blocksX*blocksY` bytes into
the buffer — a latent geometry-contract inconsistency, not a correctness bug.

## Decision

Change `PseudoQamModulator.GetPacketBufferLength` to return
`HeaderBytes + payloadBytesPerFrame`, matching the sibling modulators' contract. Add a
cross-modulator regression test asserting buffer-length parity: for every registered
modulator, `GetPacketBufferLength` equals `HeaderBytes + GetPayloadBytesPerFrame` (the
invariant the name promises). Before merging, verify `DecoderPacketCompatibilityTests` and
`BinaryGridModulatorTests` stay green.

## Consequences

- Phase 2 packet buffers shrink ~8×; the buffer-length contract is uniform and
  test-enforced across all four modulators.
- Decode behavior is unchanged (decode never relied on the oversized tail); encode
  allocations drop.
- Risk: low — the value is an allocation size, not a wire-format field. Any hidden caller
  that (incorrectly) depended on the oversized buffer would surface immediately in the
  fake-wrapper round-trip suite.

## Validation

- New `PacketBufferLengthContractTests` (8 tests): the parity invariant
  (`GetPacketBufferLength == HeaderBytes + GetPayloadBytesPerFrame`) for all four
  registered modulators, plus a bit-count-leak guard asserting the value stays within the
  frame's pixel count (a bit count would be ~8× that).
  Implementation note: the first cut of the leak guard compared against
  `BitsPerFrame` computed at macroblock granularity, which is wrong for modulators whose
  own block size differs (DCT uses 8×8 blocks, not the 16×16 macroblock) — the pixel-count
  bound is the loosest sane invariant that holds for every modulator.
- ADR-named verification suites stayed green: `DecoderPacketCompatibilityTests`,
  `BinaryGridModulatorTests`, `PseudoQamModulatorTests` (28/28 focused).
- Full suite: 331/331 passed (323 prior + 8 new).
