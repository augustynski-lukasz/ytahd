# Backlog

Open items only. When an item is resolved, delete it from here and add an ADR in `docs/decisions/`.

## Phase 4 follow-up research (motion as sync clock, not carrier)

Tracked from `docs/PLAN.md` stage C2: motion-synced Phase 3 (sparse always-moving marker
tiles as a per-frame clock enabling a 1-frame DCT lifespan — the highest-throughput
configuration analysed for Phase 4), sub-pixel offset alphabet (½-px steps via phase
correlation), delta-chained offsets (previous-frame-relative, 1-frame lifespan). Stage C1
(combined clock arbitration) has landed; not started.

## Combined clock: multi-frame-loss repair (follow-up)

Stage C1 (clock arbitration) is implemented: `DecodeMetrics.HasAudioVideoDatagramMismatch()`
detects when the audio FSK clock's datagram count disagrees with what the video pipeline
actually reconstructed (e.g. a whole parity group silently dropped, which XOR-parity alone
cannot catch) — see ADR `F-20260903-02-audio-fsk-clock-design.md`. This is detection only.
Actually _repairing_ such a loss — most plausibly by wiring the standalone durability-matrix
codec in as a stronger multi-erasure repair path once a mismatch is flagged — is a deeper
integration, deferred and not yet started.

## Phase 4 / audio FSK cadence conflict (follow-up)

Confirmed during audio FSK validation: Phase 4's 2-physical-frame-per-datagram cadence (see
ADR `F-20260903-01-phase4-motion-vector-design.md`) leaves no room for a hold segment between
pulses at the standard pulse duration, so only the first datagram boundary in a stream is
audio-detectable (back-to-back pulses merge into one continuous tone). Needs either a shorter
FSK pulse duration or a wider Phase 4 cadence to combine usefully — tracked as part of
`docs/PLAN.md` stage C1. Not started.
