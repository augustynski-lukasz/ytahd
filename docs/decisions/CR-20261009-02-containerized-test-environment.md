# CR-20261009-02 — Containerized reproducible FFmpeg test environment (RM-14)

**Date:** 2026-10-09
**Status:** Implemented
**Area:** `Dockerfile.test` (new), `.dockerignore` (new),
`YTAHD.Tests/Phase4DurabilityDiscriminatorTests.cs` (C# 12 compat fix), `README.md`
(container environment section), `docs/ROADMAP.md` (RM-14 status)

## Context

RM-14's story: a contributor wants FFmpeg-dependent scenarios reproducible across
machines. The real-codec test subset depends on whatever FFmpeg is installed locally
(`TestFfmpeg` probes `YTAHD_FFMPEG_PATH`, known local installs, then `PATH`) — two
machines can run "the same" suite against different FFmpeg builds with different
codec behavior, and a local FFmpeg upgrade silently changes the codec baseline the
suite validates against.

## Decision

- **`Dockerfile.test` pins .NET 8 SDK + FFmpeg 5.1.9-0+deb12u1** (the Debian 12
  bookworm stable package, verified via `apt-cache policy` in the base image; the
  initially assumed 6.1.2-3 is Ubuntu 24.04's version and does not exist in bookworm)
  with `apt-mark hold ffmpeg` so an apt upgrade inside the image lifecycle cannot
  silently change the codec baseline. Adds the same native deps the GitHub Actions
  workflow installs (`libfontconfig1`, `libfreetype6`).
- **Full suite by default; per-class runs via the container command.** The image
  restores/builds `YTAHD.Tests` at build time, so `docker run --rm -t ytahd-test`
  runs the whole suite and `docker run … --filter "FullyQualifiedName~<Class>"`
  targets one class without rebuilding. `TestFfmpeg`'s PATH fallback finds the
  pinned `ffmpeg` with no configuration.
- **GPU/accelerated tests self-skip in the container** (verified pattern, not a
  guess): `QsvRealCodecTests` probes the encoder via `FFmpegCapabilities.ProbeEncoder`
  and returns silently when the device is absent; `DecodeHwaccelTests` is
  probe-guarded the same way. A container has no GPU device, so those tests skip
  rather than fail — the container validates the CPU codec contract.
- **Native performance testing stays outside the container** (RM-14 requirement):
  container CPU/memory limits and layered filesystems make timings incomparable to
  native runs, and cross-image rebuilds change the codec build (a re-baseline event,
  per `scripts/check-perf-regression.ps1`'s environment rule). `YTAHD.Perf` runs on
  the host only; `perf-*.json` is dockerignored to keep host baselines out of images.
- **Not wired into GitHub Actions CI yet.** The existing workflow already installs a
  (floating) Ubuntu FFmpeg; switching it to the pinned image is a follow-up that
  trades runner time for build determinism and should be decided with CI-time data.

## Consequences

- **VERIFIED END-TO-END (2026-10-10): `docker build` + full suite in the container =
  390/390 passed in 6m19s** (Docker Engine 29.9.0 inside WSL Ubuntu 24.04, pinned
  FFmpeg 5.1.9-0+deb12u1, QSV/hwaccel tests self-skipped as designed). The RM-14
  acceptance ("documented container run of the real-codec test subset") is met and
  exceeded (full suite, not just a subset).
- "Works on my FFmpeg" divergences in real-codec tests get a canonical reproduction
  environment; a codec-behavior question can be answered by "run the container" on
  any machine with Docker.
- Known limitation: the pinned version is the Debian package, not an upstream static
  build — it lags FFmpeg master and disables nothing the suite needs, but a contributor
  testing a brand-new codec behavior still needs a local master build
  (`YTAHD_FFMPEG_PATH` override continues to work by bind-mounting or rebuilding).
- Follow-ups: optional CI wiring (above); optionally tag images per FFmpeg version if
  cross-version codec comparisons become a recurring need.

## Observed failure (first real container build)

Two defects, only visible outside the Windows dev machine:

1. **MSB4018 / NuGet PackagingException: "Unable to find fallback package folder
   'C:\Program Files (x86)\Microsoft Visual Studio\Shared\NuGetPackages'"** inside
   the container's `dotnet build`. Red herring: it looked like a NuGet config problem
   in the base image.
2. **CS8652 "ref and unsafe in async methods is Preview"** in
   `Phase4DurabilityDiscriminatorTests.cs` — but only in the container; the Windows
   build was green.

## Root cause

1. `.dockerignore` used root-anchored `bin/` / `obj/` patterns, which do NOT match
   the nested `YTAHD.Core/obj/`, `YTAHD.Cli/obj/`, `YTAHD.Tests/obj/` folders. The
   image therefore contained Windows-built `project.assets.json` files whose resolved
   fallback folders point at Windows-only paths (`C:\Program Files (x86)\...`), which
   the Linux SDK then failed to find. Defect 1 masked defect 2: the build always died
   in package resolution before the compiler ever ran.
2. `Phase4DurabilityDiscriminatorTests.cs` held a `Span<byte>` local (`expected`)
   across subsequent statements inside an `async` method — legal under C# 13, which
   the newer Windows SDK used, but rejected by the container's pinned .NET 8 SDK
   (C# 12). The suite was silently Windows-SDK-dependent.

## Resolution

1. `.dockerignore` patterns changed to `**/bin/` and `**/obj/` with a comment
   explaining the Windows-`project.assets.json` failure mode, so image inputs are
   source-only on any machine.
2. The diagnostic hex line now computes `pkt.AsSpan(...)` inline (no Span local
   spanning statements); identical output, compiles under C# 12 and C# 13. Suite
   stays buildable on both SDK generations.

## Lessons

- A `bin/`/`obj/` ignore without the `**/` prefix only matches the repo root — the
  classic Docker-on-.NET trap, and it produces a misleading NuGet error instead of a
  "stale files included" signal.
- "The suite builds green" is machine-relative when test code uses newer language
  features than the pinned SDK: a pinned-container environment only proves its worth
  by catching exactly this drift on its first run.
