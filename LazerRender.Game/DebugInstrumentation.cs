// Copyright (c) LazerRender contributors. Licensed under the MIT Licence.

using System;
using System.Diagnostics;
using osu.Framework.Logging;

namespace LazerRender
{
    /// <summary>
    /// The engine half of the Phase 8.2 debug/release distinction.
    ///
    /// <see cref="Log(string)"/> is <see cref="ConditionalAttribute">conditional</see> on the
    /// <c>LAZERRENDER_DEBUG</c> constant defined for Debug builds by the repository-root
    /// <c>Directory.Build.props</c>, so its call sites (and the message formatting) are removed from
    /// Release builds entirely.
    ///
    /// <see cref="LogRuntime(string)"/> is for diagnostics that must remain switchable without a
    /// rebuild; it is inert unless the <c>LAZERRENDER_DEBUG=1</c> environment variable is set.
    /// </summary>
    internal static class DebugInstrumentation
    {
        /// <summary>Runtime toggle for diagnostics that cannot be compiled out.</summary>
        public static bool RuntimeEnabled { get; } = Resolve();

        /// <summary>Debug-build-only instrumentation. Compiled out of Release (and kept by -p:LazerRenderDebug=true).</summary>
        [Conditional("LAZERRENDER_DEBUG")]
        public static void Log(string message)
        {
#if LAZERRENDER_DEBUG
            Logger.Log(@$"[debug] {message}");
#endif
        }

        /// <summary>Runtime-switchable diagnostic, inert unless LAZERRENDER_DEBUG=1.</summary>
        public static void LogRuntime(string message)
        {
            if (RuntimeEnabled)
                Logger.Log(@$"[debug] {message}");
        }

        private static bool Resolve()
        {
            string? value = Environment.GetEnvironmentVariable("LAZERRENDER_DEBUG");
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
