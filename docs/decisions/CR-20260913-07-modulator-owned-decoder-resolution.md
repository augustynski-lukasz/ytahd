# CR-20260913-07 — Modulator-owned frame-bit decoder resolution (capability interface)

**Date:** 2026-09-13 **Status:** Implemented
**Area:** `YTAHD.Core/Core` (`FrameBitDecoderFactory`, `IModulator` implementors),
`YTAHD.Tests`

## Context

`docs/REVIEW.md` Review 1 finding F11: `FrameBitDecoderFactory` selects the frame-bit
decoder via an if/else type ladder
(`if (modulator is BinaryGridModulator) … else if (modulator is PseudoQamModulator) …`).
Adding a Phase 5 modulator requires editing this ladder — and a forgotten registration
fails at **decode time** (`NotSupportedException` at frame N, after FFmpeg has already run),
the most expensive place to discover it.

## Decision

Move decoder resolution onto the modulator itself via an optional capability interface,
e.g. `IFrameBitDecoderProvider { IFrameBitDecoder CreateFrameBitDecoder(); }`, implemented
by each modulator. `FrameBitDecoderFactory` first tries the capability interface (handling
decorator unwrapping as it does today) and keeps the type ladder only as a legacy fallback
during migration, then the ladder is deleted. A modulator without a decoder fails at
**construction/registration time** (factory throws immediately when neither capability nor
ladder matches), not mid-decode.

Tests: factory resolution for all four modulators; decorator-wrapped modulator resolution;
a modulator missing the capability fails fast at registration (assert exception at
construction, not at first frame).

## Consequences

- Adding a Phase 5 modulator becomes: implement the capability on the modulator — no
  factory edit, and a forgotten implementation fails at startup.
- `FrameBitDecoderFactory` shrinks to capability resolution + decorator unwrapping.
- Sequencing: this is the only R6-batch item with design content; it ships as its own
  commit/ADR, separate from the `TD-20260913-02` cleanup batch.

## Validation

- New `FrameBitDecoderFactoryTests` (7 tests): capability resolution for all four shipped
  modulators, decorator-wrapped resolution, fail-fast `NotSupportedException` naming the
  modulator type for a capability-less modulator, and inner-degree-of-parallelism
  inheritance through the capability path.
- Full suite: 313/313 passed (306 prior + 7 new), including the real-FFmpeg round trips
  that exercise every modulator's decoder through the new resolution path.
