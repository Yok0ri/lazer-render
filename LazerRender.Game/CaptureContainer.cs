// Copyright (c) LazerRender contributors. Licensed under the MIT Licence.

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Logging;
using osuTK;
using osuTK.Graphics;
using osuTK.Graphics.ES30;

namespace LazerRender
{
    /// <summary>
    /// A full-screen container which renders its children into a persistent <see cref="IFrameBuffer"/>
    /// (an FBO) and exposes a deterministic, non-swapped readback of that buffer.
    ///
    /// The FBO colour attachment is 8-bit RGBA (verified at runtime via
    /// <c>glGetFramebufferAttachmentParameter</c>), so frames are downloaded with
    /// <c>glReadPixels(GL_UNSIGNED_BYTE)</c> into a five-buffer <c>GL_PIXEL_PACK_BUFFER</c> ring. On
    /// each frame the draw thread maps the buffer that was filled four frames prior (its DMA is
    /// complete by then), copies the 8.3 MB into a pooled byte array, unmaps it, and hands the array
    /// to a background thread that feeds FFmpeg. The GL map/copy/unmap stays entirely on the draw
    /// thread; only the FFmpeg pipe write is off-thread.
    /// </summary>
    public partial class CaptureContainer : Container<Drawable>, IBufferedDrawable
    {
        /// <summary>Number of pixel-pack buffers in the readback ring.</summary>
        private const int pboCount = 5;

        /// <summary>Maximum number of copied frames awaiting handoff to FFmpeg.</summary>
        private const int readbackQueueCapacity = pboCount;

        private readonly BufferedDrawNodeSharedData sharedData;

        private TaskCompletionSource<bool>? pendingCapture;
        private long captureVersion;

        /// <summary>Receives RGBA8 frames (set by the recorder to the FFmpeg sink).</summary>
        private Action<byte[], int, int>? frameConsumer;

        private int[]? pboIds;
        private long pboByteCount;
        private int nextPboWrite;
        private int framesWritten;

        private BlockingCollection<byte[]>? readbackQueue;
        private Task? readbackThread;

        /// <summary>
        /// Paces the recorder loop so it never gets more than <see cref="readbackQueueCapacity"/>
        /// frames ahead of the FFmpeg sink. Waiting here (instead of blocking the draw thread on a
        /// full queue) keeps the recorder loop producing audio and lets it throttle gracefully to
        /// FFmpeg's consumption rate rather than deadlocking the whole pipeline.
        /// </summary>
        private readonly SemaphoreSlim readbackSlots = new SemaphoreSlim(readbackQueueCapacity, readbackQueueCapacity);

        private bool diagnosticLogged;

        /// <summary>The requested output width. The FBO is scaled to match this exactly.</summary>
        public int TargetWidth { get; set; } = 1;

        /// <summary>The requested output height. The FBO is scaled to match this exactly.</summary>
        public int TargetHeight { get; set; } = 1;

        /// <summary>
        /// Diagnostic control: when <c>true</c>, the wrapped scene is not drawn and the FBO contains
        /// only the cleared background colour. Used to isolate the readback/encode cost from the
        /// scene render cost at a given resolution.
        /// </summary>
        public bool FlatFillMode { get; set; }

        private IShader textureShader = null!;

        public CaptureContainer()
        {
            RelativeSizeAxes = Axes.Both;

            sharedData = new BufferedDrawNodeSharedData(
                textureFormat: TexturePixelFormat.R8G8B8A8Float,
                formats: null,
                pixelSnapping: true,
                // Allow the FBO to exceed the physical window so it can render at the target size.
                clipToRootNode: false);
        }

        [BackgroundDependencyLoader]
        private void load(ShaderManager shaders)
        {
            textureShader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, FragmentShaderDescriptor.TEXTURE);
        }

        protected override DrawNode CreateDrawNode()
            => new CaptureDrawNode(this, new CompositeDrawableDrawNode(this), sharedData, onFrameBufferRendered);

