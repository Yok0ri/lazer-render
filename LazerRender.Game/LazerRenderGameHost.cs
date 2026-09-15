// Copyright (c) LazerRender contributors. Licensed under the MIT Licence.

using System;
using osu.Framework;
using osu.Framework.Platform;
using osu.Framework.Timing;

namespace LazerRender
{
    /// <summary>
    /// Owns the platform desktop <see cref="GameHost"/> used by the recorder.
    ///
    /// The concrete SDL hosts exposed by osu.Framework (<c>LinuxGameHost</c>, <c>WindowsGameHost</c>,
    /// <c>MacOSGameHost</c>) are internal, so the public <see cref="Host.GetSuitableDesktopHost"/>
    /// factory is the supported way to obtain a real, GPU-backed desktop host. The fixed window size
    /// itself is applied by <see cref="LazerRenderGame"/> through <c>GetFrameworkConfigDefaults</c>,
    /// which is read by the host before the window is created.
    /// </summary>
    public sealed class LazerRenderGameHost : IDisposable
    {
        /// <summary>
        /// The underlying desktop host. This drives update/draw on real threads and owns the
        /// GPU surface used for frame capture.
        /// </summary>
        public DesktopGameHost Host { get; }

        /// <summary>
        /// The recorder's manual gameplay clock. Wall-clock/vsync is ignored; each recorded frame
        /// advances this clock by <c>1000 / fps</c> and seeks the gameplay clock to it.
        /// </summary>
        public ManualClock Clock { get; } = new ManualClock { Rate = 1, IsRunning = true };

        public LazerRenderGameHost()
        {
            // Disable vsync at the driver level. osu.Framework already requests an immediate swap
            // (FrameSync.Unlimited maps to renderer.VerticalSync = false → SDL_GL_SetSwapInterval(0)),
            // but Wayland compositors ignore EGL swap-interval 0 and still pace SwapBuffers to their
            // frame callback, capping the draw loop near the refresh rate. Mesa's vblank_mode=0 (AMD/
            // Intel) and NVIDIA's __GL_SYNC_TO_VBLANK=0 bypass that pacing, which is essential for
            // faster-than-realtime rendering of an offscreen FBO.
            Environment.SetEnvironmentVariable(@"vblank_mode", @"0");
            Environment.SetEnvironmentVariable(@"__GL_SYNC_TO_VBLANK", @"0");

            Host = osu.Framework.Host.GetSuitableDesktopHost(@"lazer-render", new HostOptions
            {
                FriendlyGameName = @"LazerRender",
                // No IPC pipe: we must never contend with a live osu!lazer instance.
                IPCPipeName = null,
                // Keep any incidental framework storage next to the executable rather than in $HOME.
                PortableInstallation = true,
            });
        }

        public void Run(LazerRenderGame game) => Host.Run(game);

        public void Dispose() => Host.Dispose();
    }
}
