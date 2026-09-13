# CR-20260913-08 — Test-suite hang: ffmpeg children blocked on inherited stdin

**Date:** 2026-09-13 **Status:** Implemented
**Area:** `YTAHD.Core/Core` (`DecoderEngine`), `YTAHD.Core/Infrastructure`
(`FFmpegWrapper`, `FFmpegProbe`), `YTAHD.Tests` (`CodecIntegrationTests`,
`TestFfmpeg`, `ChildProcessPipeRealCodecTests`, new `NoStdinArgumentTests`)

## Context

The full test suite intermittently never returned: ~310 of 313 tests reported passed, then
the host sat frozen indefinitely (observed twice this session, and recorded as a
"post-run shutdown hang" in the 2026-09-12 investigation notes). The signature was a
stranded `ffmpeg.exe` child at ~0.2s CPU with all threads in Wait, and the testhost's CPU
frozen.

## Observed failure

- `dotnet test` full suite: `Passed: 310, Failed: 0` then silence; the run never printed
  its summary. A 7-minute and later a 10-minute cap both hit "TIMEOUT - killing".
- Live capture with `dotnet-stack report` on the hung testhost (PID 62568) showed the
  blocked frame chain:
  `Process.WaitForExit()` ← `CodecIntegrationTests.BinaryGridFrameBitDecoder_Parses_Valid_Header_From_Real_Libx264_Frame`.
- The child of that test (ffmpeg, file→file transcode) was frozen at 0.03–0.05s CPU with
  all 18 threads in Wait state, having written only a 48-byte MP4 header.
- Red herrings: the 2026-09-12 notes blamed a post-run xUnit/vstest handshake hang; the
  first fix attempt (`-nostdin` alone) still froze — but with a *complete* 2019-byte
  output file, which moved the diagnosis forward.

## Root cause

Two stacked defects, both in the test's raw `Process.Start` spawns (and the same latent
pattern in two production probe paths):

1. **Inherited never-closing stdin.** The spawns redirected stdout+stderr but not stdin,
   so ffmpeg inherited the test host's stdin — a pipe that never delivers data and never
   closes. ffmpeg probes stdin for interactive commands and blocked at startup before
   writing a single frame. `-nostdin` did **not** fix this under the test host: the
   decisive experiment showed the same spawn still freezing with `-nostdin` but passing
   in 80 ms once stdin was *explicitly redirected and closed*.
2. **Undrained redirected stderr with unbounded output.** The encode spawns lacked
   `-hide_banner -loglevel error -nostats`, so ffmpeg wrote banner + stream info + x264
   config + summary to the redirected-but-never-drained stderr pipe; past the kernel pipe
   buffer the child blocks forever on its final stderr write and `WaitForExit()` never
   returns. This violated the invariant `ChildProcessPipes` already documents ("every
   redirected child pipe must be drained concurrently with whatever work is waiting on
   that child") — the production paths honor it, these test spawns did not.

The two defects masked each other: fixing (1) alone surfaced (2) with a different
signature (complete output, frozen exit), which is why the first fix attempt looked like
a failure.

## Resolution

- All ffmpeg/ffprobe spawns now set `RedirectStandardInput = true` and close the stdin
  pipe immediately after start (production: `DecoderEngine` decode + ffprobe metadata,
  `FFmpegWrapper` audio extraction; tests: `CodecIntegrationTests`, `TestFfmpeg`,
  `ChildProcessPipeRealCodecTests`). The encode path already redirected stdin for frame
  input; `-nostdin` is kept as defense-in-depth on every **ffmpeg** command line.
- `CodecIntegrationTests` spawns were moved onto the repo's own `ChildProcessScope` +
  `ChildProcessPipes` machinery with concurrent drains and bounded `WaitForExit(timeout)`
  calls, and their encode commands quieted with `-hide_banner -loglevel error -nostats`.
- Regression coverage: new `NoStdinArgumentTests` pins `-nostdin` on the decode argument
  builder (with and without hwaccel), ffprobe resolution, and — the live-hang
  reproduction — a real ffmpeg encode under an inherited stdin with undrained pipes,
  bounded at 30 s.
- Gotcha discovered during validation: **ffprobe has no `-nostdin` option** — adding it
  makes every probe fail with `Failed to set value '-v' for option 'nostdin': Option not
  found` (exit 1), which surfaced as `FFprobe_Reports_FrameCount_For_Short_Video_Clip`
  reporting 0 frames. The explicit stdin redirect+close is the universal fix; `-nostdin`
  is ffmpeg-only defense-in-depth.

## Lessons

- `dotnet-stack report -p <pid>` on a *live* hung testhost is the fastest path to the
  truth; the 2026-09-12 session's "post-run handshake" conclusion was inferred without
  one and was wrong (or at least incomplete — this session's hang was mid-test).
- A fix that "doesn't work" can be a *different* defect surfacing: compare the new
  failure signature (complete output file vs. 48-byte header) before concluding the
  fix was wrong.
- `-nostdin` does not substitute for an explicitly redirected+closed stdin under a host
  whose stdin is a never-closing pipe; both are needed.
- `verbosity=minimal` buffers all output until the end, making a hung run look
  identical to a slow one; `verbosity=normal` streams per-test lines and makes the
  stall point visible.

## Validation

- Focused: `NoStdinArgumentTests` + `CodecIntegrationTests` +
  `ChildProcessPipeRealCodecTests` 10/10 in 3 s (the hang-site class previously froze
  indefinitely; the live-hang reproduction test went from a 30 s timeout to 80 ms).
- Full suite: **317/317 passed in 1 m 36 s** — the first clean full-suite completion
  after three consecutive hang attempts (310/0-then-stall twice, 301/1 with the
  ffprobe `-nostdin` regression).
