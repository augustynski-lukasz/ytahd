# F-20260828-03 — Frame Metadata, Hash Validation, and Erasure Coding

**Date:** 2026-08-28 **Status:** Implemented
**Area:** `YTAHD.Core.Core` (frame packet format)

## Context

Frames needed self-describing metadata and integrity checks so the decoder could detect
corrupted or lossy frames instead of blindly trusting decoded pixel data, and lost frames
needed a recovery path.

## Decision

- Added per-frame metadata: frame index, payload length, and a SHA-256 hash (formerly
  FEAT-014).
- Made the decoder validate frame hashes before accepting payload bits, rejecting corrupted
  frames instead of silently including them (formerly FEAT-015).
- Added erasure coding for lost-frame recovery (Reed-Solomon / fountain-style repair)
  (formerly FEAT-016).

## Consequences

Established the packet-integrity contract (header + hash) that all later modulators
(Phase 2, Phase 3, durability matrix) build on. This is the foundation later centralized into
`FramePacketCodec` (see CR-20260830-01).
