// Copyright (c) LazerRender contributors. Licensed under the MIT Licence.

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using osu.Framework.Logging;

namespace LazerRender
{
    /// <summary>
    /// Pipes raw video (RGBA via stdin) and raw audio (s16le via a named FIFO) into a single FFmpeg
    /// process which muxes them into an mp4.
    ///
    /// Video frames are produced by the asynchronous capture pipeline (PBO readback + background
    /// float→byte conversion) and enqueued here; a single writer thread performs the blocking
    /// <c>stdin.Write</c>. Audio is written on a dedicated thread fed by a queue, so FFmpeg can read
    /// either stream without one blocking the other.
    ///
    /// The FFmpeg process is started lazily on the first <see cref="EnqueueFrame"/> using the actual
    /// dimensions of the captured frame. The requested window size may be rejected by the compositor
    /// (e.g. Wayland cannot reposition non-popup windows), leaving the FBO at a different resolution,
    /// so the CLI width/height must never be trusted for the rawvideo demuxer.
    /// </summary>
    public sealed class FfmpegFrameSink : IDisposable
    {
        private const int audioSampleRate = 44100;
        private const int audioChannels = 2;

        /// <summary>
        /// Maximum number of video frames buffered between the recorder loop and the pipe writer.
        /// Kept tiny so the game can start rendering the next frame immediately while frame N is in
        /// flight, without ever accumulating an unbounded backlog of raw pixels.
        /// </summary>
        private const int videoQueueCapacity = 3;

        private readonly string outputPath;
        private readonly EncoderKind encoder;

        private int fps;
        private int targetWidth;
        private int targetHeight;
        private int motionBlurFrames;
        private Process? process;
        private Stream? stdin;
        private Stream? audioStream;
        private string? audioFifoPath;

        private BlockingCollection<byte[]>? audioQueue;
        private Task? audioWriterTask;

        private BlockingCollection<VideoFrame>? videoQueue;
        private Task? videoWriterTask;
        private Task? stderrDrainTask;

        /// <summary>A queued video frame: a pooled byte buffer plus its exact byte length.</summary>
        private readonly struct VideoFrame
        {
            public readonly byte[] Data;
            public readonly int ByteCount;

            public VideoFrame(byte[] data, int byteCount)
            {
                Data = data;
                ByteCount = byteCount;
            }
        }

        public FfmpegFrameSink(string outputPath, EncoderKind encoder = EncoderKind.Cpu)
        {
            this.outputPath = outputPath;
            this.encoder = encoder;
        }

