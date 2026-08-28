using System.Globalization;

namespace YTAHD.Perf;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var options = PerfOptions.Parse(args);
            if (options.ShowHelp)
            {
                PerfReportPrinter.PrintHelp();
                return 0;
            }

            var algorithms = options.BuildAlgorithms();
            var calculator = new MetricsCalculator();

            var reports = new List<PerfReport>();
            foreach (var algorithm in algorithms)
            {
                var report = calculator.Calculate(options, algorithm);
                reports.Add(report);
            }

            PerfReportPrinter.PrintSummary(options, reports);
            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine("Use --help for options.");
            return 2;
        }
    }
}

internal sealed class PerfOptions
{
    public required long PayloadBytes { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int MacroblockSize { get; init; }
    public required int Fps { get; init; }
    public required int HeaderBytes { get; init; }
    public required bool CompareAll { get; init; }
    public required string Algorithm { get; init; }
    public required int RepeatCount { get; init; }
    public required int ParityGroupSize { get; init; }
    public required bool ShowHelp { get; init; }

    public static PerfOptions Parse(string[] args)
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

        if (dict.ContainsKey("--help"))
        {
            return new PerfOptions
            {
                PayloadBytes = 0,
                Width = 0,
                Height = 0,
                MacroblockSize = 0,
                Fps = 0,
                HeaderBytes = 0,
                CompareAll = false,
                Algorithm = "repeat",
                RepeatCount = 3,
                ParityGroupSize = 4,
                ShowHelp = true
            };
        }

        long payloadBytes = ParseLong(dict, "--payload-bytes", 10 * 1024 * 1024);
        int width = ParseInt(dict, "--width", 3840);
        int height = ParseInt(dict, "--height", 2160);
        int macroblockSize = ParseInt(dict, "--macroblock", 16);
        int fps = ParseInt(dict, "--fps", 60);
        int headerBytes = ParseInt(dict, "--header-bytes", 51);
        int repeatCount = ParseInt(dict, "--repeat", 3);
        int parityGroupSize = ParseInt(dict, "--parity-group", 4);
        bool compareAll = ParseBool(dict, "--compare", false);
        string algorithm = ParseString(dict, "--algorithm", "xor-parity");

        if (payloadBytes <= 0) throw new ArgumentException("--payload-bytes must be > 0.");
        if (width <= 0 || height <= 0) throw new ArgumentException("--width and --height must be > 0.");
        if (macroblockSize <= 0) throw new ArgumentException("--macroblock must be > 0.");
        if (fps <= 0) throw new ArgumentException("--fps must be > 0.");
        if (headerBytes <= 0) throw new ArgumentException("--header-bytes must be > 0.");
        if (repeatCount <= 0) throw new ArgumentException("--repeat must be > 0.");
        if (parityGroupSize <= 0) throw new ArgumentException("--parity-group must be > 0.");

        return new PerfOptions
        {
            PayloadBytes = payloadBytes,
            Width = width,
            Height = height,
            MacroblockSize = macroblockSize,
            Fps = fps,
            HeaderBytes = headerBytes,
            CompareAll = compareAll,
            Algorithm = algorithm,
            RepeatCount = repeatCount,
            ParityGroupSize = parityGroupSize,
            ShowHelp = false
        };
    }

    public IReadOnlyList<IRedundancyAlgorithm> BuildAlgorithms()
    {
        if (CompareAll)
        {
            return new IRedundancyAlgorithm[]
            {
                new RepeatOnlyAlgorithm(1),
                new RepeatOnlyAlgorithm(RepeatCount),
                new XorParityAlgorithm(RepeatCount, ParityGroupSize)
            };
        }

        return Algorithm.ToLowerInvariant() switch
        {
            "repeat" => new IRedundancyAlgorithm[] { new RepeatOnlyAlgorithm(RepeatCount) },
            "xor-parity" => new IRedundancyAlgorithm[] { new XorParityAlgorithm(RepeatCount, ParityGroupSize) },
            _ => throw new ArgumentException("--algorithm must be one of: repeat, xor-parity")
        };
    }

    private static int ParseInt(Dictionary<string, string> dict, string key, int defaultValue)
    {
        if (!dict.TryGetValue(key, out var value)) return defaultValue;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            throw new ArgumentException($"{key} must be an integer.");
        return parsed;
    }

