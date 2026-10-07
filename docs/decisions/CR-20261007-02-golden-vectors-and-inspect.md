# CR-20261007-02 — Golden vectors + `ytahd inspect` (RM-11, RM-13)

**Date:** 2026-10-07 **Status:** Implemented
**Area:** `YTAHD.Tests/GoldenVectors/`, `YTAHD.Tests/GoldenVectorFixtureTests.cs`, `YTAHD.Core/Core/InspectReport.cs`, `YTAHD.Core/Core/DecoderEngine.cs`, `YTAHD.Cli/Program.cs`, `README.md`, `docs/ARCHITECTURE.md` §7.7, `docs/ROADMAP.md`

## Context

The byte-exact wire format (frame packet v1/v2, manifest chunking) is the project's main
asset (RM-19), but until now every wire-format test ran the encoder to produce its inputs
— so a codec change could silently redefine the format and all tests would still pass.
RM-11 asked for committed fixtures that decode **without running the encoder**. RM-13
asked for a machine-readable `inspect` command so streams can be triaged programmatically
(before RM-01 transit evidence, triage is also how a failed transit run gets diagnosed).

## Decision

**RM-11 — golden vectors.** `YTAHD.Tests/GoldenVectors/` holds three fixture sets, each
as `*.packets.hex` (one hex packet per line), `*.payload.hex` (expected decoded bytes),
and a `.json` sidecar pinning the schedule/manifest/header layout:

1. `golden-v2-phase1-256b` — v2 packets, 256-byte deterministic payload, phase1,
   single-chunk manifest (8 data + 2 parity + 2 manifest copies).
2. `golden-v2-phase1-256b-chunked-manifest` — same payload with a 64-byte frame capacity,
   pinning the manifest chunking wire format (2 chunks × 2 copies).
3. `golden-v1-data-70000b` — a hand-built legacy v1 packet above the 16-bit length wrap,
   pinning the hash-gated wrapped-length recovery path.

Payloads are deterministic (`byte[i] = (i*11+3) % 251`), no RNG. Fixtures are generated
by `GoldenVectorFixtureGenerator` only when `YTAHD_REGENERATE_GOLDEN=1` is set (no-op
otherwise); `GoldenVectorFixtureTests` decodes them strictly (per-frame SHA-256 enforced)
and via `TryDecodeFramePacketsWithHoles`, asserting byte-exactness and manifest fields.

**RM-13 — `inspect`.** New CLI command `inspect <input>`:
`DecoderEngine.InspectAsync` reuses the real ffmpeg pipe + frame-bit-decoder path but
stops after packet parsing — no payload assembly, no aggregation. It reports container
geometry (ffprobe), packet counts by type, invalid/canonical frame counts, declared vs
recovered data-frame counts, missing data-frame indices, the reconciled manifest (chunk
reassembly + copy agreement, same rules as the durability decode), and
`looksLikeYtahdStream`. The CLI serializes it as camelCase JSON; exit 0 iff a manifest
was recovered and copies agree. Schema documented in ARCHITECTURE §7.7.

## Consequences

- Wire-format regressions are now caught byte-exactly without the encoder; any fixture
  diff must ride with a spec update (RM-19) and an ADR.
- `inspect` gives tooling (and the RM-01 transit harness) a cheap pre-decode triage step;
  its exit code is scriptable.
- The generator test is environment-gated, so normal test runs never write files.
- Fixture regeneration is manual and deliberate — the diff review is the protocol-change
  gate.

## Lessons

- The first fixture run exposed a test bug immediately (manifest hash compared against
  hex-of-payload instead of hash-of-payload) — exactly the kind of mistake golden vectors
  exist to catch; the fixtures were correct, the assertion was not.
- PowerShell `Set-Content -Encoding UTF8` (PS 5.1) writes a BOM and double-encodes
  existing UTF-8 content — README.md had to be restored from HEAD and edited with the
  file tools instead. Never rewrite whole files via PowerShell here.
