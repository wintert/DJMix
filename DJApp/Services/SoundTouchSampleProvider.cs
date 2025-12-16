using NAudio.Wave;
using SoundTouch;
using System;

namespace DJAutoMixApp.Services
{
    public class SoundTouchSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider sourceProvider;
        private readonly SoundTouchProcessor processor;
        private readonly float[] sourceBuffer;
        private const int BufferSize = 2048; // Chunk size for reading

        public WaveFormat WaveFormat => sourceProvider.WaveFormat;

        public double Tempo
        {
            get => processor.Tempo;
            set => processor.Tempo = value;
        }

        public double Pitch
        {
            get => processor.PitchSemiTones;
            set => processor.PitchSemiTones = value;
        }

        public SoundTouchSampleProvider(ISampleProvider source)
        {
            sourceProvider = source;
            processor = new SoundTouchProcessor();
            processor.SampleRate = source.WaveFormat.SampleRate;
            processor.Channels = source.WaveFormat.Channels;
            
            // Buffer to read raw samples from source before processing
            sourceBuffer = new float[BufferSize * source.WaveFormat.Channels]; 
        }

        public void Clear()
        {
            processor.Clear();
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int channels = WaveFormat.Channels;
            int samplesWritten = 0;
            
            // Note: SoundTouch works with "numSamples" which implies FRAMES (samples per channel)
            
            // 1. Output any already processed samples
            // ReceiveSamples(Span<float> output, int maxSamples)
            // maxSamples = (available in output buffer) / channels
            int maxFrames = (count - samplesWritten) / channels;
            if (maxFrames > 0)
            {
                int framesReceived = processor.ReceiveSamples(buffer.AsSpan(offset, count), maxFrames);
                samplesWritten += framesReceived * channels;
            }
            
            // 2. Loop until we have enough samples or source is empty
            while (samplesWritten < count)
            {
                int remaining = count - samplesWritten;
                int maxFramesNeeded = remaining / channels;
                
                if (maxFramesNeeded == 0) break; // Buffer full (or aligned to < 1 frame)

                // Read fresh data from source
                int readCount = sourceProvider.Read(sourceBuffer, 0, sourceBuffer.Length);
                
                if (readCount == 0)
                {
                    // End of stream
                     // Try one filter flush
                     processor.Flush();
                     int finalFrames = processor.ReceiveSamples(buffer.AsSpan(offset + samplesWritten, remaining), maxFramesNeeded);
                     samplesWritten += finalFrames * channels;
                    break;
                }

                // Push to processor
                // PutSamples(ReadOnlySpan<float> samples, int numSamples)
                // numSamples = readCount / channels
                processor.PutSamples(sourceBuffer.AsSpan(0, readCount), readCount / channels);

                // Retrieve processed data
                int receivedFrames = processor.ReceiveSamples(buffer.AsSpan(offset + samplesWritten, remaining), maxFramesNeeded);
                samplesWritten += receivedFrames * channels;
            }

            return samplesWritten;
        }
    }
}
