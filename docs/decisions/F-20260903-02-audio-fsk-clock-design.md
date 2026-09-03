# F-20260903-02 — Audio FSK Datagram Clock: Combined-Clock Design

**Date:** 2026-09-03 **Status:** Implemented
**Area:** `YTAHD.Core/Audio` (`FskGenerator`, `GoertzelDetector`),
`YTAHD.Core/Infrastructure` (`IFFmpegWrapper` audio mux/demux),
`YTAHD.Core/Application` (`VideoCodecOptions.UseAudioClock`), `EncoderEngine`,
`DecoderEngine`, `DecodeStreamOrchestrator`, `DecodeMetrics.AudioDatagramCount`, `YTAHD.Cli`

## Context

The README describes an audio FSK clock (1000 Hz hold / 1500 Hz datagram-start) as
implemented, but `FskGenerator` only writes silence, the encode path strips audio (`-an`),
and the decoder never reads an audio track. Spec gaps in the README description:

- AAC frames are 1024 samples (~23 ms) plus encoder priming delay; a 60 fps video frame is
  735 samples (16.7 ms), so "exactly 1 frame duration" pulses cannot survive re-encode
  sample-accurately.
- Hard 1000↔1500 Hz switching causes clicks/spectral splatter that lossy audio codecs smear.
- Identical pulses cannot convey how many datagrams were lost.
- YouTube applies loudness normalization, so amplitude-based detection is unreliable.
- Decode behavior for videos without an audio track (all currently produced videos) was
  undefined.

## Decision

Implement the FSK clock as a real, optional transport feature with these corrections:

- **Pulse length ≥ 2 video frames** (~33 ms) with a measured stream delay offset and
  tolerance window (±2 frames after compensation).
- **Phase-continuous, amplitude-ramped** tone synthesis in `FskGenerator`.
- **Two-bin Goertzel detection on frequency ratio only** — never amplitude.
- **Optional end-to-end:** `UseAudioClock` defaults to `false`; absent audio track disables
  the feature at decode without error.
- **Combined clock with Phase 4:** motion markers are the fine, frame-exact clock; FSK pulse
  counting is the coarse absolute datagram counter on an independent timeline. On
  disagreement, the FSK count determines _how many_ datagrams elapsed and missing indices are
  recorded as erasures for the parity/durability layer.

Execution plan: `docs/PLAN.md`, workstream B (stages B1–B4) and combined clock stage C1.

## Consequences

- FSK alone already benefits Phases 1–3: a datagram clock with zero frame-area cost that can
  replace the duplicate-run heuristic.
- For Phase 4, FSK is a recovery-mode redundant clock (absolute loss counting,
  dropped-vs-duplicated disambiguation, bootstrap alignment), not a primary clock; its ROI
  grows as video quality degrades.
- The FFmpeg wrapper contract grows an audio input/output path; fake wrappers in tests must
  cover the audio-enabled argument shape.
- README's Audio-Assisted Clock Synchronization section was corrected to "design only" until
  this lands.

### Real-codec validation (B1–B4)

All four workstream B stages are implemented and tested (`docs/PLAN.md`). Real libx264+AAC
round-trip results (`YTAHD.Tests/YtahdCodecServiceTests.cs`,
`RealFfmpeg_AudioClock_DatagramCount_Matches_VideoLogicalFrameCount`, 16/64/256-byte
payloads): the audio-derived datagram count matched the video logical frame count exactly
in all measured runs on this ffmpeg build (`ffmpeg-20151019`), well within the ±2 frame
tolerance budgeted for AAC priming delay. `-strict -2` is required to enable this build's
experimental native AAC encoder.

**Scope decisions made during implementation:**

- **Phase 4/FSK cadence conflict confirmed.** Phase 4's 2-physical-frame-per-datagram
  cadence leaves no room for a hold segment between pulses at the standard
  `PulseDurationVideoFrames` — back-to-back pulses merge into one continuous tone, so only
  the very first datagram boundary is detectable
  (`GoertzelDetectorTests.DetectDatagramBoundaries_Handles_Phase4Style_BackToBack_Pulses`).
  `EncoderEngine.WriteAudioClockTrackAsync` caps the pulse to fit the available span rather
  than failing, but full Phase4+FSK combined-clock value requires either a shorter pulse
  duration or widening Phase 4's cadence — tracked as a `docs/BACKLOG.md` follow-up.
- **Clock arbitration landed as detection, not automated repair (stage C1).**
  `DecodeMetrics.HasAudioVideoDatagramMismatch()` compares the audio clock's datagram count
  against `TotalRecoveredLogicalFrames` (actual data + parity frames reconstructed after
  XOR recovery), catching the case XOR-parity alone cannot: a _whole_ parity group silently
  dropped (data and parity together), which today truncates the output with no error.
  Actually _recovering_ such a loss — e.g. by wiring the standalone durability-matrix
  codec in as a stronger multi-erasure repair path — remains deferred; see `docs/BACKLOG.md`.
