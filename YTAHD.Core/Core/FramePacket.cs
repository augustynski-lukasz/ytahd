using System;

namespace YTAHD.Core.Core
{
    public static class FramePacket
    {
        public const int FrameMagic = 0x5954; // 'YT'
        public const byte FrameVersion = 1;
        public const byte FrameTypeData = 0;
        public const byte FrameTypeParity = 1;

        // magic + version + frameType + frameIndex + totalDataFrames + groupStart + groupCount + payloadLen + sha256
        public const int HeaderBytes = 2 + 1 + 1 + 4 + 4 + 4 + 1 + 2 + 32;

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

            return TryParse(new ReadOnlySpan<byte>(packet), out frameType, out frameIndex, out totalDataFrames, out groupStart, out groupCount, out payloadLength, out payload);
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
            frameType = 0;
            frameIndex = 0;
            totalDataFrames = 0;
            groupStart = 0;
            groupCount = 0;
            payloadLength = 0;
            payload = Array.Empty<byte>();

            if (packet.Length < HeaderBytes)
            {
                return false;
            }

            int magic = (packet[0] << 8) | packet[1];
            byte version = packet[2];
            if (magic != FrameMagic || version != FrameVersion)
            {
                return false;
            }

            frameType = packet[3];
            if (frameType != FrameTypeData && frameType != FrameTypeParity)
            {
                return false;
            }

            frameIndex = (packet[4] << 24) | (packet[5] << 16) | (packet[6] << 8) | packet[7];
            totalDataFrames = (packet[8] << 24) | (packet[9] << 16) | (packet[10] << 8) | packet[11];
            groupStart = (packet[12] << 24) | (packet[13] << 16) | (packet[14] << 8) | packet[15];
            groupCount = packet[16];
            payloadLength = (packet[17] << 8) | packet[18];

            if (totalDataFrames <= 0 || groupStart < 0 || groupCount <= 0 || payloadLength < 0 || payloadLength > packet.Length - HeaderBytes)
            {
                return false;
            }

            payload = packet.Slice(HeaderBytes, payloadLength).ToArray();
            return true;
        }
    }
}