        /// <summary>
        /// Starts the asynchronous readback pipeline. Frames are delivered to <paramref name="frameConsumer"/>
        /// from the background handoff thread.
        /// </summary>
        public void StartReadback(Action<byte[], int, int> frameConsumer)
        {
            this.frameConsumer = frameConsumer;
            ensureReadbackPipeline();
        }

        /// <summary>
        /// Requests a capture of the next rendered frame. The returned task completes once the draw
        /// thread has rendered the frame and initiated its asynchronous PBO download.
        /// </summary>
        public async Task CaptureAsync()
        {
            // Wait for a free readback slot so the draw thread never blocks on a full queue. This is
            // what throttles the recorder loop to the FFmpeg sink's consumption rate.
            await readbackSlots.WaitAsync();

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            Interlocked.Exchange(ref pendingCapture, tcs);

            // Bump the draw version so the BufferedDrawNode re-renders its children into the FBO.
            captureVersion++;
            Invalidate(Invalidation.DrawNode);

            await tcs.Task;
        }

        /// <summary>
        /// Completes the readback queue and returns a task that finishes when every in-flight frame
        /// has been handed to the consumer. Call this before stopping the FFmpeg sink so no frames
        /// are dropped when the pipe is closed.
        /// </summary>
        public Task FlushAsync()
        {
            readbackQueue?.CompleteAdding();
            return readbackThread ?? Task.CompletedTask;
        }

        private void onFrameBufferRendered(IRenderer drawRenderer, IFrameBuffer frameBuffer)
        {
            TaskCompletionSource<bool>? tcs = Interlocked.Exchange(ref pendingCapture, null);
            if (tcs == null)
                return;

            int width = frameBuffer.Texture.Width;
            int height = frameBuffer.Texture.Height;
            int byteCount = width * height * 4;

            if (!diagnosticLogged)
            {
                diagnosticLogged = true;
                logFboDiagnostics(drawRenderer, frameBuffer);
            }

            int writePbo = nextPboWrite;
            nextPboWrite = (nextPboWrite + 1) % pboCount;

            // Issue the asynchronous download of the freshly rendered frame into the next PBO.
            // The FBO is 8-bit RGBA, so this is a same-format read (no driver conversion).
            frameBuffer.Bind();
            try
            {
                ensureReadbackBuffers(byteCount);

                GL.BindBuffer(BufferTarget.PixelPackBuffer, pboIds![writePbo]);
                GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
                GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
            }
            finally
            {
                frameBuffer.Unbind();
            }

            // Release the recorder loop as soon as the render + async download are issued. The PBO
            // copy below must never pace (or block) the recorder loop: a slow consumer would
            // otherwise back-pressure the draw thread, which starves the audio stream and deadlocks
            // FFmpeg (which waits on both inputs before muxing).
            tcs?.SetResult(true);

            // Read back the PBO filled four frames ago; its DMA is already complete, so the map
            // returns immediately. For the first few frames there is no older PBO yet, so map the one
            // just written (mapping it will block briefly until its DMA completes — acceptable for
            // the warm-up frames only).
            if (framesWritten >= pboCount - 1)
            {
                int readPbo = (nextPboWrite + 1) % pboCount;
                readbackPbo(readPbo, width, height, byteCount);
            }
            else
            {
                readbackPbo(writePbo, width, height, byteCount);
            }

            framesWritten++;
        }

