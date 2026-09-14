# Backlog

Open items only. When an item is resolved, delete it from here and add an ADR in `docs/decisions/`.

## Plain decode path is not guaranteed byte-exact for long streams

From the 2026-09-14 real-user CLI validation (1 MB payloads): the plain decode path
(xor-parity, one loss per group, no manifest) accumulates per-frame bit errors from lossy
libx264 over long streams. Observed: 1 MB phase2 throws (`2 missing data frames` in one
parity group); 256 KB phase2 **silently truncates** (260378 of 262144 bytes, exit 0,
`integrity=unknown`). Root cause and regression coverage in ADR
`F-20260914-03-long-stream-regression-coverage.md`; the durability matrix
(`--durability`, CR-20260913-02) round-trips the same streams byte-exact with
`integrity=passed` and is the supported path for large payloads.

Open follow-up: the CLI knows the input file size at decode time but only *prints*
`payloadRecovered` — it should compare the two and exit non-zero on mismatch, turning the
silent-truncation case into a loud failure even on the plain path. Not started.

## Streaming decode output (memory-bounded payload assembly)

From the 2026-09-14 real-user CLI validation (10 MB @ 4K): the decode assembles the entire
payload in memory and writes the output file only at the end. Fine at 10 MB, fatal at the
1 TB bound — the decode would OOM after hours of CPU with zero output to show. Design is
decided in ADR `F-20260914-02-streaming-decode-output.md` (Status: Accepted): contiguous-
prefix flush to a consumer sink, incremental per-group parity recovery, streaming SHA-256.
Main implementation risk: `DecodedFrameAccumulator` currently recovers losses retroactively
and needs a per-group incremental redesign. Not started.

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
