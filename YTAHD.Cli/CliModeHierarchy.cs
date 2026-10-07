using System;
using YTAHD.Core.Application;
using YTAHD.Core.Core;

namespace YTAHD.Cli;

/// <summary>
/// The three-tier configuration hierarchy (CR-20261003-02, ROADMAP RM-07/RM-08).
/// A pure classification model: which CLI selections are <see cref="ConfigurationTier.Reference"/>,
/// which are <see cref="ConfigurationTier.Validated"/>, and which are
/// <see cref="ConfigurationTier.Experimental"/>. No behavior gating — the tiers label the
/// surface and drive docs/help text; existing combinations keep working.
/// Tier assignments must cite validation evidence (ADR or test name); a new option without
/// a tier is a docs bug per ARCHITECTURE.md §7.6.
/// </summary>
public static class CliModeHierarchy
{
    /// <summary>
    /// The Tier-1 reference configuration: the exact option selection the project endorses
    /// for correctness-critical use today (binary-margin carrier + CPU baseline + the
    /// durability matrix with manifest verification).
    /// </summary>
    public const string ReferenceModulator = "phase1";

    public static ConfigurationTier ClassifyModulator(string modulatorName)
    {
        return modulatorName.Trim().ToLowerInvariant() switch
        {
            // Tier 1: the reference carrier. Only modulator proven to survive a long stream
            // on the plain path (F-20260914-03: 2703-frame round trip).
            "phase1" or "binary" or "grid" or "default" or "" => ConfigurationTier.Reference,

            // Tier 2: validated carriers with recorded real-codec evidence (full suite +
            // QsvRealCodecTests), but phase2/3 plain-path long-stream behavior is
            // documented-fragile and phase4 has the audio-clock cadence conflict.
            "phase2" or "pseudo-qam" or "qam" => ConfigurationTier.Validated,
            "phase3" or "dct" or "dct-domain" => ConfigurationTier.Validated,
            "phase4" or "motion" or "motion-vector" => ConfigurationTier.Validated,

            _ => throw new ArgumentException($"Unsupported modulation mode '{modulatorName}'.", nameof(modulatorName))
        };
    }

    public static ConfigurationTier ClassifyVideoEncoder(VideoEncoder encoder)
    {
        return encoder switch
        {
            // Tier 1: the CPU baseline every validation run has used since F-20260831-01.
            VideoEncoder.LibX264 => ConfigurationTier.Reference,

            // Tier 2: the only hardware-validated profile (QsvRealCodecTests).
            VideoEncoder.H264Qsv => ConfigurationTier.Validated,

            // Tier 3: declared but never held to the durability bar (capability probe only).
            VideoEncoder.H264Nvenc => ConfigurationTier.Experimental,
            VideoEncoder.H264Amf => ConfigurationTier.Experimental,

            _ => throw new ArgumentOutOfRangeException(nameof(encoder), encoder, "Unknown video encoder.")
        };
    }

    public static ConfigurationTier ClassifyHwaccel(HardwareAcceleration acceleration)
    {
        return acceleration switch
        {
            // 'none' contributes no experimental selection; the neutral baseline is
            // the reference tier. Decode acceleration itself is Tier 3 below.
            HardwareAcceleration.None => ConfigurationTier.Reference,

            // Tier 3: decode-side acceleration is probe-guarded at best; decode-side
            // capability probing is a known gap (Review 1, R2 follow-up).
            HardwareAcceleration.Qsv => ConfigurationTier.Experimental,
            HardwareAcceleration.Cuda => ConfigurationTier.Experimental,
            HardwareAcceleration.D3D11Va => ConfigurationTier.Experimental,

            _ => throw new ArgumentOutOfRangeException(nameof(acceleration), acceleration, "Unknown hardware acceleration.")
        };
    }

    /// <summary>
    /// The tier of a whole invocation: the highest (least-validated) tier exercised by any
    /// selection. An experimental option anywhere demotes the run to experimental.
    /// </summary>
    public static ConfigurationTier ResolveInvocationTier(
        ConfigurationTier modulator,
        ConfigurationTier encoder,
        ConfigurationTier hwaccel)
    {
        return (ConfigurationTier)Math.Max((int)modulator, Math.Max((int)encoder, (int)hwaccel));
    }

    /// <summary>
    /// One-line stderr warning for a Tier-3 selection. Informational only — the run
    /// proceeds (advisory pattern, CR-20260913-05). Returns null for non-experimental tiers.
    /// </summary>
    public static string? ExperimentalWarning(VideoEncoder encoder, HardwareAcceleration hwaccel)
    {
        const string seeAlso = "see docs/decisions/CR-20261003-02-configuration-mode-hierarchy.md";

        return encoder switch
        {
            VideoEncoder.H264Nvenc => $"warning: h264_nvenc is experimental — validated only by capability probing, not by the durability bar ({seeAlso}).",
            VideoEncoder.H264Amf => $"warning: h264_amf is experimental — validated only by capability probing, not by the durability bar ({seeAlso}).",
            _ => hwaccel switch
            {
                HardwareAcceleration.None => null,
                _ => $"warning: --hwaccel {HwaccelName(hwaccel)} is experimental — decode-side acceleration is not validated against the durability bar ({seeAlso})."
            }
        };
    }

    private static string HwaccelName(HardwareAcceleration acceleration) => acceleration switch
    {
        HardwareAcceleration.Qsv => "qsv",
        HardwareAcceleration.Cuda => "cuda",
        HardwareAcceleration.D3D11Va => "d3d11va",
        _ => acceleration.ToString().ToLowerInvariant()
    };
}

/// <summary>Order matters: higher = less validated (see ResolveInvocationTier).</summary>
public enum ConfigurationTier
{
    Reference = 0,
    Validated = 1,
    Experimental = 2
}
