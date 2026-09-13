# F-20260914-01 — CLI `--durability` flag

**Date:** 2026-09-14 **Status:** Implemented
**Area:** `YTAHD.Cli/Program.cs`

## Context

The durability matrix (CR-20260913-02 hole-tolerant reconstruction, CR-20260912-05
integrity verification) was reachable only through the service API
(`EncodeOptions.UseDurabilityMatrix` / `DecodeOptions.UseDurabilityMatrix`), which the test
suite exercises extensively — but the CLI had no flag for it. A real end-to-end CLI
validation run (2026-09-14) confirmed every other pipeline capability is CLI-reachable;
the matrix was the one gap, leaving the project's headline durability feature unusable
from the command line.

## Decision

Add `--durability` / `-D` to both CLI commands:

- **encode:** sets `UseDurabilityMatrix = true` with the conservative defaults the test
  suite pins (32-byte symbols, groups of 4, one parity symbol per group) via a shared
  `DefaultDurabilityMatrixOptions()` helper. The stream gains per-symbol hashes, XOR
  parity groups, and a start/end manifest carrying the payload SHA-256.
- **decode:** enables matrix reconstruction with the same defaults. The decode summary's
  `integrity=` field becomes meaningful: `passed` (manifest SHA-256 match), `failed`
  (mismatch — output contains documented zero-filled holes with the loss map in the
  metrics), or `frame-only` (legacy stream without a manifest).

No service-layer or wire-format change; the flag only surfaces existing options.

## Consequences

- The durability matrix is usable end-to-end from the command line; integrity reporting
  is visible in real runs, not just tests.
- Encoding with `--durability` costs ~3× more frames (128 data + 34 parity + manifest
  frames vs 43 plain data frames for a 4 KB payload at 640×480 phase1) — the price of
  per-symbol redundancy and the manifest.
- Decoding a durability stream *without* `--durability` still succeeds byte-exact via the
  legacy path (the matrix frames parse as ordinary data/parity packets); only the
  whole-payload integrity verification is skipped (`integrity=unknown`). Flag mismatch is
  therefore not dangerous, but `--durability` should be used on decode to get the
  verification.

## Validation

- Real CLI round trip (4 KB, phase1, 640×480@30, real ffmpeg): encode 162 logical frames
  / 486 video frames in 1.6 s; decode 100% completion in 1.4 s with
  `integrity=passed`, `quality=degraded (duplicateQuality=0, recoveredGroups=486)` —
  the advisory verdict correctly flags the matrix path's high recovered-group count
  (every packet counts as a recovered group in the durability branch), while integrity
  stays the sole acceptance authority. Output byte-exact.
- Negative test: decoding the same stream without `--durability` also produced
  byte-exact output through the legacy path (`integrity=unknown`), confirming the flag
  mismatch is benign.
- Full suite: 331/331 passed (CLI change is presentation-only; no test surface changed).
