# F-20260914-03 — Long-stream real-codec regression coverage

**Date:** 2026-09-14 **Status:** Implemented
**Area:** `YTAHD.Tests` (`LongStreamRealCodecTests`)

## Context

The 2026-09-14 real-user CLI validation (1 MB payload) exposed a failure class the suite
could not see: every real-codec phase2 test used 512–4096 byte payloads (1–5 data
frames), while lossy libx264 bit-error accumulation only manifests over **many** frames.
Observed at scale:

- **1 MB phase2, plain path** (1188 data frames): `Parity group starting at frame 560 has
2 missing data frames` — loud `InvalidDataException`. At 4K (34 data frames) the same
  failure hit group 4; serial encode reproduced it, so it is not a parallelism bug.
- **256 KB phase2, plain path** (297 data frames): **silent truncation** — decode
  "succeeded", returned 260378 of 262144 bytes, exit 0, `integrity=unknown`. Worse than
  the throw: no signal at all.
- **1 MB phase2, durability matrix**: byte-exact, `integrity=passed` — the designed
  answer absorbs the bit errors (per-symbol hashes + multi-erasure repair).
- **256 KB phase1, plain path** (2703 data frames): byte-exact — the binary modulator's
  black/white margin survives where 16-PAM levels do not.

Root cause: `FramePacketCodec.TryDecode` requires the per-frame SHA-256 to match exactly;
one wrong bit rejects the packet. With 3 physical copies per frame, a frame is lost only
when all 3 copies carry ≥1 bit error — rare per frame, but over hundreds of frames the
probability of ≥2 losses in one xor-parity group becomes material. CRF 23 leaves the
17-step PAM levels of phase2 with little margin; phase1's binary levels have enormous
margin. The suite's 5-frame ceiling made the accumulation invisible.

## Decision

Add `LongStreamRealCodecTests` — real-codec round trips at frame counts the suite never
previously exercised:

1. **`Phase2_LongStream_PlainPath_Is_Not_Guaranteed_ByteExact`** (256 KB, 297 frames):
   pins the honest contract — the plain path either recovers byte-exact **or** fails in a
   way the metrics expose (`TotalDecodedPayloadBytes` mismatch or
   `InvalidPacketCount > 0`). Deliberately probabilistic-tolerant: the exact outcome at
   this size varies run to run (this session observed both byte-exact and silently
   truncated), so the test asserts detectability, not a specific outcome. If the plain
   path becomes robust enough to always round-trip, update this test to assert that.
2. **`Phase2_LongStream_DurabilityMatrix_RoundTrips_ByteExact`** (8 KB ≈ 4096 symbols,
   hundreds of parity groups): the durability matrix must round-trip byte-exact with
   `integrity=passed`. Sized for a bounded runtime (~40 s) while still exercising the
   multi-group repair path the 5-frame tests never reached.
3. **`Phase1_LongStream_PlainPath_RoundTrips_ByteExact`** (256 KB, 2703 frames): pins the
   modulator-robustness difference — phase1's binary margin survives long streams on the
   plain path.

## Consequences

- The silent-truncation failure class is now visible in the suite: any regression that
  makes the plain path return wrong bytes _without a metric signal_ fails test 1.
- The durability matrix's long-stream guarantee is pinned by test 2.
- Known limitation documented: the plain path (xor-parity, one loss per group, no
  manifest) is not guaranteed byte-exact for long phase2 streams; `--durability` (CLI,
  F-20260914-01) or the service API is the supported path for large payloads. The
  streaming-decode redesign (F-20260914-02) is orthogonal and does not change this.
- Follow-up worth considering (not done here): surface `TotalDecodedPayloadBytes !=
expected` as a loud CLI error even on the plain path — the CLI knows the input file
  size at decode time and currently prints `payloadRecovered` without comparing.

## Validation

- Focused: 3/3 passed in 2 m 21 s (real ffmpeg, ~10137 + ~1116 + ~15k video frames
  exercised).
- Full suite: 334/334 passed.
