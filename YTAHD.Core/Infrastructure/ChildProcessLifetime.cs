using System;
using System.Diagnostics;

namespace YTAHD.Core.Infrastructure
{
    /// <summary>
    /// Terminates a child process and waits, briefly and boundedly, for it to actually be gone.
    /// </summary>
    /// <remarks>
    /// <see cref="Process.Kill()"/> only asks the operating system to terminate the child; it returns while the
    /// child is still tearing down and still holds its open handles. Callers that immediately touch a file the
    /// child was writing - the encoder's output video, for example - then race the teardown and can see a
    /// sharing violation. Waiting here makes "the child was released" a fact the caller can rely on rather than
    /// a hope, without letting a wedged child block the caller forever.
    /// </remarks>
    internal static class ChildProcessLifetime
    {
        private const int DefaultTerminationTimeoutMilliseconds = 5000;

        internal static void KillAndWait(Process process, int timeoutMilliseconds = DefaultTerminationTimeoutMilliseconds)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(timeoutMilliseconds);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
            catch (NotSupportedException)
            {
                // Remote or already-detached process; nothing more can be done here.
            }
            catch (SystemException)
            {
                // Access denied or a process that exited mid-kill: the caller is still better off than
                // it would be without the attempt, and the handle is disposed by the caller either way.
            }
        }
    }
}
