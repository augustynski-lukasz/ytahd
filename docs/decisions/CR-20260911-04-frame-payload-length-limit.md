# CR-20260911-04 - Wide Frame Payload Length

**Date:** 2026-09-11 **Status:** Implemented
**Area:** Frame packet protocol, Phase 3 capacity, legacy decode compatibility

## Context

The v1 frame packet header stores `payloadLength` in two bytes, so the maximum representable
payload length is 65,535 bytes. High-capacity modulators such as Phase 3 at 4K can carry more
than that per frame. Encoding more than the header can represent causes the length to wrap on
decode, which truncates each recovered frame payload even though the video still contains the
remaining data bits.

The README documents Phase 3's 4K theoretical capacity at 123,664 bytes per frame. Capping the
encoder to the v1 16-bit limit would preserve correctness but would discard a major reason to
use the DCT-domain carrier.

## Decision

Introduce frame packet protocol v2. New packets use `FramePacket.FrameVersion = 2`, a 53-byte
header, and a 32-bit big-endian `payloadLength` field. This preserves Phase 3's high per-frame
capacity while keeping the packet contract explicit.

Keep decode compatibility for v1 packets with the original 51-byte header and 16-bit length
field. For legacy oversized v1 packets, recover the true payload length by trying wrapped
16-bit length candidates and accepting the one whose SHA-256 hash matches the packet header.

## Consequences

New Phase 3 4K videos can use the documented >100 KB frame capacity without silently
truncating large payloads. Existing v1 videos remain decodable, including oversized v1 videos
when their decoded payload bytes match the stored hash. The v2 header costs two additional
payload bytes per frame compared with v1, which is negligible for high-capacity modulators.
