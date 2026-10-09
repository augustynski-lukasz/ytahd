# RM-12: generate cross-release compatibility fixtures.
#
# For each tagged release, extracts THAT tag's durability Core sources (git show),
# compiles a tiny generator against them, and runs the tag's own packet encoder on
# deterministic payloads. The emitted fixtures (YTAHD.Tests/ReleaseFixtures/<tag>/)
# are committed artifacts: ReleaseCompatibilityTests decodes them with the CURRENT
# code, proving the decoder still reads streams produced by prior releases.
#
# Usage:  powershell -File scripts\generate-release-fixtures.ps1 [-Tags v0.1.1,v0.1.3,v0.2.0]
#
# After an intentional wire-format change, old tags that the new decoder must still
# read stay as-is; a NEW protocol version gets a new tag's fixtures added here in the
# same commit (with the RM-19 spec update + ADR).

param(
    [string[]]$Tags = @('v0.1.1', 'v0.1.3', 'v0.2.0')
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$fixturesRoot = Join-Path $repoRoot 'YTAHD.Tests\ReleaseFixtures'

# The durability/packet Core subset — pure logic, no SkiaSharp/MathNet dependency,
# present under YTAHD.Core/Core/ in every tagged release since v0.1.1.
$coreFiles = @(
    'DurabilityMatrixCodec.cs',
    'DurabilityMatrixEnvelope.cs',
    'DurabilityMatrixOptions.cs',
    'DurabilityMetrics.cs',
    'DurabilityPacket.cs',
    'DurabilityRecoveryPolicy.cs',
    'DurabilityRedundancyMode.cs',
    'DurabilitySymbol.cs',
    'DurabilityTransportCodec.cs',
    'FramePacket.cs',
    'FramePacketCodec.cs',
    'IDataDurabilityCodec.cs',
    'StreamManifest.cs'
)

$generatorCsproj = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="core\**\*.cs" />
    <Compile Include="Program.cs" />
  </ItemGroup>
</Project>
'@

# Per-tag generator programs: each uses ONLY the API surface that tag actually exposed
# (that IS the compatibility surface under test). Written to the per-tag work dir.
$generatorPrograms = @{
    'v0.1.1' = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using YTAHD.Core.Core;

var outDir = args[0];
Directory.CreateDirectory(outDir);

var options = new DurabilityMatrixOptions { SymbolSize = 32, GroupSize = 4, ParitySymbolsPerGroup = 1 };
var codec = new DurabilityTransportCodec(options);

// v1 packets (51-byte header, 16-bit payload length), durability matrix, no manifest.
Emit(options, 1, new[] { ("v1-256b", 256), ("v1-200b-partial-group", 200) },
    len => codec.EncodeToFramePackets(Payload(len)), outDir);

static byte[] Payload(int n) =>
    Enumerable.Range(0, n).Select(i => (byte)((i * 11 + 3) % 251)).ToArray();

static void Emit(
    DurabilityMatrixOptions options,
    byte protocolVersion,
    (string Name, int Length)[] cases,
    Func<int, List<byte[]>> encode,
    string outDir)
{
    foreach (var (name, length) in cases)
    {
        var payload = Payload(length);
        var packets = encode(length);

        File.WriteAllBytes(
            Path.Combine(outDir, name + ".payload.hex"),
            System.Text.Encoding.ASCII.GetBytes(Convert.ToHexString(payload) + Environment.NewLine));
        File.WriteAllText(
            Path.Combine(outDir, name + ".packets.hex"),
            string.Join(Environment.NewLine, packets.Select(p => Convert.ToHexString(p))) + Environment.NewLine);

        var sidecar = new
        {
            description = "YTAHD cross-release fixture (RM-12): encoded by the tagged release's own code, decoded by the current decoder.",
            tag = "v0.1.1",
            protocolVersion,
            symbolSize = options.SymbolSize,
            groupSize = options.GroupSize,
            paritySymbolsPerGroup = options.ParitySymbolsPerGroup,
            payloadBytes = payload.Length,
            payloadSha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            packetCount = packets.Count,
            dataPacketCount = packets.Count(p => p[3] == FramePacket.FrameTypeData),
            parityPacketCount = packets.Count(p => p[3] == FramePacket.FrameTypeParity),
            manifestPacketCount = 0
        };
        File.WriteAllText(
            Path.Combine(outDir, name + ".json"),
            JsonSerializer.Serialize(sidecar, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"v0.1.1/{name}: {packets.Count} packets, {length} bytes");
    }
}
'@;
    'v0.1.3' = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using YTAHD.Core.Core;

var outDir = args[0];
Directory.CreateDirectory(outDir);

var options = new DurabilityMatrixOptions { SymbolSize = 32, GroupSize = 4, ParitySymbolsPerGroup = 1 };
var codec = new DurabilityTransportCodec(options);

// v2 packets (53-byte header, 32-bit payload length), still no manifest.
Emit(options, 2, new[] { ("v2-nomanifest-256b", 256) },
    len => codec.EncodeToFramePackets(Payload(len)), outDir);

static byte[] Payload(int n) =>
    Enumerable.Range(0, n).Select(i => (byte)((i * 11 + 3) % 251)).ToArray();

static void Emit(
    DurabilityMatrixOptions options,
    byte protocolVersion,
    (string Name, int Length)[] cases,
    Func<int, List<byte[]>> encode,
    string outDir)
{
    foreach (var (name, length) in cases)
    {
        var payload = Payload(length);
        var packets = encode(length);

        File.WriteAllBytes(
            Path.Combine(outDir, name + ".payload.hex"),
            System.Text.Encoding.ASCII.GetBytes(Convert.ToHexString(payload) + Environment.NewLine));
        File.WriteAllText(
            Path.Combine(outDir, name + ".packets.hex"),
            string.Join(Environment.NewLine, packets.Select(p => Convert.ToHexString(p))) + Environment.NewLine);

        var sidecar = new
        {
            description = "YTAHD cross-release fixture (RM-12): encoded by the tagged release's own code, decoded by the current decoder.",
            tag = "v0.1.3",
            protocolVersion,
            symbolSize = options.SymbolSize,
            groupSize = options.GroupSize,
            paritySymbolsPerGroup = options.ParitySymbolsPerGroup,
            payloadBytes = payload.Length,
            payloadSha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            packetCount = packets.Count,
            dataPacketCount = packets.Count(p => p[3] == FramePacket.FrameTypeData),
            parityPacketCount = packets.Count(p => p[3] == FramePacket.FrameTypeParity),
            manifestPacketCount = 0
        };
        File.WriteAllText(
            Path.Combine(outDir, name + ".json"),
            JsonSerializer.Serialize(sidecar, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"v0.1.3/{name}: {packets.Count} packets, {length} bytes");
    }
}
'@;
    'v0.2.0' = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using YTAHD.Core.Core;

var outDir = args[0];
Directory.CreateDirectory(outDir);

var options = new DurabilityMatrixOptions { SymbolSize = 32, GroupSize = 4, ParitySymbolsPerGroup = 1 };
var codec = new DurabilityTransportCodec(options);

// v2 packets + StreamManifest (start/end copies) - the release the RM-12 acceptance
// criterion names ("at least the previous tagged release").
Emit(options, 2, new[] { ("v2-manifest-256b", 256) },
    len => codec.EncodeToFramePackets(Payload(len), Manifest(len, options), 0), outDir);
Emit(options, 2, new[] { ("v2-manifest-256b-chunked", 256) },
    len => codec.EncodeToFramePackets(Payload(len), Manifest(len, options), 64), outDir);

static byte[] Payload(int n) =>
    Enumerable.Range(0, n).Select(i => (byte)((i * 11 + 3) % 251)).ToArray();

static StreamManifest Manifest(int len, DurabilityMatrixOptions o) => new()
{
    TotalPayloadBytes = len,
    PayloadSha256 = SHA256.HashData(Payload(len)),
    ModulatorId = "phase1",
    Width = 640,
    Height = 480,
    MacroblockSize = 16,
    Fps = 30,
    DataFrameCount = (int)Math.Ceiling(len / (double)o.SymbolSize),
    ParityGroupSize = o.GroupSize,
    ParitySymbolsPerGroup = o.ParitySymbolsPerGroup,
    UseDurabilityMatrix = true
};

static void Emit(
    DurabilityMatrixOptions options,
    byte protocolVersion,
    (string Name, int Length)[] cases,
    Func<int, List<byte[]>> encode,
    string outDir)
{
    foreach (var (name, length) in cases)
    {
        var payload = Payload(length);
        var packets = encode(length);

        File.WriteAllBytes(
            Path.Combine(outDir, name + ".payload.hex"),
            System.Text.Encoding.ASCII.GetBytes(Convert.ToHexString(payload) + Environment.NewLine));
        File.WriteAllText(
            Path.Combine(outDir, name + ".packets.hex"),
            string.Join(Environment.NewLine, packets.Select(p => Convert.ToHexString(p))) + Environment.NewLine);

        var sidecar = new
        {
            description = "YTAHD cross-release fixture (RM-12): encoded by the tagged release's own code, decoded by the current decoder.",
            tag = "v0.2.0",
            protocolVersion,
            symbolSize = options.SymbolSize,
            groupSize = options.GroupSize,
            paritySymbolsPerGroup = options.ParitySymbolsPerGroup,
            payloadBytes = payload.Length,
            payloadSha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            packetCount = packets.Count,
            dataPacketCount = packets.Count(p => p[3] == FramePacket.FrameTypeData),
            parityPacketCount = packets.Count(p => p[3] == FramePacket.FrameTypeParity),
            manifestPacketCount = packets.Count(p => p[3] == FramePacket.FrameTypeManifest)
        };
        File.WriteAllText(
            Path.Combine(outDir, name + ".json"),
            JsonSerializer.Serialize(sidecar, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"v0.2.0/{name}: {packets.Count} packets, {length} bytes");
    }
}
'@
}

New-Item -ItemType Directory -Force $fixturesRoot | Out-Null

foreach ($tag in $Tags) {
    Write-Host "=== $tag ==="
    $genDir = Join-Path $env:TEMP "ytahd-relfix-$tag"
    $coreDir = Join-Path $genDir 'core'
    $outDir = Join-Path $fixturesRoot $tag

    if (Test-Path $genDir) { Remove-Item -Recurse -Force $genDir }
    New-Item -ItemType Directory -Force $coreDir | Out-Null
    New-Item -ItemType Directory -Force $outDir | Out-Null

    foreach ($f in $coreFiles) {
        $src = "YTAHD.Core/Core/$f"
        $dst = Join-Path $coreDir $f
        $exists = & git ls-tree "${tag}" -- "YTAHD.Core/Core/$f"
        if ([string]::IsNullOrWhiteSpace("$exists")) {
            # Optional file (e.g. StreamManifest.cs predates v0.2.0): absent at this tag.
            Write-Host "  ${tag}: skipping $f (not present at this tag)"
            continue
        }
        $content = git show "${tag}:$src" | Out-String
        [IO.File]::WriteAllText($dst, $content, [Text.UTF8Encoding]::new($false))
    }

    [IO.File]::WriteAllText((Join-Path $genDir 'ReleaseFixtureGenerator.csproj'), $generatorCsproj)
    [IO.File]::WriteAllText((Join-Path $genDir 'Program.cs'), $generatorPrograms[$tag])

    dotnet run --project $genDir -v q -- $outDir
    if ($LASTEXITCODE -ne 0) { throw "generator failed for $tag" }

    Remove-Item -Recurse -Force $genDir
}

Write-Host "Done. Fixtures in $fixturesRoot - review the diff before committing."
