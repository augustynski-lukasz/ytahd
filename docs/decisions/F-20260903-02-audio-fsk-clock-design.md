# F-20260903-02 — Audio FSK Datagram Clock: Combined-Clock Design

**Date:** 2026-09-03 **Status:** Accepted
**Area:** `YTAHD.Core/Audio` (`FskGenerator`, new `GoertzelDetector`),
`YTAHD.Core/Infrastructure` (`IFFmpegWrapper` audio mux/demux),
`YTAHD.Core/Application` (`VideoCodecOptions.UseAudioClock`), `DecodeStreamOrchestrator`

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