        /// <summary>
        /// Prepares the audio FIFO and the audio writer thread, then starts FFmpeg eagerly with the
        /// requested dimensions. Starting FFmpeg here (before the recording loop runs, during the
        /// warm-up delay) means it is fully consuming its inputs by the time the first frame arrives,
        /// avoiding a startup burst that would otherwise fill the bounded queues and deadlock the
        /// pipeline. If the compositor rejects the requested size, <see cref="EnqueueFrame"/>'s size
        /// guard aborts the render cleanly before any misaligned frame is written.
        /// </summary>
        public void Start(int fps, int targetWidth, int targetHeight, int motionBlurFrames = 0)
        {
            this.fps = fps;
            this.targetWidth = targetWidth;
            this.targetHeight = targetHeight;
            this.motionBlurFrames = motionBlurFrames;

            string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
            audioFifoPath = Path.Combine(directory, "audio.fifo");

            if (File.Exists(audioFifoPath))
                File.Delete(audioFifoPath);

            using (Process? mkfifo = Process.Start(new ProcessStartInfo("mkfifo", $@"""{audioFifoPath}""") { UseShellExecute = false }))
                mkfifo?.WaitForExit();

            // Open the FIFO with ReadWrite access (non-blocking) so FFmpeg's read-side open completes.
            audioStream = new FileStream(audioFifoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

            // The video micro-queue is bounded (3 frames) so the recorder loop can stay a frame or
            // two ahead of the OS pipe write without risking RAM saturation at high resolutions.
            videoQueue = new BlockingCollection<VideoFrame>(videoQueueCapacity);

            audioQueue = new BlockingCollection<byte[]>();
            audioWriterTask = Task.Run(() =>
            {
                foreach (byte[] buffer in audioQueue.GetConsumingEnumerable())
                    audioStream.Write(buffer, 0, buffer.Length);
            });

            // Eager start: FFmpeg is spawned now (with the requested size) so it is running and
            // consuming both inputs before the first frame is captured.
            startProcess(targetWidth, targetHeight);
        }

        /// <summary>
        /// Enqueues a converted RGBA8 frame. <paramref name="rgba"/> is a pooled buffer whose ownership
        /// transfers here; it is returned to the <see cref="ArrayPool{T}"/> after the pipe write.
        /// <paramref name="byteCount"/> is the exact frame size (a pooled array may be larger than the
        /// requested size, so it must never be trusted as the write length).
        /// </summary>
        public void EnqueueFrame(byte[] rgba, int width, int height)
        {
            // The compositor can resize the surface mid-render, shifting the FBO dimensions and
            // producing a stride mismatch that corrupts the video or crashes. Never write a
            // misaligned frame to FFmpeg.
            if (width != targetWidth || height != targetHeight)
                throw new InvalidOperationException(
                    $@"FBO size shifted mid-render! Expected {targetWidth}x{targetHeight}, got {width}x{height}.");

            // FFmpeg was started eagerly in Start(); the writer thread consumes this queue.
            videoQueue!.Add(new VideoFrame(rgba, width * height * 4));
        }

        public void WriteAudio(byte[] pcm, int byteCount)
        {
            if (audioQueue == null)
                throw new InvalidOperationException(@"FFmpeg audio queue has not been started.");

            // Copy: the recorder reuses its buffer for the next frame.
            byte[] copy = new byte[byteCount];
            Buffer.BlockCopy(pcm, 0, copy, 0, byteCount);
            audioQueue.Add(copy);
        }

        private void startProcess(int width, int height)
        {
            // Hardware backends require their device/filter plumbing to be present before the
            // inputs (VAAPI/QSV) and a pixel-format + upload filter chain on the video output.
            // The raw RGBA source is unchanged: the upload filter is applied after the demuxer,
            // and the CPU-side GL_FLOAT extraction path is never touched.
            string hardwareInitArgs = buildHardwareInitArgs();
            string videoFilterArgs = buildVideoFilterArgs();
            string encoderArgs = buildEncoderArgs();

            // Both inputs are described completely on the command line, so FFmpeg never needs to
            // probe or analyse either of them. Disabling that analysis is not just an optimisation:
            // before transcoding, FFmpeg reads a chunk of *every* input to work out what it is, and
            // the audio FIFO is still empty at that moment because the recorder has not captured its
            // first frame yet. That read blocks, so FFmpeg never starts draining the video pipe, the
            // engine's bounded queues fill up, and the whole render wedges on frame one. FFmpeg 5.1
            // (Debian 12) behaves this way; 9.x does not. Always pass this explicitly rather than
            // depending on the distro's defaults. -probesize 32 is the documented minimum.
            const string inputAnalysisArgs = @"-analyzeduration 0 -probesize 32 ";

            // loglevel info (plus -hide_banner) keeps FFmpeg's periodic `fps=`/`speed=` progress
            // lines on stderr so the sink can surface encoder throughput for diagnostics.
            string arguments =
                $@"-y -loglevel info -hide_banner {hardwareInitArgs}{inputAnalysisArgs}-f rawvideo -pix_fmt rgba -s {width}x{height} -r {fps} -i pipe:0 " +
                $@"{inputAnalysisArgs}-f s16le -ar {audioSampleRate} -ac {audioChannels} -i ""{audioFifoPath}"" " +
                $@"-map 0:v -map 1:a {videoFilterArgs}{encoderArgs}-c:a aac -b:a 192k ""{outputPath}""";

            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
                EnableRaisingEvents = true,
            };

            if (!process.Start())
                throw new InvalidOperationException(@"Failed to start FFmpeg.");

            stdin = process.StandardInput.BaseStream;

            startVideoWriter();
            startStderrTelemetry();
        }

        /// <summary>
        /// Runs a single background task which drains the bounded video queue and performs the
        /// blocking <see cref="Stream.Write(byte[], int, int)"/> to FFmpeg's stdin. This is the only
        /// work moved off the recorder loop: glReadPixels, float→byte conversion and game logic all
        /// remain on their original threads.
        /// </summary>
        private void startVideoWriter()
        {
            videoWriterTask = Task.Run(() =>
            {
                try
                {
                    foreach (VideoFrame frame in videoQueue!.GetConsumingEnumerable())
                    {
                        try
                        {
                            stdin!.Write(frame.Data, 0, frame.ByteCount);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(frame.Data);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, @"FFmpeg video writer failed.");

                    // Fail fast: complete the queue so a blocked producer throws instead of
                    // hanging on a full queue after FFmpeg has died.
                    videoQueue?.CompleteAdding();
                }
            });
        }

        /// <summary>
        /// Drains FFmpeg's stderr asynchronously and logs any line containing <c>fps=</c> or
        /// <c>speed=</c>. This exposes the encoder's real throughput so hardware-vs-software
        /// fallback and filter bottlenecks are visible in the terminal.
        /// </summary>
        private void startStderrTelemetry()
        {
            stderrDrainTask = Task.Run(async () =>
            {
                using StreamReader reader = process!.StandardError;
                string? line;

                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (line.Contains(@"fps=", StringComparison.Ordinal) ||
                        line.Contains(@"speed=", StringComparison.Ordinal))
                    {
                        Logger.Log($@"FFmpeg: {line}");
                    }
                }
            });
        }

