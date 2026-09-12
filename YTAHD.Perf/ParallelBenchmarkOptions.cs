using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using YTAHD.Core.Application;

namespace YTAHD.Perf;

/// <summary>
/// Options for the <c>bench</c> mode: a real-FFmpeg, serial-vs-parallel comparison matrix
/// (see docs/decisions/CR-20260912-02-pipeline-performance-validation.md).
/// </summary>
internal sealed class ParallelBenchmarkOptions
{
    public required string FfmpegPath { get; init; }
    public required IReadOnlyList<string> Modulators { get; init; }
    public required IReadOnlyList<int> PayloadSizes { get; init; }
    public required IReadOnlyList<int> Jobs { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Fps { get; init; }
    public required int MacroblockSize { get; init; }
    public required bool VerifyFfmpeg { get; init; }
    public required string OutputDirectory { get; init; }
    public required string? JsonPath { get; init; }

    public static ParallelBenchmarkOptions Parse(string[] args)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected token '{token}'.");
            }

            string key = token;
            string value = "true";
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[i + 1];
                i++;
            }

            dict[key] = value;
        }

        string ffmpegPath = ParseString(dict, "--ffmpeg-path", ResolveFfmpegFromEnvironment() ?? "ffmpeg");
        var modulators = ParseList(dict, "--modulator", new[] { "phase3" });
        var payloadSizes = ParseIntList(dict, "--payload", new[] { 256, 1024 });
        var jobs = ParseJobsList(dict, "--jobs", new[] { 0, ParallelismPolicy.Auto });
        int width = ParseInt(dict, "--width", 640);
        int height = ParseInt(dict, "--height", 480);
        int fps = ParseInt(dict, "--fps", 30);
        int macroblockSize = ParseInt(dict, "--macroblock", 16);
        bool verifyFfmpeg = !dict.ContainsKey("--no-verify-ffmpeg");
        string? jsonPath = dict.TryGetValue("--json", out var json) ? json : null;
        string outputDirectory = ParseString(
            dict,
            "--keep-output",
            Path.Combine(Path.GetTempPath(), "ytahd-bench-" + Guid.NewGuid().ToString("N")));

        if (string.IsNullOrWhiteSpace(ffmpegPath)) throw new ArgumentException("--ffmpeg-path must not be empty.");
        if (modulators.Count == 0) throw new ArgumentException("--modulator must name at least one modulator.");
        if (payloadSizes.Count == 0) throw new ArgumentException("--payload must contain at least one size.");
        if (jobs.Count == 0) throw new ArgumentException("--jobs must contain at least one setting.");
        if (width <= 0 || height <= 0) throw new ArgumentException("--width and --height must be > 0.");
        if (fps <= 0) throw new ArgumentException("--fps must be > 0.");
        if (macroblockSize <= 0) throw new ArgumentException("--macroblock must be > 0.");

        foreach (var modulator in modulators)
        {
            if (!IsKnownModulator(modulator))
            {
                throw new ArgumentException($"Unsupported modulator '{modulator}'. Use phase1, phase2, phase3, or phase4.");
            }
        }

        return new ParallelBenchmarkOptions
        {
            FfmpegPath = ffmpegPath,
            Modulators = modulators,
            PayloadSizes = payloadSizes,
            Jobs = jobs,
            Width = width,
            Height = height,
            Fps = fps,
            MacroblockSize = macroblockSize,
            VerifyFfmpeg = verifyFfmpeg,
            OutputDirectory = outputDirectory,
            JsonPath = jsonPath
        };
    }

    private static bool IsKnownModulator(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "phase1" or "phase2" or "phase3" or "phase4";
    }

    private static string? ResolveFfmpegFromEnvironment()
    {
        var configured = Environment.GetEnvironmentVariable("YTAHD_FFMPEG_PATH");
        return string.IsNullOrWhiteSpace(configured) ? null : configured;
    }

    private static int ParseInt(Dictionary<string, string> dict, string key, int defaultValue)
    {
        if (!dict.TryGetValue(key, out var value)) return defaultValue;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            throw new ArgumentException($"{key} must be an integer.");
        return parsed;
    }

    private static string ParseString(Dictionary<string, string> dict, string key, string defaultValue)
        => dict.TryGetValue(key, out var value) ? value : defaultValue;

    private static List<string> ParseList(Dictionary<string, string> dict, string key, IEnumerable<string> defaults)
    {
        var raw = ParseString(dict, key, string.Join(',', defaults));
        var items = new List<string>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            items.Add(part);
        }

        return items;
    }

    private static List<int> ParseIntList(Dictionary<string, string> dict, string key, IEnumerable<int> defaults)
    {
        var raw = ParseString(dict, key, string.Join(',', defaults));
        var items = new List<int>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                throw new ArgumentException($"{key} entries must be integers. Got '{part}'.");
            }

            if (parsed <= 0)
            {
                throw new ArgumentException($"{key} entries must be > 0. Got '{part}'.");
            }

            items.Add(parsed);
        }

        return items;
    }

    /// <summary>
    /// Parses the same job vocabulary as the CLI: <c>auto</c>, <c>serial</c>, <c>0</c>, or an
    /// explicit worker count (see ADR CR-20260912-01-degree-of-parallelism-controls.md).
    /// </summary>
    private static List<int> ParseJobsList(Dictionary<string, string> dict, string key, IEnumerable<int> defaults)
    {
        var raw = ParseString(dict, key, string.Join(',', defaults));
        var items = new List<int>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(part, "auto", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(ParallelismPolicy.Auto);
                continue;
            }

            if (string.Equals(part, "serial", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(1);
                continue;
            }

            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
            {
                throw new ArgumentException($"{key} entries must be 'auto', 'serial', or a non-negative worker count. Got '{part}'.");
            }

            items.Add(parsed);
        }

        return items;
    }
}

