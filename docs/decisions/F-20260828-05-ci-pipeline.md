# F-20260828-05 — Add CI Pipeline

**Date:** 2026-08-28 **Status:** Implemented
**Area:** `.github/workflows`

## Context

The project needed automated `dotnet test` runs on every push/PR to guard against
regressions without relying on manual local test runs.

## Decision

Added a GitHub Actions workflow that runs `dotnet test` (formerly FEAT-012).

## Consequences

All subsequent feature work in this project is validated by CI in addition to local runs.
