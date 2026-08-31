# Backlog

Open items only. When an item is resolved, delete it from here and add an ADR in `docs/decisions/`.

## Tidy remaining warnings and dependency advisory cleanup

Nullable-reference warnings remain in a few call sites, and `SkiaSharp` 2.88.3 has a known
high-severity advisory (GHSA-j7hp-h8jx-5ppr). Address both after core production behavior
(Phase 1/2/3 + durability matrix) remains stable. Candidate fix: bump `SkiaSharp` to a patched
version and sweep remaining nullable warnings in `YTAHD.Core`.

## Phase 4: Motion Vector Abuse (Temporal Tracking)

Design and implement the Phase 4 modulation scheme described in `README.md`: encode data in
the direction/velocity of repeating tile motion between frames instead of static pixel or
frequency-domain content. Requires optical-flow tracking in the decoder. Not yet started.
