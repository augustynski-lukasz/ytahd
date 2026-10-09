# CR-20261007-04 — Streaming, memory-bounded durability decode output (RM-10)

**Date:** 2026-10-08
**Status:** Implemented
**Area:** `YTAHD.Core/Core/StreamingDurabilityDecoder.cs` (new),
`YTAHD.Core/Core/DecodeStreamOrchestrator.cs` (`ProcessToSinkAsync`),
`YTAHD.Core/Core/DecoderEngine.cs`, `YTAHD.Core/Application/DecodeOptions.cs`,
`YTAHD.Tests/StreamingDurabilityDecoderTests.cs` (new)

## Context

The durability decode assembled the entire payload in memory and wrote the output file only
at the end (the 2026-09-14 real-user validation: fine at 10 MB, fatal at archival scale —
an hours-long decode OOMs with zero output to show). The design was already accepted in
ADR `F-20260914-02-streaming-decode-output.md`: contiguous-prefix flush to a consumer sink,
incremental per-group parity recovery, streaming SHA-256, hole semantics matching the batch
hole-tolerant decode (CR-20260913-02). This ADR records the implementation that landed.

## Decision

- **`StreamingDurabilityDecoder`** (internal, `YTAHD.Core/Core/`, per the accepted design):
  consumes strictly-valid frame packets in stream order and flushes every payload byte to a
  caller-provided sink `Action<byte[],int,int>` as soon as its position is final. Peak
  memory is O(one parity group), not O(payload).
- **Group lifecycle.** A group finalizes when all its data symbols arrived (flush in symbol
  order), or its parity arrived with exactly one data symbol missing (XOR repair, then
  flush). With parity present and more than `ParitySymbolsPerGroup` symbols missing, the
  group is beyond the XOR budget: it is recorded in `MissingGroups` and its zero-filled
  hole is written **immediately** (the parity symbol is the group's last packet in stream
  order, so the verdict is final at that moment; writing in payload order keeps the sink
  valid for incremental consumers).
- **Wholly-absent groups** (tail truncation — no packet, parity included, ever arrives) can
  only be detected at `Finalize`, where the manifest schedule is authoritative: the zero
  bytes for every group from `_flushedBytes / groupBytes` to the last schedule group are
  written and their ids appended to `MissingGroups`, before the integrity hash comparison.
  This keeps hole/integrity semantics identical to the batch decode (CR-20260913-02):
  output spans the manifest schedule, loss map is reported, `integrity=failed` on
  hash/length mismatch.
- **Duplicate physical copies.** The encoder writes every logical packet 3× physically
  (`RepeatCount`); the batch layer deduplicates implicitly by dictionary key, but a
  streaming decoder must ignore packets for already-resolved (flushed or holed) groups
  explicitly — otherwise late duplicate copies re-create the group and mis-read the
  duplicates as new losses.
- **In-order arrival** is required and asserted: a group finalizing when
  `_flushedBytes != groupStartByte` throws `InvalidDataException` (loud failure, per the
  real-FFmpeg contract; all current encoders emit groups in order).
- **Manifests** reconcile with the same copy-key discipline as the batch path
  (`copyKey = !seenSymbolFrame`, start copy before the first symbol frame, end copy after);
  conflicting intact copies throw `InvalidDataException` exactly like the batch decode.
- **Partial final group length**: symbols carry the header-declared `payloadLength`;
  flush slices each symbol to its real length so the flushed byte count equals the
  manifest's `TotalPayloadBytes` exactly.
- **Integrity verdict**: streaming SHA-256 over every flushed byte (including hole zeros)
  compared against the manifest — `passed` / `failed`; `frame-only` when no manifest was
  recovered. Semantics unchanged from batch.
- **Wiring**: `DecodeStreamOrchestrator.ProcessToSinkAsync(Stream rgbStream, Stream sink, …)`
  drives the streaming decoder on the serial path; `DecodeOptions.StreamingOutput` (opt-in)
  makes `DecoderEngine.DecodeAsync` write the output via the sink into a `FileStream`
  instead of buffering a `byte[]`. The batch `ProcessAsync` path (parallel orchestrator
  included) is unchanged; non-durability decodes are unchanged.

