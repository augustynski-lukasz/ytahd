# TD-20260913-01 — Remove unused NuGet dependencies (SkiaSharp, MathNet.Numerics)

**Date:** 2026-09-13 **Status:** Accepted
**Area:** `YTAHD.Core/YTAHD.Core.csproj`, `YTAHD.Core/Core/EncoderEngine.cs`

## Context

`docs/REVIEW.md` Review 1 finding F4: `YTAHD.Core.csproj` references `SkiaSharp 2.88.9`
(including Linux native assets) and `MathNet.Numerics 4.15.0`, but neither is used. The only
`SkiaSharp` reference in source is the dead `using SkiaSharp;` at
`YTAHD.Core/Core/EncoderEngine.cs:9` — no `SK*` type appears anywhere (all rendering is
hand-rolled byte manipulation in the modulators); `MathNet` has zero references.

Impact: every consumer (CLI, tests, perf, future hosts) pulls native Skia binaries for
nothing — larger publish output, larger audit surface, slower restore.

## Decision

Remove both `PackageReference` entries from `YTAHD.Core.csproj` and delete the dead
`using SkiaSharp;` from `EncoderEngine.cs`. Rebuild and run the full test suite as the
proof (build green + suite green is the complete test strategy; there is no behavioral
surface to regression-test).

## Consequences

- Smaller restore, publish output, and dependency audit surface for the core library.
- Zero protocol or runtime risk; rollback is a trivial revert of the csproj/using edit.
- If a future modulator wants a real 2D/math library, re-add deliberately with a design ADR
  — not by resurrecting unused references.
