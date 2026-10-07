using System;
using Xunit;
using YTAHD.Cli;
using YTAHD.Core.Application;
using YTAHD.Core.Core;

namespace YTAHD.Tests;

/// <summary>
/// CR-20261003-02 (ROADMAP RM-07/RM-08): the three-tier configuration hierarchy.
/// These tests pin the classification contract — every legal CLI selection maps to exactly
/// one tier (no combination left ambiguous), the invocation tier is the highest exercised
/// tier, and the experimental-warning behavior. The tier model lives in
/// <c>YTAHD.Cli/CliModeHierarchy.cs</c> so the CLI and these tests cannot drift.
/// </summary>
public class CliModeHierarchyTests
{
    [Theory]
    [InlineData("phase1", ConfigurationTier.Reference)]
    [InlineData("binary", ConfigurationTier.Reference)]
    [InlineData("grid", ConfigurationTier.Reference)]
    [InlineData("default", ConfigurationTier.Reference)]
    [InlineData("", ConfigurationTier.Reference)]
    [InlineData("phase2", ConfigurationTier.Validated)]
    [InlineData("pseudo-qam", ConfigurationTier.Validated)]
    [InlineData("qam", ConfigurationTier.Validated)]
    [InlineData("phase3", ConfigurationTier.Validated)]
    [InlineData("dct", ConfigurationTier.Validated)]
    [InlineData("dct-domain", ConfigurationTier.Validated)]
    [InlineData("phase4", ConfigurationTier.Validated)]
    [InlineData("motion", ConfigurationTier.Validated)]
    [InlineData("motion-vector", ConfigurationTier.Validated)]
    [InlineData("PHASE3", ConfigurationTier.Validated)]
    public void Modulator_Tiers_Cover_Every_Alias(string name, ConfigurationTier expected)
    {
        Assert.Equal(expected, CliModeHierarchy.ClassifyModulator(name));
    }

    [Theory]
    [InlineData(VideoEncoder.LibX264, ConfigurationTier.Reference)]
    [InlineData(VideoEncoder.H264Qsv, ConfigurationTier.Validated)]
    [InlineData(VideoEncoder.H264Nvenc, ConfigurationTier.Experimental)]
    [InlineData(VideoEncoder.H264Amf, ConfigurationTier.Experimental)]
    public void VideoEncoder_Tiers_Cover_Every_Value(VideoEncoder encoder, ConfigurationTier expected)
    {
        Assert.Equal(expected, CliModeHierarchy.ClassifyVideoEncoder(encoder));
    }

    [Theory]
    [InlineData(HardwareAcceleration.None, ConfigurationTier.Reference)]
    [InlineData(HardwareAcceleration.Qsv, ConfigurationTier.Experimental)]
    [InlineData(HardwareAcceleration.Cuda, ConfigurationTier.Experimental)]
    [InlineData(HardwareAcceleration.D3D11Va, ConfigurationTier.Experimental)]
    public void Hwaccel_Tiers_Cover_Every_Value(HardwareAcceleration acceleration, ConfigurationTier expected)
    {
        Assert.Equal(expected, CliModeHierarchy.ClassifyHwaccel(acceleration));
    }

    [Fact]
    public void Unsupported_Modulator_Name_Throws()
    {
        Assert.Throws<ArgumentException>(() => CliModeHierarchy.ClassifyModulator("phase5"));
    }

    [Theory]
    [InlineData(ConfigurationTier.Reference, ConfigurationTier.Reference, ConfigurationTier.Validated, ConfigurationTier.Validated)]
    [InlineData(ConfigurationTier.Reference, ConfigurationTier.Reference, ConfigurationTier.Reference, ConfigurationTier.Reference)]
    [InlineData(ConfigurationTier.Reference, ConfigurationTier.Validated, ConfigurationTier.Validated, ConfigurationTier.Validated)]
    [InlineData(ConfigurationTier.Reference, ConfigurationTier.Experimental, ConfigurationTier.Validated, ConfigurationTier.Experimental)]
    [InlineData(ConfigurationTier.Experimental, ConfigurationTier.Validated, ConfigurationTier.Validated, ConfigurationTier.Experimental)]
    [InlineData(ConfigurationTier.Validated, ConfigurationTier.Validated, ConfigurationTier.Experimental, ConfigurationTier.Experimental)]
    public void Invocation_Tier_Is_The_Highest_Exercise_Tier(
        ConfigurationTier modulator,
        ConfigurationTier encoder,
        ConfigurationTier hwaccel,
        ConfigurationTier expected)
    {
        Assert.Equal(expected, CliModeHierarchy.ResolveInvocationTier(modulator, encoder, hwaccel));
    }

    [Fact]
    public void Reference_Configuration_Resolves_To_Reference_Tier()
    {
        // The exact Tier-1 selection endorsed by the ADR: phase1 + libx264 + no hwaccel.
        // (Durability is orthogonal — it is required by the tier but does not change the
        // tier classification.)
        var tier = CliModeHierarchy.ResolveInvocationTier(
            CliModeHierarchy.ClassifyModulator("phase1"),
            CliModeHierarchy.ClassifyVideoEncoder(VideoEncoder.LibX264),
            CliModeHierarchy.ClassifyHwaccel(HardwareAcceleration.None));

        Assert.Equal(ConfigurationTier.Reference, tier);
    }

    [Fact]
    public void Nvenc_Produces_Experimental_Warning()
    {
        var warning = CliModeHierarchy.ExperimentalWarning(VideoEncoder.H264Nvenc, HardwareAcceleration.None);

        Assert.NotNull(warning);
        Assert.Contains("h264_nvenc", warning, StringComparison.Ordinal);
        Assert.Contains("experimental", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Amf_Produces_Experimental_Warning()
    {
        var warning = CliModeHierarchy.ExperimentalWarning(VideoEncoder.H264Amf, HardwareAcceleration.None);

        Assert.NotNull(warning);
        Assert.Contains("h264_amf", warning, StringComparison.Ordinal);
        Assert.Contains("experimental", warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HardwareAcceleration.Qsv)]
    [InlineData(HardwareAcceleration.Cuda)]
    [InlineData(HardwareAcceleration.D3D11Va)]
    public void NonNone_Hwaccel_Produces_Experimental_Warning(HardwareAcceleration acceleration)
    {
        var warning = CliModeHierarchy.ExperimentalWarning(VideoEncoder.LibX264, acceleration);

        Assert.NotNull(warning);
        Assert.Contains("--hwaccel", warning, StringComparison.Ordinal);
        Assert.Contains("experimental", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Baseline_Selection_Produces_No_Warning()
    {
        Assert.Null(CliModeHierarchy.ExperimentalWarning(VideoEncoder.LibX264, HardwareAcceleration.None));
        // QSV encode alone is Tier 2 (validated) — no warning.
        Assert.Null(CliModeHierarchy.ExperimentalWarning(VideoEncoder.H264Qsv, HardwareAcceleration.None));
    }
}
