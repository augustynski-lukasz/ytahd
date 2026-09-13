# Backlog

Open items only. When an item is resolved, delete it from here and add an ADR in `docs/decisions/`.

## Refactoring plan from `docs/REVIEW.md` Review 1 (2026-09-13)

Design ADRs are already `Accepted` in `docs/decisions/`; each item = one commit = one ADR
(flip its ADR to `Implemented` in the landing commit). Order and rationale in
`docs/PLAN.md` Workstream G. G1–G3 should land **before the next protocol change** (the
Phase 4 follow-up research below is the natural next protocol work).

- **G2 — Wire `--hwaccel` into decode (R2; fixes F5).** Thread
  `DecodeOptions.HardwareAcceleration` into `DecoderEngine` args via
  `FFmpegEncoderArguments.HwaccelValue`; default `none` emits nothing. ADR
  `CR-20260913-04-decode-hwaccel-wiring.md`.
- **G3 — Remove unused dependencies (R3; fixes F4).** Drop `SkiaSharp` + `MathNet.Numerics`
  from `YTAHD.Core.csproj` and the dead `using SkiaSharp;`. ADR
  `TD-20260913-01-remove-unused-dependencies.md`.
- **G5 — Packet buffer-length contract (R5; fixes F9).** `PseudoQamModulator.
GetPacketBufferLength` returns bytes like its siblings; add cross-modulator parity test.
  ADR `CR-20260913-06-packet-buffer-length-contract.md`.
- **G7 — Cleanup batch (R6b; fixes F8, F10, F12, F13).** Single ffprobe resolution, cached
  per-profile tile tables, hoisted `NormalizeModulator`, delete `test-output.txt`. ADR
  `TD-20260913-02-review-cleanup-batch.md`.

## Manifest copy identity in the wire format (deferred protocol change)

From Review 1 finding F6: manifest chunk reassembly in
`DurabilityTransportCodec.CollectSymbols` splits start/end manifest copies by arrival order
(`copyKey = !seenSymbolFrame`) instead of explicit identity. If emission order ever changes,
conflicting copies could be silently reconciled. Fix: a copy-ordinal bit in the manifest
chunk header (backward-compatible, default 0). Deliberately **not** in the cleanup batch —
it is a wire-format change and must ride with the next protocol revision (e.g. alongside
the Phase 4 follow-up research).

## Phase 4 follow-up research (motion as sync clock, not carrier)

Tracked from `docs/PLAN.md` stage C2: motion-synced Phase 3 (sparse always-moving marker
tiles as a per-frame clock enabling a 1-frame DCT lifespan — the highest-throughput
configuration analysed for Phase 4), sub-pixel offset alphabet (½-px steps via phase
correlation), delta-chained offsets (previous-frame-relative, 1-frame lifespan). Stage C1
(combined clock arbitration) has landed; not started.

## Phase 4 / audio FSK cadence conflict (follow-up)

Confirmed during audio FSK validation: Phase 4's 2-physical-frame-per-datagram cadence (see
ADR `F-20260903-01-phase4-motion-vector-design.md`) leaves no room for a hold segment between
pulses at the standard pulse duration, so only the first datagram boundary in a stream is
audio-detectable (back-to-back pulses merge into one continuous tone). Needs either a shorter
FSK pulse duration or a wider Phase 4 cadence to combine usefully — tracked as part of
`docs/PLAN.md` stage C1. Not started.
