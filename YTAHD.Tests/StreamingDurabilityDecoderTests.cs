using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Application;
using YTAHD.Core.Core;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests;

/// <summary>
/// RM-10 regression coverage (CR-20261007-04, design F-20260914-02): the streaming
/// durability decoder must produce byte-identical output to the batch decoder while
/// keeping peak memory bounded by the group size, not the payload size.
/// </summary>
public class StreamingDurabilityDecoderTests
{
    private const int Width = 128;
    private const int Height = 64;
    private const int Macroblock = 1;

    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public StreamingDurabilityDecoderTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    private static DurabilityMatrixOptions MatrixOptions() => new()
    {
        SymbolSize = 32,
        GroupSize = 4,
        ParitySymbolsPerGroup = 1
    };

    private static byte[] Payload(int length) =>
        Enumerable.Range(0, length).Select(i => (byte)((i * 13 + 5) % 251)).ToArray();

    private static int modulatorPayloadBytesPerFrame()
    {
        var mod = new BinaryGridModulator(Macroblock, Macroblock);
        var geometry = new ModulatorGeometry(Width, Height, Macroblock, FramePacket.HeaderBytes, BitsPerFrame: 0);
        int borderWidth = mod.GetBorderWidth(geometry);
        geometry = geometry with { BorderWidth = borderWidth };
        return mod.GetPayloadBytesPerFrame(geometry);
    }

    private static async Task<MemoryStream> EncodeRgbAsync(byte[] payload, bool useDurability)
    {
        var tmpIn = Path.GetTempFileName();
        await File.WriteAllBytesAsync(tmpIn, payload);
        try
        {
            var fake = new FakeFFmpegWrapper(Width, Height, 30);
            var encoder = new EncoderEngine(new BinaryGridModulator(), fake, new VideoCodecOptions
            {
                Width = Width,
                Height = Height,
                MacroblockSize = Macroblock,
                Fps = 30,
                UseDurabilityMatrix = useDurability,
                DurabilityMatrixOptions = useDurability ? MatrixOptions() : null
            });
            await encoder.EncodeAsync(tmpIn, "out.mp4");

            var buffer = fake.Process!.Buffer;
            buffer.Position = 0;
            return buffer;
        }
        finally
        {
            File.Delete(tmpIn);
        }
    }

    [Fact]
    public async Task Streaming_Durability_Decode_Matches_Batch_Output_Byte_Exact()
    {
        var payload = Payload(2048);
        var rgb = await EncodeRgbAsync(payload, useDurability: true);
        rgb.Position = 0;

        using var sink = new MemoryStream();
        var orchestrator = new DecodeStreamOrchestrator(
            new BinaryGridModulator(), Width, Height, Macroblock,
            useDurabilityMatrix: true, MatrixOptions());
        var flushed = await orchestrator.ProcessToSinkAsync(rgb, sink, expectedOutputBytes: 0);

        Assert.Equal(payload.Length, flushed);
        Assert.Equal(payload, sink.ToArray());
        Assert.Equal(IntegrityStatus.Passed, orchestrator.LastDecodeMetrics.IntegrityStatus);
        Assert.NotNull(orchestrator.LastDecodeMetrics.Manifest);
        Assert.True(orchestrator.LastDecodeMetrics.Manifest!.PayloadSha256.AsSpan().SequenceEqual(SHA256.HashData(payload)));
    }

    [Fact]
    public async Task Streaming_Decode_Flushes_Incrementally_Not_At_End()
    {
        // The observable streaming property: after consuming only part of the RGB stream the
        // sink already holds the first groups' bytes. Drive the decoder directly so we control
        // how many frames are fed.
        var payload = Payload(4096);
        var rgb = await EncodeRgbAsync(payload, useDurability: true);

        var frameBytes = Width * Height * 3;
        var totalFrames = (int)(rgb.Length / frameBytes);

        long sinkBytesSeenMidStream = -1;
        using var sink = new MemoryStream();
        var streaming = new StreamingDurabilityDecoder(MatrixOptions(), (buffer, offset, count) => sink.Write(buffer, offset, count));

        var modulator = new BinaryGridModulator();
        var geometry = new ModulatorGeometry(Width, Height, Macroblock, FramePacket.HeaderBytes, BitsPerFrame: Width * Height);
        int borderWidth = modulator.GetBorderWidth(geometry with { BitsPerFrame = 0 });

        var frameBuf = new byte[frameBytes];
        var halfFrames = totalFrames / 2;
        for (int f = 0; f < halfFrames; f++)
        {
            rgb.ReadExactly(frameBuf, 0, frameBytes);
            if (!frameBitDecoderIsCanonical(frameBuf))
            {
                if (DecoderEngine.TryReadDecodedPacket(frameBuf, Width, Height, Macroblock, Width * 3, frameBytes, Width * Height, modulator, out var packet))
                {
                    streaming.Add(packet);
                }
            }
        }

        sinkBytesSeenMidStream = streaming.FlushedBytes;
        Assert.True(sinkBytesSeenMidStream > 0, "No bytes were flushed mid-stream — the decode is buffering the whole payload.");
        Assert.True(sinkBytesSeenMidStream < payload.Length, $"Mid-stream flush {sinkBytesSeenMidStream} covered the whole payload {payload.Length} — no incrementality to prove.");

        // Finish the stream and verify byte-exactness end-to-end.
        while (rgb.Position < rgb.Length)
        {
            int read = rgb.Read(frameBuf, 0, frameBytes);
            if (read < frameBytes) break;
            if (!frameBitDecoderIsCanonical(frameBuf))
            {
                if (DecoderEngine.TryReadDecodedPacket(frameBuf, Width, Height, Macroblock, Width * 3, frameBytes, Width * Height, modulator, out var packet))
                {
                    streaming.Add(packet);
                }
            }
        }

        streaming.Finalize(streaming.Manifest, out var integrity, out var missing);
        Assert.Empty(missing);
        Assert.Equal(IntegrityStatus.Passed, integrity);
        Assert.Equal(payload, sink.ToArray());
    }

