# F-20260903-03 - Release Build Workflow

**Date:** 2026-09-03 **Status:** Implemented
**Area:** GitHub Actions, YTAHD CLI distribution

## Context

The repository had continuous integration for the test suite but no repeatable way to
publish downloadable CLI binaries for a tagged release. End users should not need the
.NET runtime installed to run the released CLI.

## Decision

Add a tag-triggered GitHub Actions workflow for tags matching `v*`. The workflow runs the
full test project, publishes the CLI as self-contained single-file binaries for Windows
`win-x64` and Linux `linux-x64`, packages each binary as a ZIP archive, and creates a
GitHub release with both archives attached. FFmpeg remains a separate prerequisite and is
not bundled with the CLI.

## Consequences

Releases provide a directly downloadable Windows `.exe` archive and a Linux executable
archive without requiring a .NET installation. Self-contained artifacts are larger, and
users still need FFmpeg available on `PATH` or supplied through `--ffmpeg-path`.
