# F-20260828-04 — Extract Core Library, Add App Service Layer, and Perf Tool

**Date:** 2026-08-28 **Status:** Implemented
**Area:** `YTAHD.Core`, `YTAHD.Core.Application`, `YTAHD.Perf`

## Context

Encode/decode logic was only reachable from the CLI project. Future hosts (GUI, web) and a
dedicated performance-analysis tool needed a reusable library boundary.

## Decision

- Moved reusable encoding/decoding functionality into the dedicated `YTAHD.Core` library
  project (formerly FEAT-018).
- Added `YTAHD.Core.Application` as a shared app-layer service (`YtahdCodecService`) so CLI,
  GUI, and web hosts can call a stable API instead of the low-level engines directly
  (formerly FEAT-019).
- Added the standalone `YTAHD.Perf` project to compute algorithm overhead and bandwidth
  statistics (formerly FEAT-017).

## Consequences

`YTAHD.Core.Application.YtahdCodecService` became the preferred integration point for all
future host code, per the project's architecture conventions.
