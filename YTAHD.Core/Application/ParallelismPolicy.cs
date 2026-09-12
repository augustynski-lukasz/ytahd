using System;

namespace YTAHD.Core.Application;

/// <summary>
/// Central policy for resolving the effective degree of parallelism of the encode and decode
/// pipelines (see ADR CR-20260912-01-degree-of-parallelism-controls.md).
/// </summary>
/// <remarks>
/// The resolved value is a worker count and is always at least one. A resolved value of
/// <c>1</c> means "use the serial pipeline": callers must not spin up a one-worker parallel
/// pipeline, because that adds thread-pool scheduling noise without any throughput benefit
/// and makes debugging non-deterministic.
/// </remarks>
public static class ParallelismPolicy
{
    /// <summary>
    /// Sentinel requesting automatic worker selection from the machine's logical processor
    /// count. Distinct from <c>0</c>, which is the explicit serial default of
    /// <see cref="VideoCodecOptions.MaxDegreeOfParallelism"/>.
    /// </summary>
    public const int Auto = -1;

    /// <summary>
    /// Conservative worker cap used by <see cref="Auto"/>. This is a starting point, to be
    /// tuned by the F6 performance validation matrix; libx264 already threads internally, so
    /// saturating every core with frame workers tends to starve the codec.
    /// </summary>
    public const int AutoWorkerCap = 4;

    /// <summary>Hard upper clamp applied to any explicitly requested worker count.</summary>
    public const int MaxWorkerLimit = 64;

    /// <summary>
    /// Resolves <paramref name="requested"/> against <see cref="Environment.ProcessorCount"/>.
    /// </summary>
    public static int Resolve(int requested) => Resolve(requested, Environment.ProcessorCount);

    /// <summary>
    /// Resolves a requested degree of parallelism to a worker count for a machine with
    /// <paramref name="logicalProcessorCount"/> logical processors.
    /// </summary>
    /// <param name="requested">
    /// <see cref="Auto"/> to select a conservative count, <c>0</c> or <c>1</c> for the serial
    /// path, or a positive worker count.
    /// </param>
    /// <param name="logicalProcessorCount">
    /// Number of logical processors the machine reports; must be at least one.
    /// </param>
    /// <returns>A worker count of at least one, where <c>1</c> means the serial path.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="logicalProcessorCount"/> is less than one, or when
    /// <paramref name="requested"/> is negative and is not <see cref="Auto"/>.
    /// </exception>
    public static int Resolve(int requested, int logicalProcessorCount)
    {
        if (logicalProcessorCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(logicalProcessorCount),
                logicalProcessorCount,
                "Logical processor count must be at least one.");
        }

        if (requested == Auto)
        {
            // Leave one processor of headroom: the FFmpeg/libx264 side of the pipeline is
            // already multi-threaded. Machines with one or two cores fall back to serial.
            return Math.Max(1, Math.Min(AutoWorkerCap, logicalProcessorCount - 1));
        }

        if (requested < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requested),
                requested,
                $"Degree of parallelism must be non-negative, or ParallelismPolicy.Auto ({Auto}).");
        }

        if (requested <= 1)
        {
            return 1;
        }

        return Math.Min(requested, MaxWorkerLimit);
    }

    /// <summary>
    /// Resolves the inner-loop worker count for modulators and frame bit decoders against
    /// <see cref="Environment.ProcessorCount"/>.
    /// </summary>
    public static int ResolveInnerDegree(int requested, int frameWorkers)
        => ResolveInnerDegree(requested, frameWorkers, Environment.ProcessorCount);

    /// <summary>
    /// Resolves the inner-loop worker count that a modulators' or frame bit decoder's inner
    /// loop may use, given the frame-level worker count the pipeline actually runs.
    /// </summary>
    /// <remarks>
    /// The resolved worker count is a total CPU budget for the stage, so it is split between
    /// the frame-level workers and the inner loops rather than granted to both: inner degree
    /// is <c>logicalProcessorCount / frameWorkers</c>, clamped to at least one. This keeps the
    /// thread count near the machine's capacity instead of oversubscribing it, which matters
    /// because FFmpeg/libx264 already threads internally.
    /// Values greater than one therefore reduce the inner degree as frame-level parallelism
    /// grows, while the unconfigured default (<c>0</c>, one frame worker) keeps every
    /// processor available to the inner loop — the historical behaviour.
    /// An explicit request of one worker is the deterministic debug mode and forces the inner
    /// loops serial as well, so a run is reproducible under a debugger.
    /// </remarks>
    /// <param name="requested">The degree of parallelism as configured (see <see cref="Resolve(int)"/>).</param>
    /// <param name="frameWorkers">
    /// The number of frame-level workers that will actually be used for this run; must be at
    /// least one. Callers that know the frame count should clamp it to the available work so a
    /// short encode still gets a parallel inner loop.
    /// </param>
    /// <param name="logicalProcessorCount">Number of logical processors the machine reports.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="requested"/> is negative and is not <see cref="Auto"/>, or
    /// when <paramref name="frameWorkers"/> or <paramref name="logicalProcessorCount"/> is
    /// less than one.
    /// </exception>
    public static int ResolveInnerDegree(int requested, int frameWorkers, int logicalProcessorCount)
    {
        if (frameWorkers < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameWorkers),
                frameWorkers,
                "Frame worker count must be at least one.");
        }

        if (logicalProcessorCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(logicalProcessorCount),
                logicalProcessorCount,
                "Logical processor count must be at least one.");
        }

        if (requested < Auto)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requested),
                requested,
                $"Degree of parallelism must be non-negative, or ParallelismPolicy.Auto ({Auto}).");
        }

        if (requested == 1)
        {
            return 1;
        }

        return Math.Max(1, logicalProcessorCount / frameWorkers);
    }
}
