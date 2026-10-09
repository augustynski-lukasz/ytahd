# CR-20261009-02 — Containerized reproducible FFmpeg test environment (RM-14)

**Date:** 2026-10-09
**Status:** Implemented
**Area:** `Dockerfile.test` (new), `.dockerignore` (new), `README.md`
(container environment section), `docs/ROADMAP.md` (RM-14 status)

## Context

RM-14's story: a contributor wants FFmpeg-dependent scenarios reproducible across
machines. The real-codec test subset depends on whatever FFmpeg is installed locally
(`TestFfmpeg` probes `YTAHD_FFMPEG_PATH`, known local installs, then `PATH`) — two
machines can run "the same" suite against different FFmpeg builds with different
codec behavior, and a local FFmpeg upgrade silently changes the codec baseline the
suite validates against.

## Decision

- **`Dockerfile.test` pins .NET 8 SDK + FFmpeg 6.1.2-3** (the Debian 12 bookworm
  stable package) with `apt-mark hold ffmpeg` so an apt upgrade inside the image
  lifecycle cannot silently change the codec baseline. Adds the same native deps the
  GitHub Actions workflow installs (`libfontconfig1`, `libfreetype6`).
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

- "Works on my FFmpeg" divergences in real-codec tests get a canonical reproduction
  environment; a codec-behavior question can be answered by "run the container" on
  any machine with Docker.
- Known limitation: the pinned version is the Debian package, not an upstream static
  build — it lags FFmpeg master and disables nothing the suite needs, but a contributor
  testing a brand-new codec behavior still needs a local master build
  (`YTAHD_FFMPEG_PATH` override continues to work by bind-mounting or rebuilding).
- Known limitation: not validated end-to-end on this workstation (no Docker daemon
  installed; WSL exists but only the distro, not Docker Desktop). The filter/PATH
  behavior the image relies on is validated natively; first `docker build` on a
  Docker-capable machine is the remaining verification step.
- Follow-ups: optional CI wiring (above); optionally tag images per FFmpeg version if
  cross-version codec comparisons become a recurring need.
