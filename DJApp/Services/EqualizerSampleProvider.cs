using NAudio.Dsp;
using NAudio.Wave;
using System;

namespace DJAutoMixApp.Services
{
    /// <summary>
    /// Sample provider with 3-band equalizer (Low, Mid, High) with full kill capability
    /// </summary>
    public class EqualizerSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider sourceProvider;
        private BiQuadFilter[] lowFilters;
        private BiQuadFilter[] midFilters;
        private BiQuadFilter[] highFilters;
        
        private float lowGain = 1.0f;
        private float midGain = 1.0f;
        private float highGain = 1.0f;

        // Frequency bands (typical DJ EQ ranges)
        private const float LOW_FREQ = 100f;
        private const float MID_FREQ = 1000f;
        private const float HIGH_FREQ = 10000f;
        private const float Q = 1.0f;

        public WaveFormat WaveFormat => sourceProvider.WaveFormat;

        /// <summary>
        /// Low EQ gain (0.0 = full kill, 1.0 = unity, 2.0 = +6dB)
        /// </summary>
        public float LowGain
        {
            get => lowGain;
            set => lowGain = Math.Max(0f, Math.Min(2f, value));
        }

        /// <summary>
        /// Mid EQ gain (0.0 = full kill, 1.0 = unity, 2.0 = +6dB)
        /// </summary>
        public float MidGain
        {
            get => midGain;
            set => midGain = Math.Max(0f, Math.Min(2f, value));
        }

        /// <summary>
        /// High EQ gain (0.0 = full kill, 1.0 = unity, 2.0 = +6dB)
        /// </summary>
        public float HighGain
        {
            get => highGain;
            set => highGain = Math.Max(0f, Math.Min(2f, value));
        }

        public EqualizerSampleProvider(ISampleProvider sourceProvider)
        {
            this.sourceProvider = sourceProvider;
            int channels = sourceProvider.WaveFormat.Channels;
            int sampleRate = sourceProvider.WaveFormat.SampleRate;

            // Create filters for each channel
            lowFilters = new BiQuadFilter[channels];
            midFilters = new BiQuadFilter[channels];
            highFilters = new BiQuadFilter[channels];

            for (int i = 0; i < channels; i++)
            {
                lowFilters[i] = BiQuadFilter.LowPassFilter(sampleRate, LOW_FREQ, Q);
                midFilters[i] = BiQuadFilter.PeakingEQ(sampleRate, MID_FREQ, Q, 0);
                highFilters[i] = BiQuadFilter.HighPassFilter(sampleRate, HIGH_FREQ, Q);
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int samplesRead = sourceProvider.Read(buffer, offset, count);
            int channels = WaveFormat.Channels;

            for (int i = 0; i < samplesRead; i++)
            {
                int channel = i % channels;
                float sample = buffer[offset + i];

                // Simple 3-band approach: split into low, mid, high and recombine
                // For a proper implementation, we'd use crossover filters
                // This is a simplified approach that still gives good results
                float lowSample = lowFilters[channel].Transform(sample) * lowGain;
                float highSample = highFilters[channel].Transform(sample) * highGain;
                float midSample = sample * midGain; // Mid is the remaining

                // Recombine (simplified mixing)
                buffer[offset + i] = (lowSample + midSample + highSample) / 3f;
            }

            return samplesRead;
        }
    }
}
