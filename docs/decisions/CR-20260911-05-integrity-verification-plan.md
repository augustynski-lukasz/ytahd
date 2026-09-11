# CR-20260911-05 - Integrity Verification Plan

**Date:** 2026-09-11 **Status:** Accepted
**Area:** Frame packet decoding, stream metadata, decode metrics, CLI output

## Context

The frame packet header already includes a SHA-256 value for each frame payload, but the
decoder path has not treated that hash as a strict acceptance gate. This allowed an oversized
v1 Phase 3 packet to be accepted with a wrapped 16-bit `payloadLength`, producing a truncated
output instead of rejecting the frame. The protocol also lacks a whole-payload hash, so the
CLI cannot independently prove that the final reconstructed file equals the original input.

Per-frame hashes protect individual packets. They do not prove that the final output has all
frames in the right order, has the expected byte length, or has not been silently truncated at
assembly time. A stream-level manifest is needed for that final correctness check.

## Decision

Plan integrity hardening in two layers. First, make per-frame SHA-256 validation strict for
data and parity packets. v2 packets must match the hash for their 32-bit declared payload
length. Legacy v1 packets must match either their declared 16-bit length or, for known
oversized v1 frames, a wrapped length candidate selected only by matching the stored hash.

Second, add a stream manifest packet that records protocol version, total payload bytes,
full payload SHA-256, modulator identity, geometry, frame count, and parity/durability
settings. New decodes should verify the assembled payload hash against this manifest and
report success or fail loudly. Legacy streams without a manifest remain decodable but report
frame-only integrity.

## Consequences

Silent corruption becomes much harder: bad frames are rejected before accumulation, and bad
assembled payloads are rejected after reconstruction. The decoder gains clearer failure modes
and metrics for hash mismatches, manifest conflicts, and final payload verification. The
manifest adds protocol complexity and a small amount of redundant metadata, but it gives the
CLI the correctness proof currently provided only by tests or by manually comparing hashes
outside the tool.