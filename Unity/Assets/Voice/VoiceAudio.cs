#nullable enable

using System;

namespace NeuroSdk.Voice
{
    /// <summary>
    /// Audio conversion helpers for the voice chat side-channel.
    /// The wire format is always 48 kHz mono Float32 little-endian PCM.
    /// </summary>
    internal static class VoiceAudio
    {
        public const int WireSampleRate = 48000;

        public const byte SpeakerFrameVersion = 1;
        public const int SpeakerFrameHeaderBytes = 4;

        /// <summary>
        /// Convert arbitrary interleaved PCM to 48 kHz mono. Returns the input array
        /// unchanged when no conversion is needed.
        /// </summary>
        public static float[] ToWireFormat(float[] samples, int sampleRate, int channels)
        {
            if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
            if (sampleRate < 1) throw new ArgumentOutOfRangeException(nameof(sampleRate));

            float[] mono = channels == 1 ? samples : Downmix(samples, channels);
            return sampleRate == WireSampleRate ? mono : Resample(mono, sampleRate, WireSampleRate);
        }

        private static float[] Downmix(float[] interleaved, int channels)
        {
            int frames = interleaved.Length / channels;
            float[] mono = new float[frames];
            for (int i = 0; i < frames; i++)
            {
                float sum = 0;
                int offset = i * channels;
                for (int c = 0; c < channels; c++) sum += interleaved[offset + c];
                mono[i] = sum / channels;
            }
            return mono;
        }

        private static float[] Resample(float[] mono, int fromRate, int toRate)
        {
            int outLength = (int)((long)mono.Length * toRate / fromRate);
            if (outLength <= 0) return Array.Empty<float>();

            float[] resampled = new float[outLength];
            double step = (double)fromRate / toRate;
            for (int i = 0; i < outLength; i++)
            {
                double pos = i * step;
                int i0 = (int)pos;
                int i1 = i0 + 1 < mono.Length ? i0 + 1 : i0;
                float frac = (float)(pos - i0);
                resampled[i] = mono[i0] + (mono[i1] - mono[i0]) * frac;
            }
            return resampled;
        }

        /// <summary>
        /// Build a speaker audio frame: [u8 version, u8 flags, u16le speaker id, f32le PCM...].
        /// All Unity targets are little-endian, so a block copy produces f32le directly.
        /// </summary>
        public static byte[] EncodeSpeakerFrame(int speakerId, float[] wireSamples)
        {
            byte[] frame = new byte[SpeakerFrameHeaderBytes + wireSamples.Length * sizeof(float)];
            frame[0] = SpeakerFrameVersion;
            frame[1] = 0;
            frame[2] = (byte)(speakerId & 0xff);
            frame[3] = (byte)((speakerId >> 8) & 0xff);
            Buffer.BlockCopy(wireSamples, 0, frame, SpeakerFrameHeaderBytes, wireSamples.Length * sizeof(float));
            return frame;
        }

        /// <summary>
        /// Decode a headerless downstream PCM frame (48 kHz mono f32le). Returns null
        /// if the payload is not float32-aligned.
        /// </summary>
        public static float[]? DecodePcm(byte[] data)
        {
            if (data.Length % sizeof(float) != 0) return null;
            float[] samples = new float[data.Length / sizeof(float)];
            Buffer.BlockCopy(data, 0, samples, 0, data.Length);
            return samples;
        }
    }
}