    private static long ParseLong(Dictionary<string, string> dict, string key, long defaultValue)
    {
        if (!dict.TryGetValue(key, out var value)) return defaultValue;
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            throw new ArgumentException($"{key} must be an integer.");
        return parsed;
    }

    private static bool ParseBool(Dictionary<string, string> dict, string key, bool defaultValue)
    {
        if (!dict.TryGetValue(key, out var value)) return defaultValue;
        if (bool.TryParse(value, out var parsed)) return parsed;
        if (value == "1") return true;
        if (value == "0") return false;
        throw new ArgumentException($"{key} must be true/false.");
    }

    private static string ParseString(Dictionary<string, string> dict, string key, string defaultValue)
    {
        return dict.TryGetValue(key, out var value) ? value : defaultValue;
    }
}

internal interface IRedundancyAlgorithm
{
    string Name { get; }
    int RepeatCount { get; }
    int ParityGroupSize { get; }
    bool UsesParity { get; }
    long CalculateParityFrames(long dataFrames);
}

internal sealed class RepeatOnlyAlgorithm : IRedundancyAlgorithm
{
    public RepeatOnlyAlgorithm(int repeatCount)
    {
        RepeatCount = repeatCount;
    }

    public string Name => $"repeat(x{RepeatCount})";
    public int RepeatCount { get; }
    public int ParityGroupSize => 0;
    public bool UsesParity => false;

    public long CalculateParityFrames(long dataFrames)
    {
        _ = dataFrames;
        return 0;
    }
}

internal sealed class XorParityAlgorithm : IRedundancyAlgorithm
{
    public XorParityAlgorithm(int repeatCount, int parityGroupSize)
    {
        if (repeatCount <= 0) throw new ArgumentException("repeatCount must be > 0");
        if (parityGroupSize <= 0) throw new ArgumentException("parityGroupSize must be > 0");
        RepeatCount = repeatCount;
        ParityGroupSize = parityGroupSize;
    }

    public string Name => $"xor-parity(group:{ParityGroupSize}, repeat:x{RepeatCount})";
    public int RepeatCount { get; }
    public int ParityGroupSize { get; }
    public bool UsesParity => true;

    public long CalculateParityFrames(long dataFrames)
    {
        return (dataFrames + ParityGroupSize - 1) / ParityGroupSize;
    }
}

internal sealed class MetricsCalculator
{
    public PerfReport Calculate(PerfOptions options, IRedundancyAlgorithm algorithm)
    {
        int blocksX = options.Width / options.MacroblockSize;
        int blocksY = options.Height / options.MacroblockSize;
        if (blocksX <= 0 || blocksY <= 0)
            throw new ArgumentException("Invalid geometry. Macroblock is larger than frame dimensions.");

        long bitsPerFrame = (long)blocksX * blocksY;
        int framePacketBytes = (int)(bitsPerFrame / 8);
        int framePayloadNetBytes = framePacketBytes - options.HeaderBytes;
        if (framePayloadNetBytes <= 0)
            throw new ArgumentException("Frame payload net bytes must be > 0. Lower header or increase frame capacity.");

        long dataFrames = (options.PayloadBytes + framePayloadNetBytes - 1) / framePayloadNetBytes;
        long parityFrames = algorithm.CalculateParityFrames(dataFrames);
        long logicalFrames = dataFrames + parityFrames;
        long physicalFrames = logicalFrames * algorithm.RepeatCount;

        int frameVideoBytes = options.Width * options.Height * 3;
        long videoTotalBytes = physicalFrames * frameVideoBytes;

        long packetBytesPerLogicalFrame = options.HeaderBytes + framePayloadNetBytes;
        long packetBytesTotal = logicalFrames * packetBytesPerLogicalFrame;

        long frameHeaderBytesTotal = logicalFrames * options.HeaderBytes;
        long parityOverheadBytesTotal = parityFrames * framePayloadNetBytes;
        long payloadOverheadTotal = packetBytesTotal - options.PayloadBytes;

        double durationSeconds = (double)physicalFrames / options.Fps;
        double videoBandwidthBps = durationSeconds > 0 ? (videoTotalBytes * 8d) / durationSeconds : 0d;
        double dataBandwidthBps = durationSeconds > 0 ? (options.PayloadBytes * 8d) / durationSeconds : 0d;

        return new PerfReport
        {
            Algorithm = algorithm.Name,
            Width = options.Width,
            Height = options.Height,
            MacroblockSize = options.MacroblockSize,
            Fps = options.Fps,
            InitialPayloadBytes = options.PayloadBytes,
            FrameSizeBytes = frameVideoBytes,
            FrameHeaderBytes = options.HeaderBytes,
            PayloadWithOverheadPerFrameBytes = packetBytesPerLogicalFrame,
            PayloadNetPerFrameBytes = framePayloadNetBytes,
            TotalDataFrames = dataFrames,
            TotalParityFrames = parityFrames,
            TotalLogicalFrames = logicalFrames,
            TotalPhysicalFrames = physicalFrames,
            TotalPayloadOverheadBytes = payloadOverheadTotal,
            FrameHeaderOverheadBytes = frameHeaderBytesTotal,
            ParityOverheadBytes = parityOverheadBytesTotal,
            HeaderOverheadPercentVsPayload = Percent(frameHeaderBytesTotal, options.PayloadBytes),
            ParityOverheadPercentVsPayload = Percent(parityOverheadBytesTotal, options.PayloadBytes),
            TotalOverheadPercentVsPayload = Percent(payloadOverheadTotal, options.PayloadBytes),
            AvgNetDataPerPhysicalFrameBytes = (double)options.PayloadBytes / physicalFrames,
            AvgNetDataPerLogicalFrameBytes = (double)options.PayloadBytes / logicalFrames,
            AvgOverheadPerLogicalFrameBytes = (double)payloadOverheadTotal / logicalFrames,
            AvgOverheadPerPhysicalFrameBytes = (double)payloadOverheadTotal / physicalFrames,
            VideoTotalBytes = videoTotalBytes,
            VideoBandwidthBps = videoBandwidthBps,
            DataBandwidthBps = dataBandwidthBps,
            EffectiveDataToVideoPercent = Percent(options.PayloadBytes, videoTotalBytes)
        };
    }