Four regression tests (`StreamingDurabilityDecoderTests`): byte-exact equality with the
batch output; incremental flushing before the stream ends (partial stream → `FlushedBytes`
strictly between 0 and payload size); hole reported + `integrity=failed` when the final
group is truncated away; conflicting manifests fail loudly.

## Observed failure

`Streaming_Decode_Reports_Hole_For_Wholly_Missing_Group` failed as
`Assert.True(missing.Count > 0)` — "Dropping the final group never produced a hole (last
integrity=Failed)" — with diagnostics showing the confusing combination
`integrity=Failed … missing=[] flushed=1920` (manifest declares 2048). An earlier iteration
had also shown the opposite red herring: test 2 failing with
`missing=[0,1,2,3,4,…]` (every group a hole) after the decoder was otherwise correct.

## Root cause

Two stacked defects, one masking the other:

1. **Duplicate physical copies re-created flushed groups** (test 2's `missing=[0,1,2,…]`):
   `Add` buffered every packet; after a group flushed, the 2nd/3rd physical copies of its
   packets arrived and rebuilt the `GroupBuffer`, so the duplicate parity copy saw >1
   "missing" symbols → `MarkHole` fired for every group. This masked defect 2 because the
   hole path was at least _reached_.
2. **Wholly-absent tail groups never produced a hole** (test 3's `missing=[]`): `MarkHole`
   is only reachable from `TryFlushGroup`, which requires the group's parity symbol to have
   arrived. A truncated tail group (dropped with its parity) never entered `TryFlushGroup`,
   and `Finalize` computed `integrity=Failed` from the short `_flushedBytes` but wrote no
   hole and reported no missing groups.

## Resolution

- Defect 1: `_resolvedGroupIds` guard in `Add` — packets for already-resolved (flushed or
  holed) groups are ignored; per-symbol real lengths tracked from header `payloadLength`.
- Defect 2: `Finalize` materializes tail holes from the manifest schedule when
  `_flushedBytes < TotalPayloadBytes`, appending the covered group ids to `MissingGroups`
  and zero-filling the sink before the integrity comparison.
- Both changes are backward-safe: the batch decoder is untouched, and the streaming
  decoder's output/loss-map contract now provably matches it (byte-exact test).

## Lessons

- **Physical repeats are invisible at the batch layer but fatal to incremental designs.**
  The batch decoder's dictionary-dedup-by-key hides the 3× emission entirely; any streaming
  rewrite must add resolved-state tracking or duplicate copies masquerade as fresh losses.
- **A loss detector that requires the victim to announce itself can't see whole-group
  losses.** Parity-arrival was used as the "verdict is final" trigger, but a group that
  loses _everything_ (including parity) never triggers — only the manifest schedule at
  finalize can size that hole. Both detectors are needed: parity-arrival for partial
  groups, manifest-clamp at finalize for wholly-absent ones.
- **Test order mattered**: fixing defect 1 first made test 3 fail _differently_ (silent
  `missing=[]` instead of bogus holes), which is what localized defect 2. Driving one test
  to green at a time kept the two root causes separable.

## Consequences

- Durability decodes can now stream to disk with O(one group) memory; large-payload
  recovery no longer requires buffering the whole payload. Consumers get bytes
  incrementally in payload order, with holes zero-filled in place.
- Aborted/truncated decodes produce well-defined partial output plus the loss map
  (`MissingGroups`), matching the batch hole-tolerant semantics.
- Scope: the streaming path is serial-only and durability-only for now; the parallel
  orchestrator path and plain (non-durability) decode remain batch. `DecodeOptions.StreamingOutput`
  is opt-in; default behavior is unchanged.
- Follow-up (backlog-adjacent, noted in ROADMAP): a `YTAHD.Perf` peak-RSS measurement on a
  ≥ 100 MB payload to formally verify the acceptance clause of F-20260914-02; sliding-window
  resumability remains future work.