        /// <summary>Logs the renderer backend and the capture FBO's actual colour attachment format once.</summary>
        private void logFboDiagnostics(IRenderer drawRenderer, IFrameBuffer frameBuffer)
        {
            Logger.Log($@"Capture renderer backend: {drawRenderer.GetType().FullName}");

            try
            {
                PropertyInfo? vsync = drawRenderer.GetType().GetProperty(@"VerticalSync", BindingFlags.NonPublic | BindingFlags.Instance);
                PropertyInfo? tearing = drawRenderer.GetType().GetProperty(@"AllowTearing", BindingFlags.NonPublic | BindingFlags.Instance);

                if (vsync != null || tearing != null)
                    Logger.Log($@"Capture renderer swap state: VerticalSync={vsync?.GetValue(drawRenderer)?.ToString() ?? "?"} AllowTearing={tearing?.GetValue(drawRenderer)?.ToString() ?? "?"}");
            }
            catch (Exception ex)
            {
                Logger.Log($@"Capture renderer swap-state probe failed: {ex.Message}");
            }

            frameBuffer.Bind();
            try
            {
                GL.GetFramebufferAttachmentParameter(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, FramebufferParameterName.FramebufferAttachmentComponentType, out int componentType);
                GL.GetFramebufferAttachmentParameter(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, FramebufferParameterName.FramebufferAttachmentRedSize, out int redSize);
                GL.GetFramebufferAttachmentParameter(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, FramebufferParameterName.FramebufferAttachmentGreenSize, out int greenSize);
                GL.GetFramebufferAttachmentParameter(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, FramebufferParameterName.FramebufferAttachmentBlueSize, out int blueSize);
                GL.GetFramebufferAttachmentParameter(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, FramebufferParameterName.FramebufferAttachmentAlphaSize, out int alphaSize);

                // GL_FLOAT = 0x1406, GL_UNSIGNED_NORMALIZED = 0x8C17.
                Logger.Log($@"Capture FBO colour attachment: componentType=0x{componentType:X4} rgbaBits={redSize}/{greenSize}/{blueSize}/{alphaSize}");
            }
            finally
            {
                frameBuffer.Unbind();
            }
        }

        /// <summary>
        /// Allocates (or re-sizes) the pixel-pack buffer ring. The buffers are persistent, so this runs
        /// only on the first frame or if the FBO size unexpectedly changes.
        /// </summary>
        private void ensureReadbackBuffers(long byteCount)
        {
            if (pboIds == null)
            {
                pboIds = new int[pboCount];
                GL.GenBuffers(pboCount, pboIds);
            }

            if (pboByteCount == byteCount)
                return;

            for (int i = 0; i < pboCount; i++)
            {
                GL.BindBuffer(BufferTarget.PixelPackBuffer, pboIds[i]);
                GL.BufferData(BufferTarget.PixelPackBuffer, new IntPtr(byteCount), IntPtr.Zero, BufferUsageHint.StreamRead);
            }

            GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
            pboByteCount = byteCount;
            framesWritten = 0; // re-sized ring: discard any in-flight layout assumptions.
        }

        /// <summary>
        /// Maps the given PBO, copies its RGBA8 payload into a pooled buffer, and unmaps it — all on
        /// the draw thread, where the GL context is current. The pooled buffer is handed to the
        /// background handoff thread which feeds FFmpeg.
        /// </summary>
        private void readbackPbo(int pbo, int width, int height, int byteCount)
        {
            GL.BindBuffer(BufferTarget.PixelPackBuffer, pboIds![pbo]);
            IntPtr mapped = GL.MapBufferRange(BufferTarget.PixelPackBuffer, IntPtr.Zero, new IntPtr(byteCount), BufferAccessMask.MapReadBit);

            if (mapped == IntPtr.Zero)
            {
                // The map is only refused transiently during warm-up while the driver settles; emit a
                // black frame rather than stalling or corrupting the stream. Steady-state maps do not
                // fail because map/copy/unmap now happen atomically on the draw thread.
                ErrorCode mapError = GL.GetError();
                Logger.Log($@"PBO map failed (pbo={pbo}, {width}x{height}, glError={mapError}); emitting a black frame.");
                GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);

                byte[] fallback = ArrayPool<byte>.Shared.Rent(byteCount);
                Array.Clear(fallback, 0, byteCount);
                readbackQueue!.Add(fallback);
                return;
            }

