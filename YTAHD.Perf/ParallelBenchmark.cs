using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using YTAHD.Core.Application;
using YTAHD.Core.Core;
using YTAHD.Core.Infrastructure;
using YTAHD.Core.Modulation;

namespace YTAHD.Perf;

/// <summary>
/// Resource sampling for one benchmark run: pipeline CPU time, peak working set, peak managed
/// heap, and total managed allocations. FFmpeg runs as a child process, so its CPU time is not
/// included here — the metrics-based FFmpeg wait share reported alongside covers that gap.
/// </summary>
internal sealed class ResourceSampler : IDisposable
{
    private readonly Process _process;
    private readonly Timer _timer;
    private readonly TimeSpan _startCpuTime;
    private readonly long _startAllocatedBytes;

    private long _peakWorkingSetBytes;
    private long _peakPrivateBytes;
    private long _peakManagedBytes;
    private int _disposed;

    public ResourceSampler()
    {
        _process = Process.GetCurrentProcess();
        _process.Refresh();

        _startCpuTime = _process.TotalProcessorTime;
        _startAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);

        _peakWorkingSetBytes = _process.WorkingSet64;
        _peakPrivateBytes = _process.PrivateMemorySize64;
        _peakManagedBytes = GC.GetTotalMemory(forceFullCollection: false);

