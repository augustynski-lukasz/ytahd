# CR-20260902-01 — Document CLI Usage

**Date:** 2026-09-02 **Status:** Implemented
**Area:** `README.md`, `YTAHD.Cli/Program.cs`

## Context

The README documented FFmpeg path overrides but lacked a runnable CLI quick start and a
complete option listing, making the command-line entry point difficult to discover.

## Decision

Added a `CLI Usage` section with encode/decode command syntax, a Phase 3 end-to-end example,
and tables describing every option and its default. The documentation mirrors the command
parser in `YTAHD.Cli/Program.cs`.

## Consequences

Users can now run the CLI directly from the README and select matching modulation modes for
encode/decode without reading the source code.