        /// <summary>
        /// Returns the FFmpeg hardware-device initialisation arguments for the selected encoder
        /// (placed before the inputs), or an empty string for software encoding.
        /// </summary>
        private string buildHardwareInitArgs() => encoder switch
        {
            EncoderKind.Amd => @"-vaapi_device /dev/dri/renderD128 ",
            EncoderKind.Intel => @"-init_hw_device qsv=hw -filter_hw_device hw ",
            _ => string.Empty,
        };

        /// <summary>
        /// Returns the <c>-vf</c> chain for the selected encoder, combining the optional motion-blur
        /// <c>tmix</c> filter with any pixel-format/upload filters required by the hardware backend.
        /// </summary>
        private string buildVideoFilterArgs()
        {
            string? motionBlurFilter = buildMotionBlurFilter();

            // Hardware backends consume NV12, so the filter chain converts the raw RGBA frames on the
            // CPU and uploads them to the device. For QSV the final "format=qsv" is required so the
            // upload filter produces frames in the format the encoder expects.
            string? encoderFilter = encoder switch
            {
                EncoderKind.Amd => "format=nv12,hwupload",
                EncoderKind.Intel => "format=nv12,hwupload=extra_hw_frames=64,format=qsv",
                _ => null,
            };

            if (motionBlurFilter == null && encoderFilter == null)
                return string.Empty;

            var filters = new System.Collections.Generic.List<string>();
            if (motionBlurFilter != null)
                filters.Add(motionBlurFilter);
            if (encoderFilter != null)
                filters.Add(encoderFilter);

            return $@"-vf ""{string.Join(",", filters)}"" ";
        }

        /// <summary>
        /// Returns the video encoder arguments for the selected backend. The software fallback keeps
        /// the original libx264 settings.
        /// </summary>
        private string buildEncoderArgs() => encoder switch
        {
            EncoderKind.Amd => @"-c:v h264_vaapi -qp 18 ",
            EncoderKind.Nvidia => @"-c:v h264_nvenc -preset p4 -cq 18 ",
            EncoderKind.Intel => @"-c:v h264_qsv -global_quality 18 ",
            _ => @"-c:v libx264 -crf 18 -preset fast ",
        };

        /// <summary>
        /// Builds the <c>tmix=...</c> filter expression when motion blur is enabled, or <c>null</c>
        /// when it is disabled. The weights decay exponentially so the most recent frame dominates,
        /// simulating a longer shutter angle without touching the recorder loop.
        /// </summary>
        private string? buildMotionBlurFilter()
        {
            if (motionBlurFrames <= 1)
                return null;

            int frames = Math.Clamp(motionBlurFrames, 2, 32);

            // Exponential decay, most recent frame first: 1, r, r^2, ... The weights are normalised
            // to sum to 1 here because this FFmpeg build's tmix filter has no "normalize" option.
            double[] weights = new double[frames];
            for (int i = 0; i < frames; i++)
                weights[i] = Math.Pow(0.6, i);

            double sum = weights.Sum();

            string weightList = string.Join(" ", weights.Select(w => (w / sum).ToString("0.###", CultureInfo.InvariantCulture)));

            return $@"tmix=frames={frames}:weights={weightList}";
        }

        public void Finish()
        {
            // Stop accepting video frames and wait for the writer to drain every queued frame into
            // FFmpeg's stdin before closing the pipe.
            videoQueue?.CompleteAdding();
            videoWriterTask?.Wait();

            audioQueue?.CompleteAdding();

            // Close the video input first. FFmpeg's rawvideo demuxer blocks on stdin waiting for
            // more frames/EOF; once it sees EOF it turns to draining the audio FIFO, which in turn
            // unblocks the audio writer thread below.
            stdin?.Flush();
            stdin?.Close();

            audioWriterTask?.Wait();

            audioStream?.Flush();
            audioStream?.Close();

            process?.WaitForExit();
            stderrDrainTask?.Wait();
        }

        public void Dispose()
        {
            try
            {
                videoQueue?.CompleteAdding();
            }
            catch
            {
            }

            try
            {
                audioQueue?.CompleteAdding();
            }
            catch
            {
            }

            try
            {
                if (process is { HasExited: false })
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            try
            {
                audioStream?.Dispose();
            }
            catch
            {
            }

            try
            {
                stdin?.Dispose();
            }
            catch
            {
            }

            process?.Dispose();

            try
            {
                if (audioFifoPath != null)
                    File.Delete(audioFifoPath);
            }
            catch
            {
            }
        }
    }
}
