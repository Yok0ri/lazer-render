// Copyright (c) LazerRender contributors. Licensed under the MIT Licence.

using System;
using System.IO;
using ManagedBass;
using ManagedBass.Fx;
using ManagedBass.Mix;
using osu.Framework.Logging;

namespace LazerRender
{
    /// <summary>
    /// Decodes a beatmap audio track through ManagedBass in a deterministic, offline fashion.
    ///
    /// The track is opened as a BASS decode stream (no audio device output), rate-adjusted with
    /// BassFx tempo for DT/HT/Daycore/Nightcore, and fed through a 44.1kHz stereo decode mixer so
    /// <see cref="Read"/> always returns the exact s16le sample layout FFmpeg expects.
    /// </summary>
    public sealed class BassTrackDecoder : IDisposable
    {
        private const int frequency = 44100;
        private const int channels = 2;

        private readonly string tempFilePath;
        private int? mixerHandle;
        private int? sourceHandle;

        private BassTrackDecoder(int mixerHandle, int sourceHandle, string tempFilePath)
        {
            this.mixerHandle = mixerHandle;
            this.sourceHandle = sourceHandle;
            this.tempFilePath = tempFilePath;
        }

        /// <summary>
        /// Decodes the track and, when <paramref name="rate"/> differs from 1, applies the speed
        /// change through a BASS FX tempo stream.
        /// </summary>
        /// <param name="audioData">The raw audio bytes of the beatmap track.</param>
        /// <param name="rate">The speed multiplier (1.5 for DT, 0.75 for HT, ...).</param>
        /// <param name="adjustPitch">Whether the pitch should follow the speed change (DT/HT with
        /// "adjust pitch" enabled). When <c>false</c> pitch is preserved (Daycore/Nightcore and
        /// DT/HT with "adjust pitch" disabled).</param>
        /// <param name="tempFilePath">Temporary file used to hand the track to BASS.</param>
        public static BassTrackDecoder? Create(byte[] audioData, double rate, bool adjustPitch, string tempFilePath)
        {
            File.WriteAllBytes(tempFilePath, audioData);

            int stream = Bass.CreateStream(tempFilePath, 0, 0, BassFlags.Decode | BassFlags.Prescan);

            if (stream == 0)
            {
                Logger.Log($@"BASS failed to create track decode stream: {Bass.LastError}");
                return null;
            }

            int source = stream;

            if (Math.Abs(rate - 1.0) > 0.0001)
            {
                int tempo = BassFx.TempoCreate(stream, BassFlags.Decode | BassFlags.FxFreeSource);

                if (tempo != 0)
                {
                    if (adjustPitch)
                    {
                        // Frequency-style rate change: scales both tempo and pitch (the classic
                        // "chipmunk" DT / "deep" HT sound). Decode channels ignore the plain
                        // ChannelAttribute.Frequency, so this goes through BASS FX TempoFrequency
                        // instead, expressed relative to the source's own sample rate.
                        Bass.ChannelGetInfo(stream, out ChannelInfo info);
                        double sourceFrequency = info.Frequency > 0 ? info.Frequency : frequency;
                        Bass.ChannelSetAttribute(tempo, ChannelAttribute.TempoFrequency, sourceFrequency * rate);
                    }
                    else
                    {
                        // BASS tempo is expressed as a percentage offset (0 = 1.0x, +50 = 1.5x).
                        // Tempo-only processing preserves the original pitch (Daycore/Nightcore).
                        Bass.ChannelSetAttribute(tempo, ChannelAttribute.Tempo, (rate - 1.0) * 100);
                    }

                    source = tempo;
                }
            }

            int mixer = BassMix.CreateMixerStream(frequency, channels, BassFlags.Decode | BassFlags.MixerNonStop);

            if (mixer == 0)
            {
                Bass.StreamFree(source);
                return null;
            }

            if (!BassMix.MixerAddChannel(mixer, source, BassFlags.MixerChanNoRampin))
            {
                Bass.StreamFree(mixer);
                Bass.StreamFree(source);
                return null;
            }

            return new BassTrackDecoder(mixer, source, tempFilePath);
        }

        /// <summary>
        /// Reads exactly <paramref name="byteCount"/> bytes of interleaved s16le stereo PCM for the
        /// given track time, seeking the decode mixer first and padding any short reads with silence.
        /// </summary>
        public void Read(double seconds, byte[] buffer, int byteCount)
        {
            int mixer = mixerHandle ?? 0;

            if (mixer == 0 || seconds < 0)
            {
                Array.Clear(buffer, 0, byteCount);
                return;
            }

            long position = Bass.ChannelSeconds2Bytes(mixer, seconds);
            Bass.ChannelSetPosition(mixer, position);

            int read = Bass.ChannelGetData(mixer, buffer, byteCount);

            if (read < 0)
                read = 0;

            if (read < byteCount)
                Array.Clear(buffer, read, byteCount - read);
        }

        public void Dispose()
        {
            if (mixerHandle is int mixer)
            {
                Bass.StreamFree(mixer);
                mixerHandle = null;
            }

            if (sourceHandle is int source)
            {
                Bass.StreamFree(source);
                sourceHandle = null;
            }

            try
            {
                File.Delete(tempFilePath);
            }
            catch
            {
            }
        }
    }
}
