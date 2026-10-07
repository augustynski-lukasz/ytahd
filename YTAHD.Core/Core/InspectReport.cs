using System;
using System.Collections.Generic;

namespace YTAHD.Core.Core;

/// <summary>
/// Machine-readable stream triage report (RM-13, CR-20261007-02): what an <c>inspect</c>
/// pass learned about a YTAHD-encoded video WITHOUT assembling the payload. Serialized as
/// JSON by the CLI; the schema is documented in docs/ARCHITECTURE.md §7.7.
/// </summary>
public sealed class InspectReport
{
    /// <summary>Input video path as inspected.</summary>
    public string InputVideo { get; set; } = string.Empty;

    /// <summary>Video container geometry as probed by ffprobe (0 when probing failed).</summary>
    public int Width { get; set; }
    public int Height { get; set; }
    public int Fps { get; set; }

    /// <summary>Total video frames in the container (0 when probing failed).</summary>
    public int TotalVideoFrames { get; set; }

    /// <summary>Modulator used to decode the frame packets.</summary>
    public string ModulatorId { get; set; } = string.Empty;

    /// <summary>Frame packets recovered from the video (data + parity + manifest).</summary>
    public int PacketCount { get; set; }
    public int DataPacketCount { get; set; }
    public int ParityPacketCount { get; set; }
    public int ManifestPacketCount { get; set; }

    /// <summary>Frames that produced no valid packet (lossy-drift or loss candidates).</summary>
    public int InvalidPacketCount { get; set; }

    /// <summary>Canonical separator frames seen (phase4 datagram boundaries).</summary>
    public int CanonicalFrameCount { get; set; }

    /// <summary>Total video frames read from the container.</summary>
    public int FramesSeen { get; set; }

    /// <summary>Protocol-declared total data frames (from packet headers; 0 when none seen).</summary>
    public int DeclaredTotalDataFrames { get; set; }

    /// <summary>Distinct data frame indices recovered (holes imply loss).</summary>
    public int DistinctDataFrameIndices { get; set; }

    /// <summary>Data frame indices missing between 0 and the highest recovered index.</summary>
    public IReadOnlyList<int> MissingDataFrameIndices { get; set; } = Array.Empty<int>();

    /// <summary>The reconciled stream manifest, when at least one intact copy was recovered.</summary>
    public StreamManifest? Manifest { get; set; }

    /// <summary>Whether the start and end manifest copies agree (null when fewer than two copies).</summary>
    public bool? ManifestCopiesAgree { get; set; }

    /// <summary>Per-frame payload capacity implied by the recovered packets (0 when none seen).</summary>
    public int PayloadBytesPerFrame { get; set; }

    /// <summary>True when at least one packet parsed strictly (magic + version + per-frame hash).</summary>
    public bool LooksLikeYtahdStream { get; set; }
}
