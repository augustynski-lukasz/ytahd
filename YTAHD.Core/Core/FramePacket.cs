using System;

namespace YTAHD.Core.Core
{
    public static class FramePacket
    {
        public const int FrameMagic = 0x5954; // 'YT'
        public const byte LegacyFrameVersion = 1;
        public const byte FrameVersion = 2;
        public const byte FrameTypeData = 0;
        public const byte FrameTypeParity = 1;

        /// <summary>
        /// Stream manifest frame (CR-20260912-05 stage 2): describes the whole encoded object so
        /// decode can prove whole-payload integrity. Emitted redundantly and carried inside the
        /// durability parity groups like a data symbol.
        /// </summary>
        public const byte FrameTypeManifest = 2;

        // v1: magic + version + frameType + frameIndex + totalDataFrames + groupStart + groupCount + payloadLen16 + sha256
        public const int LegacyHeaderBytes = 2 + 1 + 1 + 4 + 4 + 4 + 1 + 2 + 32;

        // v2: magic + version + frameType + frameIndex + totalDataFrames + groupStart + groupCount + payloadLen32 + sha256
        public const int HeaderBytes = 2 + 1 + 1 + 4 + 4 + 4 + 1 + 4 + 32;

        public static bool TryParse(
            byte[] packet,
            out byte frameType,
            out int frameIndex,
            out int totalDataFrames,
            out int groupStart,
            out int groupCount,
            out int payloadLength,
            out byte[] payload)
        {
            if (packet == null)
            {
                frameType = 0;
                frameIndex = 0;
                totalDataFrames = 0;
                groupStart = 0;
                groupCount = 0;
                payloadLength = 0;
                payload = Array.Empty<byte>();
                return false;
            }

            return FramePacketCodec.TryDecode(new ReadOnlySpan<byte>(packet), out frameType, out frameIndex, out totalDataFrames, out groupStart, out groupCount, out payloadLength, out payload);
        }

        public static bool TryParse(
            ReadOnlySpan<byte> packet,
            out byte frameType,
            out int frameIndex,
            out int totalDataFrames,
            out int groupStart,
            out int groupCount,
            out int payloadLength,
            out byte[] payload)
        {
            return FramePacketCodec.TryDecode(packet, out frameType, out frameIndex, out totalDataFrames, out groupStart, out groupCount, out payloadLength, out payload);
        }
    }
}
