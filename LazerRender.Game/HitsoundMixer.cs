// Copyright (c) LazerRender contributors. Licensed under the MIT Licence.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using ManagedBass;
using ManagedBass.Mix;
using osu.Framework.Audio;
using osu.Framework.Audio.Mixing;
using osu.Framework.Logging;

namespace LazerRender
{
    /// <summary>
    /// Reflects into osu-framework's internal <c>BassAudioMixer</c> instances to intercept every sample
    /// (hitsound/UI sound) mixdown without modifying the engine submodule.
    ///
    /// <para>
    /// By default mixers are real-time playback mixers, which cannot be consumed faster-than-realtime.
    /// <see cref="ConvertToOfflineDecode"/> swaps each mixer's native BASS handle for an equivalent
    /// <see cref="BassFlags.Decode"/> mixer, so <see cref="Read"/> can pull the mixed PCM deterministically
    /// at the recorder's frame rate.
    /// </para>
    /// </summary>
    public sealed class HitsoundMixer
    {
        private const int frequency = 44100;
        private const int channels = 2;

        private readonly AudioManager audioManager;

        // Reflection metadata shared by every BassAudioMixer instance (all mixers use the same type).
        private readonly PropertyInfo? handleProperty;
        private readonly MethodInfo? handleSetter;
        private readonly FieldInfo? handleBackingField;

        // AudioManager.ActiveMixers (non-public property) / activeMixers (private field).
        private readonly PropertyInfo? activeMixersProperty;
        private readonly FieldInfo? activeMixersField;

        private readonly HashSet<AudioMixer> convertedMixers = new HashSet<AudioMixer>();
        private readonly List<int> captureHandles = new List<int>();

        private float[] sampleAccumulator = Array.Empty<float>();
        private float[] sampleScratch = Array.Empty<float>();

