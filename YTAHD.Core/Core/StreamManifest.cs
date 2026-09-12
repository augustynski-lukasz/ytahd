using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace YTAHD.Core.Core;

/// <summary>
/// Stream-level manifest (CR-20260912-05 stage 2): describes the whole encoded object so a
/// decode can prove whole-payload integrity instead of trusting per-frame lengths.
/// </summary>
public sealed class StreamManifest
{
    public const byte CurrentVersion = 1;

    public byte Version { get; init; } = CurrentVersion;

    /// <summary>Total payload bytes of the encoded object.</summary>
    public long TotalPayloadBytes { get; init; }

    /// <summary>SHA-256 over the full original payload.</summary>
    public byte[] PayloadSha256 { get; init; } = Array.Empty<byte>();

    /// <summary>Modulator identifier used at encode time (e.g. "phase1").</summary>
    public string ModulatorId { get; init; } = string.Empty;

    public int Width { get; init; }
    public int Height { get; init; }
    public int MacroblockSize { get; init; }
    public int Fps { get; init; }

    /// <summary>Number of data frames (excluding parity and manifest frames).</summary>
    public int DataFrameCount { get; init; }

    /// <summary>Data frames per parity group (0 when the durability matrix is off).</summary>
    public int ParityGroupSize { get; init; }

    /// <summary>Parity symbols per group (0 when the durability matrix is off).</summary>
    public int ParitySymbolsPerGroup { get; init; }

    /// <summary>True when the durability matrix produced the frame stream.</summary>
    public bool UseDurabilityMatrix { get; init; }
}

/// <summary>
/// Serialises <see cref="StreamManifest"/> to and from the manifest frame payload. The wire
/// format is a fixed little-endian header followed by the modulator identifier; the payload is
/// additionally protected by the frame header's per-frame SHA-256.
/// </summary>
public static class StreamManifestCodec
{
    private const int FixedBytes =
        sizeof(byte)   // version
        + sizeof(long) // totalPayloadBytes
        + 32           // payloadSha256
        + sizeof(int)  // modulatorIdLength
        + sizeof(int)  // width
        + sizeof(int)  // height
        + sizeof(int)  // macroblockSize
        + sizeof(int)  // fps
        + sizeof(int)  // dataFrameCount
        + sizeof(int)  // parityGroupSize
        + sizeof(int)  // paritySymbolsPerGroup
        + sizeof(byte);// useDurabilityMatrix

    public static byte[] Serialize(StreamManifest manifest)
    {
        if (manifest is null)
        {
            throw new ArgumentNullException(nameof(manifest));
        }

        if (manifest.PayloadSha256 is null || manifest.PayloadSha256.Length != 32)
        {
            throw new ArgumentException("PayloadSha256 must be a 32-byte SHA-256 digest.", nameof(manifest));
        }

        var modulatorIdBytes = Encoding.UTF8.GetBytes(manifest.ModulatorId ?? string.Empty);
        if (modulatorIdBytes.Length > ushort.MaxValue)
        {
            throw new ArgumentException("ModulatorId is too long.", nameof(manifest));
        }

        var payload = new byte[FixedBytes + modulatorIdBytes.Length];
        int offset = 0;

        payload[offset++] = manifest.Version;
        WriteInt64(payload, ref offset, manifest.TotalPayloadBytes);
        Buffer.BlockCopy(manifest.PayloadSha256, 0, payload, offset, 32);
        offset += 32;
        WriteInt32(payload, ref offset, modulatorIdBytes.Length);
        Buffer.BlockCopy(modulatorIdBytes, 0, payload, offset, modulatorIdBytes.Length);
        offset += modulatorIdBytes.Length;
        WriteInt32(payload, ref offset, manifest.Width);
        WriteInt32(payload, ref offset, manifest.Height);
        WriteInt32(payload, ref offset, manifest.MacroblockSize);
        WriteInt32(payload, ref offset, manifest.Fps);
        WriteInt32(payload, ref offset, manifest.DataFrameCount);
        WriteInt32(payload, ref offset, manifest.ParityGroupSize);
        WriteInt32(payload, ref offset, manifest.ParitySymbolsPerGroup);
        payload[offset] = manifest.UseDurabilityMatrix ? (byte)1 : (byte)0;

        return payload;
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> payload, out StreamManifest? manifest)
    {
        manifest = null;
        if (payload.Length < FixedBytes)
        {
            return false;
        }

        int offset = 0;
        byte version = payload[offset++];
        if (version != StreamManifest.CurrentVersion)
        {
            return false;
        }

        long totalPayloadBytes = ReadInt64(payload, ref offset);
        var payloadSha256 = new byte[32];
        Buffer.BlockCopy(payload.ToArray(), offset, payloadSha256, 0, 32);
        offset += 32;
        int modulatorIdLength = ReadInt32(payload, ref offset);
        if (modulatorIdLength < 0 || modulatorIdLength > payload.Length - offset)
        {
            return false;
        }

        var modulatorId = Encoding.UTF8.GetString(payload.Slice(offset, modulatorIdLength));
        offset += modulatorIdLength;
        int width = ReadInt32(payload, ref offset);
        int height = ReadInt32(payload, ref offset);
        int macroblockSize = ReadInt32(payload, ref offset);
        int fps = ReadInt32(payload, ref offset);
        int dataFrameCount = ReadInt32(payload, ref offset);
        int parityGroupSize = ReadInt32(payload, ref offset);
        int paritySymbolsPerGroup = ReadInt32(payload, ref offset);
        bool useDurabilityMatrix = payload[offset] == 1;

        manifest = new StreamManifest
        {
            Version = version,
            TotalPayloadBytes = totalPayloadBytes,
            PayloadSha256 = payloadSha256,
            ModulatorId = modulatorId,
            Width = width,
            Height = height,
            MacroblockSize = macroblockSize,
            Fps = fps,
            DataFrameCount = dataFrameCount,
            ParityGroupSize = parityGroupSize,
            ParitySymbolsPerGroup = paritySymbolsPerGroup,
            UseDurabilityMatrix = useDurabilityMatrix
        };

        return true;
    }

    private static void WriteInt32(byte[] payload, ref int offset, int value)
    {
        payload[offset++] = (byte)(value & 0xFF);
        payload[offset++] = (byte)((value >> 8) & 0xFF);
        payload[offset++] = (byte)((value >> 16) & 0xFF);
        payload[offset++] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteInt64(byte[] payload, ref int offset, long value)
    {
        for (int i = 0; i < 8; i++)
        {
            payload[offset++] = (byte)((value >> (8 * i)) & 0xFF);
        }
    }

    private static int ReadInt32(ReadOnlySpan<byte> payload, ref int offset)
    {
        int value = payload[offset]
            | (payload[offset + 1] << 8)
            | (payload[offset + 2] << 16)
            | (payload[offset + 3] << 24);
        offset += 4;
        return value;
    }

    private static long ReadInt64(ReadOnlySpan<byte> payload, ref int offset)
    {
        long value = 0;
        for (int i = 0; i < 8; i++)
        {
            value |= (long)payload[offset + i] << (8 * i);
        }

        offset += 8;
        return value;
    }
}
