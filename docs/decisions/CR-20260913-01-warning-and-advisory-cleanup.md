# CR-20260913-01 — Warning and dependency advisory cleanup

**Date:** 2026-09-13 **Status:** Implemented
**Area:** `YTAHD.Core/YTAHD.Core.csproj`, `YTAHD.Core/Infrastructure/FFmpegWrapper.cs`, `YTAHD.Tests/YTAHD.Tests.csproj`

## Context

The build carried 20 warnings: NU1903 (SkiaSharp 2.88.3, high-severity advisory
GHSA-j7hp-h8jx-5ppr), CS1998 at `FFmpegWrapper.cs(62,43)` (`StartAsync` declared `async`
but never awaited), and 18 CS8632 nullable-annotation warnings in test files.

## Decision

- Bump `SkiaSharp` / `SkiaSharp.NativeAssets.Linux` to **2.88.9** — the patched release on
  the same 2.88 line, fixing the advisory without the breaking API changes of the 3.x line.
- Fix CS1998 by removing `async` from `FFmpegWrapper.StartAsync` and returning the
  `ProcessWrapper` via `Task.FromResult` — the method never awaited anything (stderr drain
  is deliberately fire-and-forget per CR-20260912-04). Public signature unchanged.
- Add `<Nullable>enable</Nullable>` to `YTAHD.Tests.csproj` to match `YTAHD.Core`, making
  all CS8632 warnings vanish without touching any test source.

## Consequences

- Build is warning-free (0 warnings). No test-source changes were needed.
- Full suite green: 287/287 (1 m 20 s), confirming the SkiaSharp bump and `StartAsync`
  signature behavior are regression-free.
- `Nullable` now active in the test project; any future nullable misuse there will surface
  as a warning rather than being silently ignored.
