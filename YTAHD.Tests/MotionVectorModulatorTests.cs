using System;
using Xunit;
using YTAHD.Core.Modulation;

namespace YTAHD.Tests
{
    public class MotionVectorModulatorTests
    {
        private const int CellSize = MotionTileBasis.CellSize;

        [Fact]
        public void GetPayloadBytesPerFrame_Matches_CellCount_Minus_Header()
        {
            var mod = new MotionVectorModulator();
            const int width = 200;
            const int height = 160;
            const int borderWidth = 20;
            const int headerBytes = 3;

            int usableWidth = width - borderWidth * 2;
            int usableHeight = height - borderWidth * 2;
            int expectedBlocksX = usableWidth / CellSize;
            int expectedBlocksY = usableHeight / CellSize;
            int expected = (expectedBlocksX * expectedBlocksY) - headerBytes;

            var geometry = new ModulatorGeometry(width, height, mod.MacroblockWidth, headerBytes, borderWidth);
            int actual = mod.GetPayloadBytesPerFrame(geometry);

            Assert.True(expected > 0);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void GetPacketBufferLength_Adds_HeaderBytes_To_Payload()
        {
            var mod = new MotionVectorModulator();
            var geometry = new ModulatorGeometry(200, 160, mod.MacroblockWidth, 3, 20);

            Assert.Equal(3 + 9, mod.GetPacketBufferLength(geometry, 9));
        }

        [Fact]
        public void GetBorderWidth_Matches_Phase3_Contract()
        {
            var mod = new MotionVectorModulator();
            Assert.Equal(32, mod.GetBorderWidth(new ModulatorGeometry(200, 160, mod.MacroblockWidth, 0)));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(17)]
        [InlineData(128)]
        [InlineData(200)]
        [InlineData(255)]
        public void SingleCell_RoundTrips_BytePayload(byte value)
        {
            var mod = new MotionVectorModulator();
            var cellBuffer = new byte[CellSize * CellSize];
            var output = new byte[1];

            mod.Encode(new[] { value }, cellBuffer);
            mod.Decode(cellBuffer, output);

            Assert.Equal(value, output[0]);
        }

        [Fact]
        public void Encode_EmptyInput_Renders_CanonicalHomeTile()
        {
            var mod = new MotionVectorModulator();
            var canonicalBuffer = new byte[CellSize * CellSize];
            var homeBuffer = new byte[CellSize * CellSize];

            mod.Encode(ReadOnlySpan<byte>.Empty, canonicalBuffer);
            mod.Encode(new byte[] { 0 }.AsSpan(0, 0), homeBuffer); // also empty; sanity duplicate path

            Assert.Equal(canonicalBuffer, homeBuffer);
        }

        [Fact]
        public void CreateFrame_RoundTrips_FullCapacityPayload()
        {
            var mod = new MotionVectorModulator();
            const int width = 200;
            const int height = 160;
            const int borderWidth = 20;

            int usableWidth = width - borderWidth * 2;
            int usableHeight = height - borderWidth * 2;
            int blocksX = usableWidth / CellSize;
            int blocksY = usableHeight / CellSize;
            int totalCells = blocksX * blocksY;

            var payload = new byte[totalCells];
            for (int i = 0; i < totalCells; i++) payload[i] = (byte)(i * 37 % 256);

            byte[] frame = mod.CreateFrame(new ModulatorGeometry(width, height, mod.MacroblockWidth, 0, borderWidth), payload);

            var decoded = new byte[totalCells];
            int blockIndex = 0;
            for (int blockY = 0; blockY + CellSize <= height - borderWidth * 2; blockY += CellSize)
            {
                for (int blockX = 0; blockX + CellSize <= width - borderWidth * 2; blockX += CellSize)
                {
                    var cellBuffer = ExtractCellGrayscale(frame, width, borderWidth + blockX, borderWidth + blockY);
                    var outByte = new byte[1];
                    mod.Decode(cellBuffer, outByte);
                    decoded[blockIndex++] = outByte[0];
                }
            }

            Assert.Equal(payload, decoded);
        }

        [Fact]
        public void CreateFrame_EmptyPayload_RendersCanonicalCellsEverywhere()
        {
            var mod = new MotionVectorModulator();
            const int width = 200;
            const int height = 160;
            const int borderWidth = 20;

            byte[] frame = mod.CreateFrame(new ModulatorGeometry(width, height, mod.MacroblockWidth, 0, borderWidth), ReadOnlySpan<byte>.Empty);

            var expectedHomeCell = new byte[CellSize * CellSize];
            mod.Encode(ReadOnlySpan<byte>.Empty, expectedHomeCell);

            int usableWidth = width - borderWidth * 2;
            int usableHeight = height - borderWidth * 2;
            for (int blockY = 0; blockY + CellSize <= usableHeight; blockY += CellSize)
            {
                for (int blockX = 0; blockX + CellSize <= usableWidth; blockX += CellSize)
                {
                    var cellBuffer = ExtractCellGrayscale(frame, width, borderWidth + blockX, borderWidth + blockY);
                    Assert.Equal(expectedHomeCell, cellBuffer);
                }
            }
        }

        private static byte[] ExtractCellGrayscale(byte[] rgbaFrame, int frameWidth, int cellOriginX, int cellOriginY)
        {
            var buffer = new byte[CellSize * CellSize];
            for (int py = 0; py < CellSize; py++)
            {
                for (int px = 0; px < CellSize; px++)
                {
                    int idx = ((cellOriginY + py) * frameWidth + (cellOriginX + px)) * 4;
                    buffer[py * CellSize + px] = rgbaFrame[idx];
                }
            }

            return buffer;
        }
    }
}