    private static double Percent(double part, double total)
    {
        if (total <= 0) return 0;
        return (part / total) * 100d;
    }
}

internal sealed class PerfReport
{
    public required string Algorithm { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int MacroblockSize { get; init; }
    public required int Fps { get; init; }
    public required long InitialPayloadBytes { get; init; }
    public required int FrameSizeBytes { get; init; }
    public required int FrameHeaderBytes { get; init; }
    public required long PayloadWithOverheadPerFrameBytes { get; init; }
    public required int PayloadNetPerFrameBytes { get; init; }
    public required long TotalDataFrames { get; init; }
    public required long TotalParityFrames { get; init; }
    public required long TotalLogicalFrames { get; init; }
    public required long TotalPhysicalFrames { get; init; }
    public required long TotalPayloadOverheadBytes { get; init; }
    public required long FrameHeaderOverheadBytes { get; init; }
    public required long ParityOverheadBytes { get; init; }
    public required double HeaderOverheadPercentVsPayload { get; init; }
    public required double ParityOverheadPercentVsPayload { get; init; }
    public required double TotalOverheadPercentVsPayload { get; init; }
    public required double AvgNetDataPerPhysicalFrameBytes { get; init; }
    public required double AvgNetDataPerLogicalFrameBytes { get; init; }
    public required double AvgOverheadPerLogicalFrameBytes { get; init; }
    public required double AvgOverheadPerPhysicalFrameBytes { get; init; }
    public required long VideoTotalBytes { get; init; }
    public required double VideoBandwidthBps { get; init; }
    public required double DataBandwidthBps { get; init; }
    public required double EffectiveDataToVideoPercent { get; init; }
}

internal static class PerfReportPrinter
{
    public static void PrintSummary(PerfOptions options, IEnumerable<PerfReport> reports)
    {
        Console.WriteLine("YTAHD Performance Analysis");
        Console.WriteLine($"Geometry: {options.Width}x{options.Height}, macroblock {options.MacroblockSize}, fps {options.Fps}");
        Console.WriteLine($"Requested payload: {FormatBytes(options.PayloadBytes)}");
        Console.WriteLine();

        foreach (var r in reports)
        {
            Console.WriteLine(new string('=', 72));
            Console.WriteLine($"Algorithm: {r.Algorithm}");
            Console.WriteLine(new string('-', 72));
            Console.WriteLine($"Initial payload                     : {FormatBytes(r.InitialPayloadBytes)} ({r.InitialPayloadBytes} B)");
            Console.WriteLine($"Video total size                    : {FormatBytes(r.VideoTotalBytes)} ({r.VideoTotalBytes} B)");
            Console.WriteLine($"Frame size                          : {FormatBytes(r.FrameSizeBytes)} ({r.FrameSizeBytes} B)");
            Console.WriteLine($"Frame header size                   : {r.FrameHeaderBytes} B");
            Console.WriteLine($"Payload with overhead per frame     : {r.PayloadWithOverheadPerFrameBytes} B");
            Console.WriteLine($"Payload netto data per frame        : {r.PayloadNetPerFrameBytes} B");
            Console.WriteLine($"Total data frames                   : {r.TotalDataFrames}");
            Console.WriteLine($"Total parity frames                 : {r.TotalParityFrames}");
            Console.WriteLine($"Total logical frames                : {r.TotalLogicalFrames}");
            Console.WriteLine($"Total physical frames               : {r.TotalPhysicalFrames}");
            Console.WriteLine($"Frame header overhead total         : {FormatBytes(r.FrameHeaderOverheadBytes)} ({r.FrameHeaderOverheadBytes} B)");
            Console.WriteLine($"Parity overhead total               : {FormatBytes(r.ParityOverheadBytes)} ({r.ParityOverheadBytes} B)");
            Console.WriteLine($"Total payload overhead              : {FormatBytes(r.TotalPayloadOverheadBytes)} ({r.TotalPayloadOverheadBytes} B)");
            Console.WriteLine($"Header overhead % vs payload        : {r.HeaderOverheadPercentVsPayload:F3}%");
            Console.WriteLine($"Parity overhead % vs payload        : {r.ParityOverheadPercentVsPayload:F3}%");
            Console.WriteLine($"Total overhead % vs payload         : {r.TotalOverheadPercentVsPayload:F3}%");
            Console.WriteLine($"Avg net data per logical frame      : {r.AvgNetDataPerLogicalFrameBytes:F3} B/frame");
            Console.WriteLine($"Avg net data per physical frame     : {r.AvgNetDataPerPhysicalFrameBytes:F3} B/frame");
            Console.WriteLine($"Avg overhead per logical frame      : {r.AvgOverheadPerLogicalFrameBytes:F3} B/frame");
            Console.WriteLine($"Avg overhead per physical frame     : {r.AvgOverheadPerPhysicalFrameBytes:F3} B/frame");
            Console.WriteLine($"Video bandwidth                     : {FormatBitsPerSecond(r.VideoBandwidthBps)}");
            Console.WriteLine($"Data bandwidth                      : {FormatBitsPerSecond(r.DataBandwidthBps)}");
            Console.WriteLine($"Effective data/video ratio          : {r.EffectiveDataToVideoPercent:F6}%");
            Console.WriteLine();
        }
    }