    [Fact]
    public async Task Streaming_Decode_Reports_Hole_For_Wholly_Missing_Group()
    {
        // Truncate the stream to drop the final group(s): the streaming decoder must zero-fill
        // the hole from the manifest schedule and report integrity=failed (same contract as the
        // batch hole-tolerant decode, CR-20260913-02).
        var payload = Payload(2048);
        var rgb = await EncodeRgbAsync(payload, useDurability: true);

        var frameBytes = Width * Height * 3;
        var totalFrames = (int)(rgb.Length / frameBytes);
        const int physicalFramesPerLogicalFrame = 3; // BinaryGridModulator: no IFrameEmissionStrategy override

        // Strip the final logical group entirely: its data frames + parity frame + the end
        // manifest copy, each written physicalFramesPerLogicalFrame times (same tail-truncation
        // arithmetic as CombinedClockArbitrationTests).
        var payloadBytesPerFrame = modulatorPayloadBytesPerFrame();
        var dataFrameCount = (int)Math.Ceiling(payload.Length / (double)MatrixOptions().SymbolSize);
        var groupCount = (int)Math.Ceiling(dataFrameCount / (double)MatrixOptions().GroupSize);
        var symbolsInLastGroup = dataFrameCount - ((groupCount - 1) * MatrixOptions().GroupSize);
        var dropForOneWholeGroup = (symbolsInLastGroup + 1 /* parity */ + 1 /* end manifest */) * physicalFramesPerLogicalFrame;
        _output.WriteLine($"payloadBytesPerFrame={payloadBytesPerFrame} totalFrames={totalFrames} dataFrameCount={dataFrameCount} groupCount={groupCount} symbolsInLastGroup={symbolsInLastGroup} dropForOneWholeGroup={dropForOneWholeGroup}");

        IntegrityStatus integrity = IntegrityStatus.Unknown;
        IReadOnlyList<int> missing = Array.Empty<int>();

        // Drop enough tail physical frames to swallow exactly the final logical group, then a
        // few extra steps to be robust against codec padding frames.
        foreach (var drop in new[] { dropForOneWholeGroup, dropForOneWholeGroup + physicalFramesPerLogicalFrame, dropForOneWholeGroup + 2 * physicalFramesPerLogicalFrame })
        {
            if (drop >= totalFrames)
            {
                continue;
            }

            using var truncated = new MemoryStream(rgb.ToArray(), 0, (totalFrames - drop) * frameBytes, writable: false);
            using var sink = new MemoryStream();
            var orchestrator = new DecodeStreamOrchestrator(new BinaryGridModulator(), Width, Height, Macroblock, true, MatrixOptions());
            await orchestrator.ProcessToSinkAsync(truncated, sink, 0);
            integrity = orchestrator.LastDecodeMetrics.IntegrityStatus;
            missing = orchestrator.LastDecodeMetrics.MissingDatagramIds;
            _output.WriteLine($"drop={drop} integrity={integrity} missing=[{string.Join(",", missing)}] flushed={sink.Length}");
            if (missing.Count > 0)
            {
                break;
            }
        }

        Assert.True(missing.Count > 0, $"Dropping the final group never produced a hole (last integrity={integrity}).");
        Assert.Equal(IntegrityStatus.Failed, integrity);
    }

    [Fact]
    public void Streaming_Decoder_Fails_Loudly_On_Conflicting_Manifests()
    {
        var options = MatrixOptions();
        var codec = new DurabilityTransportCodec(options);
        var payload = Payload(256);

        var manifestA = new StreamManifest
        {
            TotalPayloadBytes = payload.LongLength,
            PayloadSha256 = SHA256.HashData(payload),
            ModulatorId = "phase1",
            Width = 640,
            Height = 480,
            MacroblockSize = 16,
            Fps = 30,
            DataFrameCount = 8,
            ParityGroupSize = 4,
            ParitySymbolsPerGroup = 1,
            UseDurabilityMatrix = true
        };
        var manifestB = new StreamManifest
        {
            TotalPayloadBytes = payload.LongLength + 1,
            PayloadSha256 = SHA256.HashData(payload),
            ModulatorId = "phase1",
            Width = 640,
            Height = 480,
            MacroblockSize = 16,
            Fps = 30,
            DataFrameCount = 8,
            ParityGroupSize = 4,
            ParitySymbolsPerGroup = 1,
            UseDurabilityMatrix = true
        };

        var packets = codec.EncodeToFramePackets(payload, manifestA);
        var conflictingEndCopy = FramePacketCodec.CreateManifestFramePacket(8, StreamManifestCodec.Serialize(manifestB));

        using var sink = new MemoryStream();
        var streaming = new StreamingDurabilityDecoder(options, (buffer, offset, count) => sink.Write(buffer, offset, count));

        // Feed everything except the trailing manifest copy, then the conflicting copy.
        foreach (var packet in packets.Take(packets.Count - 1))
        {
            streaming.Add(packet);
        }

        Assert.Throws<InvalidDataException>(() => streaming.Add(conflictingEndCopy));
    }

    private bool frameBitDecoderIsCanonical(byte[] frame)
    {
        // BinaryGridModulator has no canonical separator concept; kept as a named helper so the
        // streaming loop mirrors the production pipeline shape.
        return false;
    }
}
