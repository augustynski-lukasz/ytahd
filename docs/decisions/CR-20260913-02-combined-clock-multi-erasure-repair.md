# CR-20260913-02 — Combined-clock multi-erasure repair (hole-tolerant reconstruction)

**Date:** 2026-09-13 **Status:** Implemented
**Area:** `YTAHD.Core/Core` (`DurabilityMatrixCodec`, `DurabilityTransportCodec`,
`DecodeMetrics`, `DecodeStreamOrchestrator`), `YTAHD.Cli`, `YTAHD.Tests`

## Context

Stage C1 landed detection only: `DecodeMetrics.HasAudioVideoDatagramMismatch()` flags when
the audio FSK clock reports more datagrams than the video pipeline reconstructed. The
backlog item asks to wire the durability-matrix codec in as a stronger repair path.

Constraints discovered while designing:

- A wholly-missing parity group (data _and_ parity frames gone) is information-theoretically
  unrecoverable from the video stream: the group's XOR parity covers only that group, and the
  manifest's SHA-256 cannot be inverted.
- Today's behavior on that loss is worse than needed: `DurabilityMatrixCodec.TryDecode`
  aborts on the _first_ unrecoverable group, so a mid-stream whole-group loss destroys the
  entire output even though every later group decoded intact, and the failure message is
  generic ("could not reconstruct the payload") with no loss location.

## Decision

Hole-tolerant reconstruction with an authoritative loss map:

1. `DurabilityMatrixCodec` recovers each parity group independently; a group that cannot be
   recovered becomes a hole (zero-filled in the output) instead of aborting the decode. The
   old all-or-nothing `TryDecode` signature stays; a new overload exposes the hole map.
   Hole sizing uses the manifest (`DataFrameCount`/`ParityGroupSize`) when present, else
   neighboring groups' constant `GroupSize` metadata.
2. `DurabilityTransportCodec.TryDecodeFramePackets` gains an overload returning the missing
   group ids; the orchestrator records them as `DecodeMetrics.MissingDatagramIds`.
3. Integrity stays loud: any hole ⇒ `IntegrityStatus.Failed` (length/hash cannot match), with
   the CLI error naming the exact lost datagram groups. No silent corruption, no success
   downgrade — partial recovery is reported, never accepted as intact.

## Consequences

- A mid-stream whole-group loss no longer destroys the whole stream: decoded output contains
  a documented hole and the decode fails with a precise, actionable diagnostic instead of an
  opaque abort.
- Fully-intact streams and single-in-group losses (already repaired) behave exactly as
  before — recovery path unchanged, no wire-format change.
- Known limitation: hole content is zeros, by construction; consumers needing those bytes
  must re-encode. A stronger scheme (per-group multi-parity / Reed–Solomon) is a separate
  follow-up, as is automatic re-request semantics (n/a for a file transport).