            byte[] rgba = ArrayPool<byte>.Shared.Rent(byteCount);
            Marshal.Copy(mapped, rgba, 0, byteCount);
            GL.UnmapBuffer(BufferTarget.PixelPackBuffer);
            GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);

            readbackQueue!.Add(rgba);
        }


        private void ensureReadbackPipeline()
        {
            if (readbackThread != null)
                return;

            readbackQueue = new BlockingCollection<byte[]>(readbackQueueCapacity);

            readbackThread = Task.Run(() =>
            {
                try
                {
                    foreach (byte[] rgba in readbackQueue.GetConsumingEnumerable())
                    {
                        // The frame has left the readback queue; release its slot so the recorder loop
                        // can capture another frame (the consumer below may block on the FFmpeg sink).
                        readbackSlots.Release();

                        try
                        {
                            // Ownership of rgba transfers to the consumer (the FFmpeg sink), which
                            // returns it to the pool after writing.
                            frameConsumer!(rgba, TargetWidth, TargetHeight);
                        }
                        catch
                        {
                            ArrayPool<byte>.Shared.Return(rgba);
                            throw;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, @"Frame readback pipeline failed.");
                }
            });
        }

        protected override void Dispose(bool isDisposing)
        {
            readbackQueue?.CompleteAdding();
            base.Dispose(isDisposing);
            sharedData.Dispose();

            // The PBOs are not explicitly deleted here: disposal runs during host teardown where a
            // GL context is not guaranteed to be current, and calling GL.DeleteBuffers from such a
            // thread causes a native crash. The context is destroyed immediately afterwards, which
            // releases the buffers with the rest of the GL state.
            pboIds = null;
        }

        // IBufferedDrawable / ITexturedShaderDrawable

        Color4 IBufferedDrawable.BackgroundColour => new Color4(0, 0, 0, 1);
        DrawColourInfo? IBufferedDrawable.FrameBufferDrawColour => new DrawColourInfo(Color4.White);
        Vector2 IBufferedDrawable.FrameBufferScale
        {
            get
            {
                float width = ScreenSpaceDrawQuad.Width;
                float height = ScreenSpaceDrawQuad.Height;

                if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0)
                    return Vector2.One;

                // Nudge below the exact ratio so the renderer's ceil/round of the FBO size lands
                // exactly on the target instead of one pixel over (e.g. 1921 instead of 1920).
                return new Vector2(
                    MathF.BitDecrement(TargetWidth / width),
                    MathF.BitDecrement(TargetHeight / height));
            }
        }
        IShader ITexturedShaderDrawable.TextureShader => textureShader;

        public override DrawColourInfo DrawColourInfo => new DrawColourInfo(Color4.White);

        private class CaptureDrawNode : BufferedDrawNode, ICompositeDrawNode
        {
            private readonly Action<IRenderer, IFrameBuffer> onRendered;

            private new CaptureContainer Source => (CaptureContainer)base.Source;

            public CaptureDrawNode(CaptureContainer source, CompositeDrawableDrawNode child, BufferedDrawNodeSharedData sharedData, Action<IRenderer, IFrameBuffer> onRendered)
                : base(source, child, sharedData)
            {
                this.onRendered = onRendered;
            }

            public List<DrawNode>? Children
            {
                get => ((CompositeDrawableDrawNode)Child).Children;

                // FlatFillMode culls the scene's draw nodes so only the cleared FBO is captured,
                // while the capture/readback/encode path stays identical.
                set => ((CompositeDrawableDrawNode)Child).Children = Source.FlatFillMode ? new List<DrawNode>() : value!;
            }

            public bool AddChildDrawNodes => true;

            protected override long GetDrawVersion() => Source.captureVersion;

            protected override void PopulateContents(IRenderer renderer)
            {
                base.PopulateContents(renderer);

                // Children have just been rendered into SharedData.MainBuffer.
                onRendered(renderer, SharedData.MainBuffer);
            }

            protected override void DrawContents(IRenderer renderer)
            {
                // Still blit to the backbuffer so the visible window reflects the rendered state.
                base.DrawContents(renderer);
            }
        }
    }
}
