# CR-20261007-01 — CLI decode exits non-zero on untrustworthy recovery (RM-03)

**Date:** 2026-10-07 **Status:** Implemented
**Area:** `YTAHD.Cli/Program.cs`, `YTAHD.Tests/CliDecodeExitCodeTests.cs`, `README.md`, `docs/BACKLOG.md`, `docs/ROADMAP.md`

## Context

The 2026-09-14 real-user CLI validation (ADR `F-20260914-03-long-stream-regression-coverage.md`)
showed the plain decode path (no `--durability`, no manifest) can return a **silently
truncated** payload: 256 KB phase2 recovered 260378 of 262144 bytes with **exit code 0**
and `integrity=unknown`. The CLI printed `payloadRecovered` and `outputBytes` in the
summary but never compared them, so scripts and CI could not detect the failure. The
durability path already reports `integrity=failed` in the summary — but that too left the
exit code at 0. ROADMAP story RM-03 (E1-adjacent trust guardrail) asked for the caveat to
be promoted into the README Quick Start and for the CLI to fail loudly.

## Decision

The decode handler in `YTAHD.Cli/Program.cs` now gates the exit code on three signals,
evaluated **after** the summary line is printed (diagnostics stay on stdout):

1. **`integrity=failed`** (durability path): the recovered payload does not match the
   stream manifest's declared length or SHA-256 → exit 1 with a stderr message stating
   the output is NOT trustworthy.
2. **`--expected-bytes` mismatch** (plain path): a new decode option takes the original
   payload size; when set and the written output size differs → exit 1. On the plain path
   there is no manifest, so the caller-supplied expected size is the only authoritative
   truncation guard. This replaces the BACKLOG idea of comparing against "the input file
   size" — the decode input is the *video*, whose size is unrelated to the payload size,
   so an explicit option is the correct contract.
3. **Audio-clock datagram mismatch**: when `--audio-clock` reports more datagrams than
   logical frames recovered (beyond tolerance) → exit 1, since frames were likely lost
   beyond parity repair.

All failure reasons are written to stderr prefixed `Decode FAILED:`; the exit code is 1.
Happy paths (integrity=passed, correct expected size, no clock mismatch) keep exit 0.

README: the plain-path caveat is now a callout at the top of **CLI Usage** (Quick Start
position) rather than only in the Reliability section, and the decode option table
documents `--expected-bytes`.

## Observed failure

Not a test failure but a silent-failure audit: `LongStreamRealCodecTests` documented the
truncation (exit 0, integrity=unknown) and `DecodeAggregator`'s remarks already said "the
CLI reports integrity and exit status from the metrics" — but no CLI code did so. No
existing test asserted a non-zero decode exit code anywhere.

## Root cause

The decode `SetHandler` in `YTAHD.Cli/Program.cs` only set `ctx.ExitCode` on argument
validation errors; the success path fell through with exit 0 regardless of the metrics.
The plain path additionally had no expected-size source at all
(`DecodeOptions`-based `DecodeAsync` passes `expectedOutputBytes = 0`, so
`ResolveExpectedOutputBytes` just sums whatever decoded — truncation invisible).

## Resolution

- Exit-code gate after the summary print (three signals above), stderr messages, exit 1.
- New `--expected-bytes` decode option as the plain-path truncation guard.
- Regression tests in `YTAHD.Tests/CliDecodeExitCodeTests.cs` (real CLI process, real
  FFmpeg): wrong expected size → non-zero + stderr message; correct expected size → 0;
  durability round-trip → 0 with `integrity=passed`.
- BACKLOG item "Plain decode path is not guaranteed byte-exact for long streams" deleted
  (its open follow-up is resolved; the underlying limitation itself is documented in
  README + ADR F-20260914-03 and is inherent to the plain path).

## Consequences

- Scripts/CI can now trust the decode exit code on both paths.
- The plain path remains limited (no whole-payload verification without a manifest);
  `--expected-bytes` only catches *length* truncation, not same-length corruption —
  `--durability` remains the endorsed path for anything beyond a few KB.
- `--expected-bytes` is advisory input, not protocol state; no wire-format change.
- Follow-up (not scheduled): plumb the manifest's `TotalPayloadBytes` into the plain path
  when a legacy stream carries one, so the gate can be automatic.

## Lessons

- "The CLI reports integrity and exit status from the metrics" was written in the core's
  doc comments but never implemented — a contract stated only in prose, with no test
  pinning it, stayed unimplemented for weeks. Exit-code contracts need process-level
  regression tests, not just summary-string assertions.
- The BACKLOG follow-up's proposed fix ("compare against the input file size") was wrong:
  the decode input is the video, not the payload. Reading the actual handler before
  implementing caught this before shipping a nonsense comparison.