        // 20 ms is frequent enough to catch a burst of resident 4K frames between FFmpeg writes.
        _timer = new Timer(_ => Sample(), state: null, dueTime: 0, period: 20);
    }

    private void Sample()
    {
        try
        {
            _process.Refresh();

            UpdatePeak(ref _peakWorkingSetBytes, _process.WorkingSet64);
            UpdatePeak(ref _peakPrivateBytes, _process.PrivateMemorySize64);
            UpdatePeak(ref _peakManagedBytes, GC.GetTotalMemory(forceFullCollection: false));
        }
        catch (InvalidOperationException)
        {
            // The process handle went away; the last sampled values still describe the run.
        }
    }

    private static void UpdatePeak(ref long target, long candidate)
    {
        long current = Interlocked.Read(ref target);
        while (candidate > current)
        {
            long observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    public ResourceSnapshot Stop()
    {
        _timer.Dispose();
        Sample();

        _process.Refresh();

        return new ResourceSnapshot
        {
            PipelineCpuTime = _process.TotalProcessorTime - _startCpuTime,
            AllocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - _startAllocatedBytes,
            PeakWorkingSetBytes = Interlocked.Read(ref _peakWorkingSetBytes),
            PeakPrivateBytes = Interlocked.Read(ref _peakPrivateBytes),
            PeakManagedBytes = Interlocked.Read(ref _peakManagedBytes)
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _timer.Dispose();
        }
    }
}

internal sealed class ResourceSnapshot
{
    public required TimeSpan PipelineCpuTime { get; init; }
    public required long AllocatedBytes { get; init; }
    public required long PeakWorkingSetBytes { get; init; }
    public required long PeakPrivateBytes { get; init; }
    public required long PeakManagedBytes { get; init; }
}

/// <summary>One measured encode/decode round trip for a single (modulator, payload, jobs) cell.</summary>
internal sealed class BenchmarkRunResult
{
    public required string Modulator { get; init; }
    public required int PayloadBytes { get; init; }
    public required int RequestedDegreeOfParallelism { get; init; }
    public required int ResolvedDegreeOfParallelism { get; init; }

    public required double EncodeWallSeconds { get; init; }
    public required double DecodeWallSeconds { get; init; }
    public required double RoundTripWallSeconds { get; init; }

    public required TimeSpan PipelineCpuTime { get; init; }
    public required long PeakWorkingSetBytes { get; init; }
    public required long PeakPrivateBytes { get; init; }
    public required long PeakManagedBytes { get; init; }
    public required long AllocatedBytes { get; init; }

    public required EncodeMetrics EncodeMetrics { get; init; }
    public required DecodeMetrics DecodeMetrics { get; init; }

    public required long OutputVideoBytes { get; init; }
    public required bool PayloadMatches { get; init; }

    /// <summary>Encode throughput over the source payload, in bytes per second of wall time.</summary>
    public double EncodeThroughputBytesPerSecond
        => EncodeWallSeconds > 0 ? PayloadBytes / EncodeWallSeconds : 0d;

    public double DecodeThroughputBytesPerSecond
        => DecodeWallSeconds > 0 ? PayloadBytes / DecodeWallSeconds : 0d;

    /// <summary>
    /// Pipeline CPU time as a percentage of one full machine for the whole round trip. Values
    /// well below 100% mean the run was dominated by waiting (FFmpeg pipes, process startup)
    /// rather than by the managed pipeline.
    /// </summary>
    public double PipelineCpuPercent
        => RoundTripWallSeconds > 0 && Environment.ProcessorCount > 0
            ? (PipelineCpuTime.TotalSeconds / (RoundTripWallSeconds * Environment.ProcessorCount)) * 100d
            : 0d;

    /// <summary>Share of encode wall time spent blocked on FFmpeg stdin writes.</summary>
    public double EncodeFfmpegWaitPercent
        => EncodeWallSeconds > 0 ? (EncodeMetrics.FfmpegWriteMilliseconds / 1000d / EncodeWallSeconds) * 100d : 0d;

    /// <summary>Share of decode wall time spent blocked on FFmpeg stdout reads.</summary>
    public double DecodeFfmpegWaitPercent
        => DecodeWallSeconds > 0 ? (DecodeMetrics.FrameReadMilliseconds / 1000d / DecodeWallSeconds) * 100d : 0d;
}

internal sealed class BenchmarkRunner
{
    private readonly ParallelBenchmarkOptions _options;
    private readonly TextWriter _output;

    public BenchmarkRunner(ParallelBenchmarkOptions options, TextWriter output)
    {
        _options = options;
        _output = output;
    }

    public async Task<IReadOnlyList<BenchmarkRunResult>> RunAsync(CancellationToken cancellationToken = default)
    {
        ValidateFfmpeg();

        var factory = new DefaultFFmpegWrapperFactory(_options.FfmpegPath);
        var results = new List<BenchmarkRunResult>();

        Directory.CreateDirectory(_options.OutputDirectory);

        foreach (var modulatorName in _options.Modulators)
        {
            foreach (var payloadSize in _options.PayloadSizes)
            {
                foreach (var requestedJobs in _options.Jobs)
                {
                    var result = await RunCellAsync(
                        factory,
                        modulatorName,
                        payloadSize,
                        requestedJobs,
                        measured: true,
                        cancellationToken).ConfigureAwait(false);

                    results.Add(result);

                    _output.WriteLine(
                        $"  {result.Modulator,-7} {payloadSize,7} B  jobs={FormatJobs(result):-14} " +
                        $"enc={result.EncodeWallSeconds,7:F2}s  dec={result.DecodeWallSeconds,7:F2}s  " +
                        $"ok={(result.PayloadMatches ? "yes" : "NO")}");
                }
            }
        }

        return results;
    }

    private async Task<BenchmarkRunResult> RunCellAsync(
        IFFmpegWrapperFactory factory,
        string modulatorName,
        int payloadSize,
        int requestedJobs,
        bool measured,
        CancellationToken cancellationToken)
    {
        _ = measured;

        var tag = $"{Guid.NewGuid():N}";
        var inputFile = Path.Combine(_options.OutputDirectory, $"{tag}.bin");
        var outputVideo = Path.Combine(_options.OutputDirectory, $"{tag}.mp4");
        var outputFile = Path.Combine(_options.OutputDirectory, $"{tag}.out");

        try
        {
            var payload = CreatePayload(payloadSize, modulatorName);
            await File.WriteAllBytesAsync(inputFile, payload, cancellationToken).ConfigureAwait(false);

            var modulator = CreateModulator(modulatorName);
            var service = new YtahdCodecService(modulator, factory);
            var resolvedJobs = ParallelismPolicy.Resolve(requestedJobs);

            using var sampler = new ResourceSampler();

            var roundTripStopwatch = Stopwatch.StartNew();

            var encodeStopwatch = Stopwatch.StartNew();
            await service.EncodeAsync(
                new EncodeOptions
                {
                    InputFile = inputFile,
                    OutputVideo = outputVideo,
                    Width = _options.Width,
                    Height = _options.Height,
                    MacroblockSize = _options.MacroblockSize,
                    Fps = _options.Fps,
                    VerifyFfmpeg = _options.VerifyFfmpeg,
                    MaxDegreeOfParallelism = requestedJobs
                },
                cancellationToken).ConfigureAwait(false);
            encodeStopwatch.Stop();

            var decodeStopwatch = Stopwatch.StartNew();
            await service.DecodeAsync(
                new DecodeOptions
                {
                    InputVideo = outputVideo,
                    OutputFile = outputFile,
                    Width = _options.Width,
                    Height = _options.Height,
                    MacroblockSize = _options.MacroblockSize,
                    Fps = _options.Fps,
                    VerifyFfmpeg = _options.VerifyFfmpeg,
                    MaxDegreeOfParallelism = requestedJobs
                },
                cancellationToken).ConfigureAwait(false);
            decodeStopwatch.Stop();

            roundTripStopwatch.Stop();

            var snapshot = sampler.Stop();
            var decoded = await File.ReadAllBytesAsync(outputFile, cancellationToken).ConfigureAwait(false);

            return new BenchmarkRunResult
            {
                Modulator = modulatorName,
                PayloadBytes = payloadSize,
                RequestedDegreeOfParallelism = requestedJobs,
                ResolvedDegreeOfParallelism = resolvedJobs,
                EncodeWallSeconds = encodeStopwatch.Elapsed.TotalSeconds,
                DecodeWallSeconds = decodeStopwatch.Elapsed.TotalSeconds,
                RoundTripWallSeconds = roundTripStopwatch.Elapsed.TotalSeconds,
                PipelineCpuTime = snapshot.PipelineCpuTime,
                PeakWorkingSetBytes = snapshot.PeakWorkingSetBytes,
                PeakPrivateBytes = snapshot.PeakPrivateBytes,
                PeakManagedBytes = snapshot.PeakManagedBytes,
                AllocatedBytes = snapshot.AllocatedBytes,
                EncodeMetrics = service.LastEncodeMetrics,
                DecodeMetrics = service.LastDecodeMetrics,
                OutputVideoBytes = File.Exists(outputVideo) ? new FileInfo(outputVideo).Length : 0,
                PayloadMatches = PayloadEquals(payload, decoded)
            };
        }
        finally
        {
            DeleteQuietly(inputFile);
            DeleteQuietly(outputVideo);
            DeleteQuietly(outputFile);
        }
    }

    private void ValidateFfmpeg()
    {
        var factory = new DefaultFFmpegWrapperFactory(_options.FfmpegPath);
        var wrapper = factory.CreateForEncode(_options.Width, _options.Height, _options.Fps);

        if (!wrapper.IsAvailableAsync().GetAwaiter().GetResult())
        {
            throw new ArgumentException(
                $"ffmpeg was not found at '{_options.FfmpegPath}'. Pass --ffmpeg-path <file-or-directory> " +
                "or put ffmpeg on PATH.");
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Benchmark scratch files are best-effort cleanup only.
        }
    }

    private static byte[] CreatePayload(int size, string modulatorName)
    {
        var payload = new byte[size];
        new Random(7919 + size * 31 + modulatorName.Length).NextBytes(payload);
        return payload;
    }

    private static bool PayloadEquals(byte[] expected, byte[] actual)
    {
        if (expected.Length != actual.Length)
        {
            return false;
        }

        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                return false;
            }
        }

        return true;
    }

    internal static IModulator CreateModulator(string mode)
    {
        return mode.Trim().ToLowerInvariant() switch
        {
            "phase1" => new BinaryGridModulator(),
            "phase2" => new PseudoQamModulator(),
            "phase3" => new DctModulator(),
            "phase4" => new MotionVectorModulator(),
            _ => throw new ArgumentException($"Unsupported modulator '{mode}'. Use phase1, phase2, phase3, or phase4.")
        };
    }

    private static string FormatJobs(BenchmarkRunResult result)
        => result.RequestedDegreeOfParallelism == ParallelismPolicy.Auto
            ? $"auto->{result.ResolvedDegreeOfParallelism}"
            : $"{result.RequestedDegreeOfParallelism}->{result.ResolvedDegreeOfParallelism}";
}