/// <summary>
/// Minimal JSON writer for benchmark results. Hand-rolled to keep <c>YTAHD.Perf</c> free of
/// extra dependencies; the shape is a flat array of per-run objects plus a machine header.
/// </summary>
internal static class BenchmarkJson
{
    public static string Serialize(ParallelBenchmarkOptions options, IReadOnlyList<BenchmarkRunResult> results)
    {
        var builder = new StringBuilder();
        builder.AppendLine("{");

        builder.Append("  \"ffmpeg\": ").Append(Quote(options.FfmpegPath)).AppendLine(",");
        builder.Append("  \"geometry\": ").Append(Quote($"{options.Width}x{options.Height}@{options.Fps}")).AppendLine(",");
        builder.Append("  \"macroblockSize\": ").Append(options.MacroblockSize).AppendLine(",");
        builder.Append("  \"logicalProcessors\": ").Append(Environment.ProcessorCount).AppendLine(",");
        builder.Append("  \"dotnetVersion\": ").Append(Quote(Environment.Version.ToString())).AppendLine(",");
        builder.AppendLine("  \"runs\": [");

        for (int i = 0; i < results.Count; i++)
        {
            var result = results[i];
            builder.AppendLine("    {");
            builder.Append("      \"modulator\": ").Append(Quote(result.Modulator)).AppendLine(",");
            builder.Append("      \"payloadBytes\": ").Append(result.PayloadBytes).AppendLine(",");
            builder.Append("      \"requestedJobs\": ").Append(result.RequestedDegreeOfParallelism).AppendLine(",");
            builder.Append("      \"resolvedJobs\": ").Append(result.ResolvedDegreeOfParallelism).AppendLine(",");
            builder.Append("      \"encodeSeconds\": ").Append(Number(result.EncodeWallSeconds)).AppendLine(",");
            builder.Append("      \"decodeSeconds\": ").Append(Number(result.DecodeWallSeconds)).AppendLine(",");
            builder.Append("      \"roundTripSeconds\": ").Append(Number(result.RoundTripWallSeconds)).AppendLine(",");
            builder.Append("      \"encodeBytesPerSecond\": ").Append(Number(result.EncodeThroughputBytesPerSecond)).AppendLine(",");
            builder.Append("      \"decodeBytesPerSecond\": ").Append(Number(result.DecodeThroughputBytesPerSecond)).AppendLine(",");
            builder.Append("      \"pipelineCpuSeconds\": ").Append(Number(result.PipelineCpuTime.TotalSeconds)).AppendLine(",");
            builder.Append("      \"pipelineCpuPercent\": ").Append(Number(result.PipelineCpuPercent)).AppendLine(",");
            builder.Append("      \"encodeFfmpegWaitPercent\": ").Append(Number(result.EncodeFfmpegWaitPercent)).AppendLine(",");
            builder.Append("      \"decodeFfmpegWaitPercent\": ").Append(Number(result.DecodeFfmpegWaitPercent)).AppendLine(",");
            builder.Append("      \"peakWorkingSetBytes\": ").Append(result.PeakWorkingSetBytes).AppendLine(",");
            builder.Append("      \"peakPrivateBytes\": ").Append(result.PeakPrivateBytes).AppendLine(",");
            builder.Append("      \"peakManagedBytes\": ").Append(result.PeakManagedBytes).AppendLine(",");
            builder.Append("      \"allocatedBytes\": ").Append(result.AllocatedBytes).AppendLine(",");
            builder.Append("      \"outputVideoBytes\": ").Append(result.OutputVideoBytes).AppendLine(",");
            builder.Append("      \"framesWritten\": ").Append(result.EncodeMetrics.TotalFramesWritten).AppendLine(",");
            builder.Append("      \"framesDecoded\": ").Append(result.DecodeMetrics.TotalFramesDecoded).AppendLine(",");
            builder.Append("      \"payloadMatches\": ").Append(result.PayloadMatches ? "true" : "false").AppendLine();
            builder.Append("    }").AppendLine(i < results.Count - 1 ? "," : string.Empty);
        }

        builder.AppendLine("  ]");
        builder.AppendLine("}");

        return builder.ToString();
    }

    private static string Quote(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    private static string Number(double value)
        => double.IsFinite(value) ? value.ToString("R", CultureInfo.InvariantCulture) : "null";
}
