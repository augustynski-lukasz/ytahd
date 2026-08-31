using System;
using System.Diagnostics;

namespace YTAHD.Core.Infrastructure
{
    internal static class DebugTrace
    {
        private static readonly bool Enabled =
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YTAHD_DEBUG")) ||
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") == "Development" ||
            Debugger.IsAttached;

        public static void Log(string component, string message)
        {
            if (!Enabled)
            {
                return;
            }

            var line = $"[YTAHD:{component}] {message}";
            Console.Error.WriteLine(line);
            Trace.WriteLine(line);
        }
    }
}