    public static void PrintHelp()
    {
        Console.WriteLine("YTAHD.Perf options:");
        Console.WriteLine("  --payload-bytes <n>    Input payload size in bytes (default 10485760)");
        Console.WriteLine("  --width <n>            Frame width (default 3840)");
        Console.WriteLine("  --height <n>           Frame height (default 2160)");
        Console.WriteLine("  --macroblock <n>       Macroblock size in pixels (default 16)");
        Console.WriteLine("  --fps <n>              Frame rate (default 60)");
        Console.WriteLine("  --header-bytes <n>     Header bytes per packet/frame (default 51)");
        Console.WriteLine("  --repeat <n>           Physical repeat count per logical frame (default 3)");
        Console.WriteLine("  --parity-group <n>     Data frames per parity frame for xor-parity (default 4)");
        Console.WriteLine("  --algorithm <name>     repeat | xor-parity (default xor-parity)");
        Console.WriteLine("  --compare true|false   Compare repeat(x1), repeat(xN), xor-parity (default false)");
        Console.WriteLine("  --help                 Show this help");
        Console.WriteLine();
        Console.WriteLine("Example:");
        Console.WriteLine("  dotnet run --project YTAHD.Perf -- --payload-bytes 52428800 --compare true");
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
        int idx = 0;
        while (bytes >= 1024 && idx < units.Length - 1)
        {
            bytes /= 1024;
            idx++;
        }

        return $"{bytes:F3} {units[idx]}";
    }

    private static string FormatBitsPerSecond(double bitsPerSecond)
    {
        string[] units = { "bps", "Kbps", "Mbps", "Gbps", "Tbps" };
        int idx = 0;
        while (bitsPerSecond >= 1000 && idx < units.Length - 1)
        {
            bitsPerSecond /= 1000;
            idx++;
        }

        return $"{bitsPerSecond:F3} {units[idx]}";
    }
}
