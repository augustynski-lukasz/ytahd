# CR-20260911-01 - FFmpeg Directory CLI Path

**Date:** 2026-09-11 **Status:** Implemented
**Area:** CLI FFmpeg options, FFmpeg process wrapper, FFprobe lookup

## Context

The CLI accepted `--ffmpeg-path` but expected the value to be the exact FFmpeg executable.
When a user supplied the FFmpeg `bin` directory instead, process startup would treat the
directory as the executable. A common typo, `--ffpmeg-path`, also produced a parse error
before the command could run.

## Decision

Resolve explicit FFmpeg directory paths to `ffmpeg.exe` or `ffmpeg` before starting FFmpeg,
and reuse the resolved directory to locate `ffprobe.exe` or `ffprobe`. The CLI also accepts
`--ffpmeg-path` as a compatibility alias for `--ffmpeg-path`.

## Consequences

Users can pass either the FFmpeg executable path or the containing directory. The documented
option remains `--ffmpeg-path`, while the misspelled alias prevents a simple typo from
blocking command execution.