        public HitsoundMixer(AudioManager audioManager)
        {
            this.audioManager = audioManager;

            Type mixerType = audioManager.SampleMixer.GetType();

            handleProperty = mixerType.GetProperty("Handle", BindingFlags.Instance | BindingFlags.Public);
            handleSetter = handleProperty?.GetSetMethod(nonPublic: true);

            // Auto-property setters may not be discoverable via GetSetMethod on every compiler/runtime,
            // so keep the compiler-generated backing field as a fallback overwrite point.
            handleBackingField = mixerType.GetField("<Handle>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);

            Type managerType = typeof(AudioManager);
            activeMixersProperty = managerType.GetProperty("ActiveMixers", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            activeMixersField = managerType.GetField("activeMixers", BindingFlags.Instance | BindingFlags.NonPublic);
        }

        /// <summary>Whether the reflection targets required for interception could be resolved.</summary>
        public bool IsAvailable => handleProperty != null;

        /// <summary>
        /// Converts every currently known sample-routing mixer into an offline decode mixer.
        /// Safe to call repeatedly; newly created mixers (e.g. ruleset-isolated mixers) are picked up
        /// on subsequent calls from <see cref="Read"/>.
        /// </summary>
        public void ConvertToOfflineDecode()
        {
            if (handleProperty == null)
                return;

            IEnumerable? mixers = getActiveMixers();
            if (mixers == null)
                return;

            foreach (object item in mixers)
            {
                if (item is AudioMixer mixer)
                    convertMixer(mixer);
            }
        }

        /// <summary>
        /// Pulls one frame of interleaved s16le stereo PCM, summing every captured mixer and padding
        /// short reads with silence.
        /// </summary>
        public int Read(byte[] buffer, int byteCount)
        {
            Array.Clear(buffer, 0, byteCount);

            // Pick up any mixers created since the last pull (rulesets create isolated mixers lazily).
            ConvertToOfflineDecode();

            if (captureHandles.Count == 0)
                return 0;

            int sampleCount = byteCount / 2;

            if (sampleAccumulator.Length < sampleCount)
                sampleAccumulator = new float[sampleCount];

            if (sampleScratch.Length < sampleCount)
                sampleScratch = new float[sampleCount];

            float[] accumulator = sampleAccumulator;
            float[] scratch = sampleScratch;

            Array.Clear(accumulator, 0, sampleCount);

            foreach (int handle in captureHandles)
            {
                Array.Clear(scratch, 0, sampleCount);

                int read = Bass.ChannelGetData(handle, scratch, (sampleCount * sizeof(float)) | (int)DataFlags.Float);
                int floats = read <= 0 ? 0 : Math.Min(read / sizeof(float), sampleCount);

                for (int i = 0; i < floats; i++)
                    accumulator[i] += scratch[i];
            }

            // Convert the accumulated float32 samples to s16le, hard-clipping at [-1, 1].
            for (int i = 0; i < sampleCount; i++)
            {
                float v = Math.Clamp(accumulator[i], -1f, 1f);
                short s = (short)(v * short.MaxValue);

                buffer[i * 2] = (byte)s;
                buffer[i * 2 + 1] = (byte)(s >> 8);
            }

            return byteCount;
        }

        private void convertMixer(AudioMixer mixer)
        {
            if (!convertedMixers.Add(mixer))
                return;

            // The track mixer carries the beatmap music, which is already decoded offline via
            // BassTrackDecoder. Capturing it here too would double the music in the final mix.
            if (mixer.Identifier == "TrackMixer")
                return;

            if (handleProperty == null)
                return;

            int current = handleProperty.GetValue(mixer) is int h ? h : 0;

            // The mixer may not have been created by the audio thread yet; retry later.
            if (current == 0)
            {
                convertedMixers.Remove(mixer);
                return;
            }

            if (Bass.ChannelGetInfo(current, out ChannelInfo info) && (info.Flags & BassFlags.Decode) != 0)
            {
                captureHandles.Add(current);
                Logger.Log($@"Hitsound mixer: mixer ""{mixer.Identifier}"" is already an offline decode mixer (#{current}).");
                return;
            }

            // Float is required here: osu-framework DSP/effects expect a float mixer.
            int decode = BassMix.CreateMixerStream(frequency, channels, BassFlags.Decode | BassFlags.MixerNonStop | BassFlags.Float);

            if (decode == 0)
            {
                convertedMixers.Remove(mixer);
                Logger.Log($@"Hitsound mixer: failed to create decode mixer for ""{mixer.Identifier}"" ({Bass.LastError}).");
                return;
            }

            try
            {
                overwriteHandle(mixer, decode);
            }
            catch (Exception ex)
            {
                Bass.StreamFree(decode);
                Logger.Log($@"Hitsound mixer: failed to overwrite handle for ""{mixer.Identifier}"" ({ex.Message}).");
                return;
            }

            captureHandles.Add(decode);
            Logger.Log($@"Hitsound mixer: converted mixer ""{mixer.Identifier}"" to offline decode mixer #{decode}.");
        }

        private void overwriteHandle(AudioMixer mixer, int value)
        {
            if (handleSetter != null)
            {
                try
                {
                    handleSetter.Invoke(mixer, new object[] { value });
                    return;
                }
                catch
                {
                    // Fall through to the backing-field path below.
                }
            }

            if (handleBackingField != null)
            {
                handleBackingField.SetValue(mixer, value);
                return;
            }

            throw new InvalidOperationException(@"No Handle setter or backing field found.");
        }

        private IEnumerable? getActiveMixers()
        {
            object? value = activeMixersProperty?.GetValue(audioManager);
            value ??= activeMixersField?.GetValue(audioManager);

            return value as IEnumerable;
        }

        /// <summary>
        /// Adds two interleaved s16le stereo buffers sample-by-sample, clamping into
        /// <paramref name="destination"/>.
        /// </summary>
        public static void AddClamped(byte[] destination, byte[] source, int byteCount)
        {
            int bytes = Math.Min(byteCount, Math.Min(destination.Length, source.Length)) & ~1;

            for (int i = 0; i < bytes; i += 2)
            {
                short a = (short)(destination[i] | (destination[i + 1] << 8));
                short b = (short)(source[i] | (source[i + 1] << 8));
                int sum = a + b;

                short clamped = (short)Math.Clamp(sum, short.MinValue, short.MaxValue);

                destination[i] = (byte)clamped;
                destination[i + 1] = (byte)(clamped >> 8);
            }
        }
    }
}
