using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using YTAHD.Core.Core;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests
{
    public sealed class CodecIntegrationTests
    {
        private static string? GetAvailableFfmpegPath() => TestFfmpeg.GetAvailableFfmpegPath();

        [Fact]
        public void BinaryGridFrameBitDecoder_Parses_Valid_Header_From_Real_Libx264_Frame()
        {
            var ffmpegPath = GetAvailableFfmpegPath();
            Assert.False(string.IsNullOrWhiteSpace(ffmpegPath), "ffmpeg must be available for the real codec integration test.");

            var width = 128;
            var height = 64;
            var macroblockSize = 1;
            var mod = new BinaryGridModulator(macroblockSize, macroblockSize);
            var payload = new byte[48];
            new Random(123).NextBytes(payload);

            var packet = FramePacketCodec.CreateDataFramePacket(0, 1, 0, 1, payload.Length, payload, payload.Length);
            var rgbaFrame = mod.CreateFrame(width, height, 0, packet);
            var rgbFrame = FrameProtocolHelpers.ConvertRgbaToRgb(rgbaFrame, width, height);

            var rawPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_codec_integration.rgb");
            var outputPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_codec_integration.mp4");

            try
            {
                File.WriteAllBytes(rawPath, rgbFrame);

                var encodeArgs = $"-y -f rawvideo -pix_fmt rgb24 -s {width}x{height} -r 30 -i \"{rawPath}\" -c:v libx264 -pix_fmt yuv420p -an \"{outputPath}\"";
                using (var encode = Process.Start(new ProcessStartInfo(ffmpegPath, encodeArgs)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }))
                {
                    Assert.NotNull(encode);
                    encode.WaitForExit();
                    Assert.Equal(0, encode.ExitCode);
                }

                var decodeArgs = $"-hide_banner -loglevel error -i \"{outputPath}\" -f rawvideo -pix_fmt rgb24 -s {width}x{height} -r 30 -";
                using var decode = Process.Start(new ProcessStartInfo(ffmpegPath, decodeArgs)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                Assert.NotNull(decode);

                var frameBytes = width * height * 3;
                var decodedRgb = new byte[frameBytes];
                int read = 0;
                while (read < frameBytes)
                {
                    int n = decode.StandardOutput.BaseStream.Read(decodedRgb, read, frameBytes - read);
                    if (n == 0)
                    {
                        break;
                    }

                    read += n;
                }

                decode.WaitForExit();
                Assert.True(read >= frameBytes, "ffmpeg decode did not produce the expected raw RGB frame");

                var decodedPacket = new byte[150];
                var decoder = FrameBitDecoderFactory.CreateForModulator(mod);
                decoder.Decode(decodedRgb, width, height, macroblockSize, width * 3, decodedRgb.Length, decodedPacket, 0);

                Assert.True(FramePacketCodec.TryDecode(decodedPacket, out _, out _, out _, out _, out _, out var payloadLength, out var actualPayload));
                Assert.Equal(payload.Length, payloadLength);
                Assert.Equal(payload.Length, actualPayload.Length);
            }
            finally
            {
                if (File.Exists(rawPath)) File.Delete(rawPath);
                if (File.Exists(outputPath)) File.Delete(outputPath);
            }
        }

        [Fact]
        public void DurabilityTransportCodec_RoundTrips_Through_Real_Libx264_Frames()
        {
            var ffmpegPath = GetAvailableFfmpegPath();
            Assert.False(string.IsNullOrWhiteSpace(ffmpegPath), "ffmpeg must be available for the real durability integration test.");

            var width = 128;
            var height = 64;
            var macroblockSize = 1;
            var modulator = new BinaryGridModulator(macroblockSize, macroblockSize);
            var codec = new DurabilityTransportCodec(new DurabilityMatrixOptions
            {
                SymbolSize = 32,
                GroupSize = 4,
                ParitySymbolsPerGroup = 1
            });

            var payload = Enumerable.Range(0, 48).Select(i => (byte)((i * 13 + 7) % 251)).ToArray();
            var packets = codec.EncodeToFramePackets(payload);
            Assert.NotEmpty(packets);

            var packet = packets[0];
            var frameCapacityBits = (width / macroblockSize) * (height / macroblockSize);
            Assert.True(frameCapacityBits >= packet.Length * 8, $"Real-frame capacity ({frameCapacityBits} bits) is too small for the packet ({packet.Length * 8} bits).");

            var rgbaFrame = modulator.CreateFrame(width, height, 0, packet);
            var rgbFrame = FrameProtocolHelpers.ConvertRgbaToRgb(rgbaFrame, width, height);

            var rawPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_durability_raw.rgb");
            var outputPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_durability_roundtrip.mp4");
            var decodedPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_durability_decoded.rgb");
            var frameBytes = width * height * 3;

            try
            {
                File.WriteAllBytes(rawPath, rgbFrame);

                var encodeArgs = $"-y -f rawvideo -pix_fmt rgb24 -s {width}x{height} -r 30 -i \"{rawPath}\" -c:v libx264 -pix_fmt yuv420p -an \"{outputPath}\"";
                using (var encode = Process.Start(new ProcessStartInfo(ffmpegPath, encodeArgs)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }))
                {
                    Assert.NotNull(encode);
                    encode.WaitForExit();
                    Assert.Equal(0, encode.ExitCode);
                }

                var decodeArgs = $"-hide_banner -loglevel error -i \"{outputPath}\" -f rawvideo -pix_fmt rgb24 -s {width}x{height} -r 30 \"{decodedPath}\"";
                using (var decode = Process.Start(new ProcessStartInfo(ffmpegPath, decodeArgs)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }))
                {
                    Assert.NotNull(decode);
                    decode.WaitForExit();
                    Assert.Equal(0, decode.ExitCode);
                }

                Assert.True(File.Exists(decodedPath), "ffmpeg decode did not produce the RGB output file.");
                var decodedBytes = File.ReadAllBytes(decodedPath);
                Assert.True(decodedBytes.Length >= frameBytes, "ffmpeg decode did not yield enough raw RGB data for the real packet smoke test.");

                var packetDecoder = FrameBitDecoderFactory.CreateForModulator(modulator);
                var packetBuffer = new byte[512];
                var decodedFrame = new byte[frameBytes];
                Buffer.BlockCopy(decodedBytes, 0, decodedFrame, 0, frameBytes);
                packetDecoder.Decode(decodedFrame, width, height, macroblockSize, width * 3, decodedFrame.Length, packetBuffer, 0);

                Assert.True(FramePacketCodec.TryDecodeWithTolerance(packetBuffer, out _, out _, out _, out _, out _, out var payloadLength, out var decodedPacket), "the real H.264 round-trip should retain a recoverable packet header.");
                Assert.NotEmpty(decodedPacket);
                Assert.True(payloadLength > 0);
            }
            finally
            {
                if (File.Exists(rawPath)) File.Delete(rawPath);
                if (File.Exists(outputPath)) File.Delete(outputPath);
                if (File.Exists(decodedPath)) File.Delete(decodedPath);
            }
        }

        [Fact]
        public async Task DecodeStreamOrchestrator_Processes_A_Fake_Rgb_Stream_As_Integration_Boundary()
        {
            var tmpIn = Path.GetTempFileName();
            try
            {
                byte[] data = new byte[16];
                new Random(2).NextBytes(data);
                await File.WriteAllBytesAsync(tmpIn, data);

                var mod = new BinaryGridModulator();
                var fake = new FakeFFmpegWrapper(128, 64, 30);
                var encoder = new EncoderEngine(mod, fake, 1, 128, 64, 30);
                await encoder.EncodeAsync(tmpIn, "out.mp4");

                var buffer = fake.Process?.Buffer;
                Assert.NotNull(buffer);
                buffer.Position = 0;

                var orchestrator = new DecodeStreamOrchestrator(128, 64, 1);
                var output = await orchestrator.ProcessAsync(buffer, data.Length);

                Assert.Equal(data, output);
            }
            finally
            {
                File.Delete(tmpIn);
            }
        }
    }
}
