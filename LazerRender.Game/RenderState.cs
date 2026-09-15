// Copyright (c) LazerRender contributors. Licensed under the MIT Licence.

using System.Threading;

namespace LazerRender
{
    /// <summary>
    /// Process-wide state shared between the CLI entry point and the renderer. The recorder loop polls
    /// <see cref="CancellationToken"/> so an external supervisor (or Ctrl+C / SIGTERM) can abort a job
    /// cleanly, letting the FFmpeg sink's teardown kill its child process tree.
    /// </summary>
    public static class RenderState
    {
        public static CancellationToken CancellationToken { get; set; } = CancellationToken.None;
    }
}
