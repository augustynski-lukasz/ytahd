# CR-20260913-04 — Wire `--hwaccel` into the decode path

**Date:** 2026-09-13 **Status:** Implemented
**Area:** `YTAHD.Cli/Program.cs`, `YTAHD.Core/Core` (`DecoderEngine`, `DecodeOptions`),
`YTAHD.Core/Infrastructure` (`FFmpegEncoderArguments`), `YTAHD.Tests`

## Context

`docs/REVIEW.md` Review 1 finding F5: the CLI parses, validates, and echoes `--hwaccel`
(`TryParseHwaccel`, option registration, help text), and the encode handler sets
`HardwareAcceleration` — but the decode handler never sets it and
`DecoderEngine.DecodeAsync` builds its ffmpeg arguments
(`-hide_banner -loglevel error -i ... -f rawvideo -pix_fmt rgb24 ...`) without any
`-hwaccel` flag. `FFmpegEncoderArguments.HwaccelValue` exists with no decode-side caller.
The CLI help therefore promises a decode-side acceleration experiment that does nothing —
a misleading surface on a project whose bar is honest diagnostics.

## Decision

Wire it (preferred over removal — the enum and mapping already exist and are tested):

1. Add `HardwareAcceleration` to `DecodeOptions` (default `none`).
2. `DecoderEngine.DecodeAsync` prepends `-hwaccel <value> -hwaccel_output_format nv12`
   (value from `FFmpegEncoderArguments.HwaccelValue`) before `-i`; `none` emits nothing, so
   default arguments stay byte-identical to today. `-hwaccel_output_format nv12` is
   mandatory, not optional: without it ffmpeg keeps decoded frames in GPU surface memory
   (hwaccel_output_format defaults to the accel type, e.g. `qsv`), and the rgb24 swscale
   conversion fails with "Impossible to convert between the formats" (exit -40), yielding
   zero decodable frames. Verified manually: `-hwaccel qsv` alone → exit -40, 0 bytes;
   with `-hwaccel_output_format nv12` → exit 0, full rawvideo payload.
3. The CLI decode handler threads the parsed `--hwaccel` value through.

Tests: fake-wrapper argument assertions (pattern from `FFmpegEncoderArgumentsTests`) for
`none` (no flag), `qsv`, `cuda`; one real-FFmpeg decode smoke test with `--hwaccel qsv`
guarded by a capability probe (pattern from `QsvRealCodecTests`). Known gap from Review 1:
decode-side capability probing (`ffmpeg -hwaccels` output) is not implemented — budget a
small probe extension as part of this work; until then the qsv smoke test stays
probe-guarded and may skip on unsupported stacks.

## Consequences

- CLI decode surface matches its advertised behavior; default path unchanged (no flag
  emitted), so rollback is a plain revert.
- Decode-side hwaccel remains an experiment: raw RGB output still must return CPU-visible
  `rgb24` bytes, so gains may be small (consistent with Workstream D's original scoping in
  `docs/PLAN.md`).
- Decode-side hwaccel remains an experiment: raw RGB output still must return CPU-visible
  `rgb24` bytes, so gains may be small (consistent with Workstream D's original scoping in
  `docs/PLAN.md`). The nv12→rgb24 CPU conversion still happens; only the H.264 decode itself
  is offloaded.
- Follow-up (not in this ADR): decode-side capability probe extension if qsv decode proves
  useful in practice. Until then the qsv smoke test stays probe-guarded and may skip on
  unsupported stacks.

## Validation

- Focused: `dotnet test --filter FullyQualifiedName~DecodeHwaccelTests` → 7/7 passed
  (including the real-FFmpeg qsv decode round trip, which failed 0-packet before the
  `-hwaccel_output_format nv12` fix).
- Full suite: 306/306 passed (1 m 24 s).