internal static class BenchmarkReportPrinter
{
    public static void PrintHelp()
    {
        Console.WriteLine("YTAHD parallel pipeline benchmark");
        Console.WriteLine();
        Console.WriteLine("Usage: YTAHD.Perf bench [options]");
        Console.WriteLine();
        Console.WriteLine("Runs real FFmpeg encode/decode round trips and reports throughput, CPU, memory,");
        Console.WriteLine("FFmpeg wait share, and payload correctness for each serial/parallel setting.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --ffmpeg-path <file|dir>   ffmpeg.exe or its directory. Defaults to PATH.");
        Console.WriteLine("  --modulator <list>         Comma-separated phase1,phase2,phase3,phase4. Default: phase3.");
        Console.WriteLine("  --payload <list>           Comma-separated payload byte sizes. Default: 256,1024.");
        Console.WriteLine("  --jobs <list>              Comma-separated 'auto', 'serial', or worker counts.");
        Console.WriteLine("                             Default: 0,auto  (sequential frames vs. auto workers).");
        Console.WriteLine("  --width <px>               Frame width. Default: 640.");
        Console.WriteLine("  --height <px>              Frame height. Default: 480.");
        Console.WriteLine("  --fps <n>                  Frame rate. Default: 30.");
        Console.WriteLine("  --macroblock <px>          Macroblock size. Default: 16.");
        Console.WriteLine("  --no-verify-ffmpeg         Skip the FFmpeg availability probe inside the pipeline.");
        Console.WriteLine("  --json <path>              Also write the raw results as JSON.");
        Console.WriteLine("  --keep-output <dir>        Directory for scratch files. Default: a temp directory.");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  YTAHD.Perf bench --modulator phase3,phase4 --payload 256,1024 --jobs 0,1,auto");
        Console.WriteLine("  YTAHD.Perf bench --ffmpeg-path \"D:\\tools\\ffmpeg\\bin\" --json perf.json");
    }

