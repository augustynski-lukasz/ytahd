using System;
using System.Diagnostics;

namespace YTAHD.Core.Infrastructure
{
    /// <summary>
    /// Owns a child process for the lifetime of a scope: if the scope ends before the child exited, the
    /// child and its descendants are killed before the handle is released.
    /// </summary>
    /// <remarks>
    /// <see cref="Process.Dispose"/> only releases handles - it does not terminate the child. A child that is
    /// still running when the caller gives up keeps writing into a pipe nobody reads any more, so it blocks
    /// forever on a full pipe buffer and leaks a real ffmpeg process per failed, aborted or cancelled
    /// operation. Killing on release makes the failure and cancellation paths self-cleaning, which in turn
    /// lets the pipe pump unblock (see <see cref="ChildPipeStream"/>).
    /// </remarks>
    internal sealed class ChildProcessScope : IDisposable
    {
        private readonly Process _process;
        private bool _disposed;

        private ChildProcessScope(Process process)
        {
            _process = process;
        }

        internal Process Process => _process;

        /// <summary>
        /// Starts <paramref name="startInfo"/> and takes ownership of the resulting process.
        /// </summary>
        /// <exception cref="InvalidOperationException">The process could not be started.</exception>
        internal static ChildProcessScope Start(ProcessStartInfo startInfo, string failureMessage)
        {
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException(failureMessage);
            return new ChildProcessScope(process);
        }

        /// <summary>
        /// True when the child has already exited on its own, i.e. nothing will be killed on release.
        /// </summary>
        internal bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch
                {
                    return false;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                // Waits for the child to actually be gone, so that a caller which lost the race with a
                // cancellation can immediately reuse or delete the file the child was writing.
                ChildProcessLifetime.KillAndWait(_process);
            }
            finally
            {
                _process.Dispose();
            }
        }
    }
}
