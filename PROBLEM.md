# Phase 3 investigation notes

## Summary

The Phase 3 DCT carrier path is still not valid for a real H.264/libx264 encode-decode round trip, even though the lower-level synthetic and drift-tolerant decoder tests pass. The root issue is not a simple threshold bug: the decoded real-world stream is lossy and the packet/header extraction is still selecting the wrong byte alignment or wrong candidate stream during final reconstruction.

The project does not treat synthetic-only tests as sufficient evidence. The real FFmpeg contract is the ground truth, and the DCT path still fails there.

---

## Observed real-world failure mode

The real decoded stream continues to produce invalid DCT packet candidates during recovery. The investigation repeatedly saw malformed candidate headers such as:

- `4E 53 00 00`
- expected packet prefix pattern: `59 54 01 00` / protocol header pattern for the active frame packet format

This indicates the DCT decoder is still reading from a misaligned offset or selecting the wrong bytes from a lossy RGB decode stream.

The final assembly path also reports:

- `Missing frame index 0`
- `Decoded payload is incomplete`

These two symptoms point to a structural issue in the DCT-frame recovery pipeline, not just a small threshold mismatch.

---

## What was already identified and corrected

### 1) Lossy compression is the main constraint

Real H.264 output is not bit-perfect. Any decoder logic that expects exact values from the raw encoded pixels will fail under compression drift. The project confirmed the need to tolerate:

- drift across luminance values
- duplicated frames
- weak payloads
- imperfect luminance separation
- lossy reconstruction of low-frequency DCT coefficients

### 2) Background carrier value must be neutral, not 128

One important bug was the DCT carrier contract itself: the background / non-carrier area was effectively being left at a neutral value of `128` instead of `0` for the low-frequency carrier path. That invalidated the encoding assumptions and contributed to decoder confusion under real decode.

This was corrected toward a real neutral baseline so the carrier is less likely to collide with the surrounding frame content.

### 3) Missing leading frame in decode recovery was a real bug

The decoder stop condition was incorrectly treating a missing first required frame as a terminal condition too early. The project fixed the missing-leading-frame logic so the recovery policy no longer exits prematurely when a leading frame is absent but later valid payload data still exists.

This was necessary for correct behavior under packet loss or reorder conditions, but it was not sufficient to solve the full Phase 3 issue.

### 4) Packet extraction tolerance needed to be widened

The DCT candidate search and packet parse layer were too strict for real decoded frames. Real H.264 output causes the exact coefficient amplitudes to drift, so the decoder must search a wider set of possible offsets and score candidate packets instead of assuming a single exact alignment.

This was improved, but the wrong-byte-header issue still reproduces under real frame input.

---

## Root cause direction

The remaining Phase 3 problem appears to be a protocol contract mismatch between:

1. the low-frequency DCT carrier layout,
2. the packet offset that the DCT decoder scans,
3. the raw RGB decode path used by `DecoderEngine` / `DecodeStreamOrchestrator`, and
4. the final `DecodedFrameAccumulator` assembly logic.

The core technical suspicion is that the DCT modulator is still placing or extracting payload bytes at the wrong spatial offset, so the packet scanner finds a plausible but invalid header sequence instead of the true protocol frame header.

In other words, the decoder is likely still reading the wrong spatial region or wrong bit ordering under real compression, even though synthetic DCT tests are valid in isolation.

---

## Evidence from the investigation

### Real FFmpeg validation confirmed the gap

The project already has a working real FFmpeg validation flow for the stable modes:

- Phase 1: `BinaryGridModulator`
- Phase 2: `PseudoQamModulator`

These modes pass end-to-end encode/decode under the actual CLI and service paths. The Phase 3 DCT path is the remaining red case.

### The phase-3 probe work repeatedly showed invalid packet accumulation

Temporary probe projects and raw-frame experiments confirmed:

- the decoded real frame stream is not aligned to the synthetic assumptions,
- the payload packet header does not consistently appear at the expected offset,
- a decoder can see plausible but invalid tokens before the true frame header arrives,
- final assembly fails because missing frame index 0 prevents a complete payload from being reconstructed.

### The direct DCT packet tests pass in isolation

Synthetic DCT unit tests still pass, including tests for:

- DCT throughput / capacity calculations,
- basic frame round-trip payload recovery,
- coefficient drift compensation,
- packet-decoder recovery from lossy-like changes.

This is valuable evidence that the DCT encode/decode logic is structurally sound in isolation, but the actual real H.264 decode path is still not crossing the same assumptions.

---

## Current status

### Known good

- Phase 1 and Phase 2 remain valid with real FFmpeg
- CLI guardrails pass for Phase 1 and Phase 2
- DCT unit tests pass in synthetic and drifted scenarios
- the missing-leading-frame decode stop issue was fixed
- the DCT carrier background contract was corrected

### Still failing

- real Phase 3 DCT round trip under real H.264 encode/decode
- final packet extraction/offset alignment under compressed frame output
- complete payload assembly after real-frame recovery

---

## Working hypothesis for the remaining fix

The Phase 3 fix should focus on one root cause at a time:

1. trace the exact emitted DCT carrier layout and packet bytes,
2. confirm the exact spatial coefficient region that survives the H.264 round trip,
3. adjust the DCT frame bit decoder to search and score the correct byte alignment under real decoded frames,
4. then repair the final frame aggregation path so frame 0 is not treated as missing when a valid packet header is still recoverable.

At this point, the investigation shows that the DCT path is not yet production-safe for real compression, and it must stay isolated from the known-good Phase 1/2 pipeline.

---

## Commands and validation notes

The project already enforces the real validation standard:

- `dotnet test YTAHD.Tests/YTAHD.Tests.csproj --logger "console;verbosity=minimal"`
- CLI smoke checks using explicit `--ffmpeg-path` and actual `dotnet run --project ... -- encode/decode`

The Phase 1 and Phase 2 real-world checks are green; the Phase 3 DCT path remains the outstanding problem to continue resolving.
