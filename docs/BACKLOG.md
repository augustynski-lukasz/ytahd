# Backlog

Open items only. When an item is resolved, delete it from here and add an ADR in `docs/decisions/`.

## Migrate to .NET 10

The solution targets `net8.0` across all projects (Core, Cli, Tests, Perf, probe). .NET 10
is the current LTS (support window runs to Nov 2028); .NET 8 LTS support ends Nov 2026, so
this migration should land before then. Scope:

- Bump `TargetFramework` in all five csproj files + CI (`dotnet-test.yml` `dotnet-version`)
  + `Dockerfile.test` base image (`mcr.microsoft.com/dotnet/sdk:10.0-bookworm-slim`) +
  README environment mentions.
- Verify the full suite on real FFmpeg in the pinned container (per RM-14 the container is
  the canonical reproduction environment; 390/390 is the current baseline there).
- Check `System.CommandLine` (beta4 on `YTAHD.Cli`) for breaking changes — a beta
  dependency is the most likely friction point.
- Re-baseline perf afterwards (per `CR-20261009-01`, a runtime change is a new environment;
  run `YTAHD.Perf bench --json` and compare with `scripts/check-perf-regression.ps1`).
- RM-11/RM-12 golden vectors and release fixtures must stay green (they pin wire format,
  not runtime — but the compat tests are the safety net if anything shifts).

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
