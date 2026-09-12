# CR-20260912-05 — Integrity Verification Combined With the Durability Matrix

**Date:** 2026-09-12 **Status:** Implemented
**Area:** `FramePacketCodec`, `FramePacket`, `DurabilityTransportCodec`,
`DurabilityMatrixCodec`, `DecodeStreamOrchestrator`, `DecodeMetrics`, `YtahdCodecService`,
`YTAHD.Cli`, `YTAHD.Tests`

## Context

Workstream E (`CR-20260911-05`, `Accepted`) plans strict integrity verification, and the Data
Durability Matrix (`F-20260830-04`, `Implemented`) provides parity-based symbol transport.
Reading them together exposes a three-way gap:

1. **The frame protocol's per-frame SHA-256 is never enforced.** `FramePacketCodec.TryDecode`
   parses the v2 header hash but does not compare it against the payload; only the v1 legacy
   wrapped-length recovery consults the hash. A corrupted payload is accepted whenever the
   header parses.
2. **The durability matrix has no whole-payload proof.** Its decode derives the expected
   length by summing decoded `payloadLength` header fields — untrusted values. A corrupted
   length field silently truncates the output, and a whole missing parity group (data and
   parity together) is undetectable without the optional audio clock.
3. **The README's durability section is stale**, describing the matrix as a standalone
   prototype although ADR `F-20260830-04` records it as service-integrated and real-FFmpeg
   verified.

The two features are complementary, not overlapping: the matrix answers "can I rebuild missing
frames?", integrity verification answers "is what I rebuilt actually the original?". Neither
answers both today.

## Decision

Implement Workstream E in five stages, each one commit with its ADR, and bind the manifest to
the durability matrix rather than running it as a parallel side channel.

**Stage 1 — E1, strict per-frame SHA validation.** `FramePacketCodec.TryDecode` (and
`TryDecodeWithTolerance`) enforce the v2 header hash: a packet whose
`SHA256(payload[0..payloadLength])` does not match the header hash is rejected as invalid, not
accepted with a low score. v1 legacy frames keep their existing hash-gated wrapped-length
recovery. This hardens both decode paths at once, because
`DurabilityTransportCodec.TryDecodeFramePackets` filters packets through `TryDecode`.

**Stage 2 — E2, manifest as a durability symbol.** Add `FramePacket.FrameTypeManifest = 2`
carrying: protocol version, total payload bytes, full payload SHA-256, modulator identifier,
geometry (width, height, macroblock size, fps), data frame count, and parity/durability
settings. The manifest is emitted **inside** the parity groups as ordinary protected symbols
(free single-loss recovery from the existing XOR machinery, zero new math) and additionally
redundantly at stream start and end so at least one copy survives typical loss. The decoder
reconciles multiple manifest copies; conflicting manifests fail loudly.

**Stage 3 — E3, final verification wired into the matrix decode.** After
`DurabilityTransportCodec.TryDecodeFramePackets` succeeds, the orchestrator computes SHA-256
over the assembled payload and compares it with the manifest hash. The manifest's declared
`totalPayloadBytes` replaces the sum-of-`payloadLength` heuristic as the authoritative
expected length, closing the silent-truncation hole. `DecodeMetrics` gains an integrity status
(`passed` / `failed` / `frame-only` for legacy streams without a manifest) and the CLI decode
summary reports it.

**Stage 4 — E4/E5, legacy policy and validation matrix.** Legacy v1 videos without a manifest
remain decodable under strict per-frame hashes and report `integrity=frame-only`. A real-codec
validation matrix covers phase1–phase4 with deliberate frame corruption, missing manifests,
and the existing oversized v1 Phase 3 recovery case.

**Stage 5 — documentation.** Rewrite the README durability section to describe the combined
integrity + durability transport; delete the resolved backlog items.

## Consequences

- Silent corruption becomes detectable at three levels: per-frame (hash), per-stream
  (manifest), and whole-payload (final hash).
- The durability matrix gains an authoritative expected length and a way to detect a wholly
  missing parity group without the audio clock.
- The manifest adds one frame type and a small amount of redundant metadata per stream; group
  geometry is unchanged because the manifest rides inside existing groups.
- Legacy v1 streams keep decoding; they simply cannot prove whole-payload integrity.
- Strict hash enforcement can reject frames that the old quality-score path would have
  accepted; the real-codec validation matrix is the guard that this does not regress payload
  recovery on healthy streams.

## Implementation notes (what actually landed)

- **E1:** `FramePacketCodec.TryDecode` enforces the v2 header hash for data, parity, and
  manifest frames before returning a payload; `TryDecodeWithTolerance` inherits the check.
  v1 keeps its hash-gated wrapped-length recovery. Tests: `FramePacketCodecTests` (5 new).
- **E2:** `FramePacket.FrameTypeManifest = 2`; `StreamManifest` + `StreamManifestCodec`
  (little-endian fixed header + modulator id, 32-byte whole-payload SHA-256, geometry, frame
  counts, parity settings); `DurabilityTransportCodec.EncodeToFramePackets(payload, manifest)`
  emits two intact manifest copies (stream start and end), each protected by its own per-frame
  hash; `TryDecodeFramePackets(..., out manifest)` reconciles identical copies and fails on
  conflicting ones. `PacketQualityScorer.IsFramePacketValid` had to learn the new frame type —
  it silently dropped manifest frames otherwise. Tests: `StreamManifestTests` (7 new).
- **E3:** `DecodeMetrics.IntegrityStatus` (`Passed`/`Failed`/`FrameOnly`/`Unknown`) and
  `DecodeMetrics.Manifest`; `DecodeStreamOrchestrator.VerifyAgainstManifest` runs in both the
  serial and parallel durability branches — a manifest-declared length or whole-payload hash
  mismatch throws and is reported as `Failed`; a legacy stream without a manifest reports
  `FrameOnly`. CLI decode summary gained `integrity=`. Tests: `IntegrityEndToEndTests`
  (3 new, real-codec round trip reports `Passed`).
- **E4/E5:** legacy durability stream without a manifest still decodes and reports
  `FrameOnly`; corrupting a data frame's payload is repaired through parity, while corrupting
  a frame and its parity fails loudly; the real-codec corruption test never silently
  truncates. Tests: `IntegrityLegacyAndCorruptionTests` (4 new).
- Suite: **265/265 passed**, reproduced twice (35 s / 39 s), zero orphaned ffmpeg children;
  no healthy-stream regression from strict hashing.