    public static void PrintSummary(
        ParallelBenchmarkOptions options,
        IReadOnlyList<BenchmarkRunResult> results,
        TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("YTAHD parallel pipeline benchmark");
        output.WriteLine("  ffmpeg       : " + options.FfmpegPath);
        output.WriteLine("  geometry     : " + $"{options.Width}x{options.Height}@{options.Fps} MB={options.MacroblockSize}");
        output.WriteLine("  logical CPUs : " + Environment.ProcessorCount);
        output.WriteLine("  .NET version : " + Environment.Version);
        output.WriteLine();

        const string header =
            "modulator  payload     jobs         enc(s)    dec(s)    enc MB/s  dec MB/s  pipeCPU%  encWait%  decWait%  peakWS(MB)  peakGC(MB)  outVideo(KB)  ok";
        output.WriteLine(header);
        output.WriteLine(new string('-', header.Length));

        foreach (var result in results)
        {
            output.WriteLine(string.Join("  ", new[]
            {
                result.Modulator.PadRight(9),
                result.PayloadBytes.ToString(CultureInfo.InvariantCulture).PadLeft(7),
                FormatJobs(result).PadRight(11),
                result.EncodeWallSeconds.ToString("F2", CultureInfo.InvariantCulture).PadLeft(8),
                result.DecodeWallSeconds.ToString("F2", CultureInfo.InvariantCulture).PadLeft(8),
                (result.EncodeThroughputBytesPerSecond / (1024d * 1024d)).ToString("F2", CultureInfo.InvariantCulture).PadLeft(9),
                (result.DecodeThroughputBytesPerSecond / (1024d * 1024d)).ToString("F2", CultureInfo.InvariantCulture).PadLeft(9),
                result.PipelineCpuPercent.ToString("F1", CultureInfo.InvariantCulture).PadLeft(9),
                result.EncodeFfmpegWaitPercent.ToString("F1", CultureInfo.InvariantCulture).PadLeft(9),
                result.DecodeFfmpegWaitPercent.ToString("F1", CultureInfo.InvariantCulture).PadLeft(9),
                (result.PeakWorkingSetBytes / (1024d * 1024d)).ToString("F1", CultureInfo.InvariantCulture).PadLeft(11),
                (result.PeakManagedBytes / (1024d * 1024d)).ToString("F1", CultureInfo.InvariantCulture).PadLeft(11),
                (result.OutputVideoBytes / 1024d).ToString("F1", CultureInfo.InvariantCulture).PadLeft(13),
                result.PayloadMatches ? "yes" : "NO"
            }));
        }

        output.WriteLine();
        output.WriteLine("Legend:");
        output.WriteLine("  pipeCPU%  = managed pipeline CPU time / (wall time x logical CPUs)");
        output.WriteLine("  encWait%  = encode wall time blocked on FFmpeg stdin writes");
        output.WriteLine("  decWait%  = decode wall time blocked on FFmpeg stdout reads");
        output.WriteLine("  peakWS    = peak process working set (the memory-pressure safety constraint)");
        output.WriteLine("  peakGC    = peak managed heap size");
        output.WriteLine("  ok        = decoded payload is byte-identical to the source payload");

        var failures = new List<BenchmarkRunResult>();
        foreach (var result in results)
        {
            if (!result.PayloadMatches)
            {
                failures.Add(result);
            }
        }

        output.WriteLine();
        if (failures.Count == 0)
        {
            output.WriteLine($"Correctness: {results.Count}/{results.Count} round trips recovered the payload exactly.");
        }
        else
        {
            output.WriteLine($"Correctness: {results.Count - failures.Count}/{results.Count} round trips matched; {failures.Count} FAILED.");
            foreach (var failure in failures)
            {
                output.WriteLine(
                    $"  FAILED {failure.Modulator} payload={failure.PayloadBytes} jobs={FormatJobs(failure)}");
            }
        }
    }

    private static string FormatJobs(BenchmarkRunResult result)
        => result.RequestedDegreeOfParallelism == ParallelismPolicy.Auto
            ? $"auto->{result.ResolvedDegreeOfParallelism}"
            : $"{result.RequestedDegreeOfParallelism}->{result.ResolvedDegreeOfParallelism}";
}
