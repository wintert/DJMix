using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;

namespace DJAutoMixApp.Services
{
    /// <summary>
    /// Sample provider for a single deck with sample-accurate position tracking
    /// This replaces the WaveOutEvent-per-deck architecture
    /// </summary>
    public class DeckSampleProvider : ISampleProvider
    {
        private AudioFileReader? audioFile;
        private SoundTouchSampleProvider? soundTouchProvider;
        private EqualizerSampleProvider? eqProvider;
        private VolumeSampleProvider? volumeProvider;

        public WaveFormat WaveFormat { get; private set; }

        // Deck properties
        public string DeckName { get; }
        public double BPM { get; set; }
        public double BeatOffset { get; set; } // First beat position in seconds
        public bool IsTrackLoaded => audioFile != null;
        public string? CurrentTrackPath { get; private set; }

        // Playback state
        public bool IsPlaying { get; set; }
        private long samplePosition = 0; // Sample-accurate position

        // Sync offset in samples - mixer applies this during playback
        public long SyncOffsetSamples { get; set; } = 0;

        // Tempo and pitch
        private double tempo = 1.0;
        public double Tempo
        {
            get => tempo;
            set
            {
                tempo = Math.Clamp(value, 0.5, 2.0);
                if (soundTouchProvider != null)
                    soundTouchProvider.Tempo = tempo;
            }
        }

        private double pitch = 0;
        public double Pitch
        {
            get => pitch;
            set
            {
                pitch = Math.Clamp(value, -12, 12);
                if (soundTouchProvider != null)
                    soundTouchProvider.Pitch = pitch;
            }
        }

        // Volume (0.0 to 1.0)
        private float volume = 0.7f;
        public float Volume
        {
            get => volume;
            set
            {
                volume = Math.Clamp(value, 0f, 1f);
                if (volumeProvider != null)
                    volumeProvider.Volume = volume;
            }
        }

        // EQ Controls (0.0=kill, 1.0=flat, 2.0=+6dB)
        private float eqLow = 1.0f;
        public float EqLow
        {
            get => eqLow;
            set
            {
                eqLow = Math.Clamp(value, 0f, 2f);
                if (eqProvider != null) eqProvider.LowGain = eqLow;
            }
        }

        private float eqMid = 1.0f;
        public float EqMid
        {
            get => eqMid;
            set
            {
                eqMid = Math.Clamp(value, 0f, 2f);
                if (eqProvider != null) eqProvider.MidGain = eqMid;
            }
        }

        private float eqHigh = 1.0f;
        public float EqHigh
        {
            get => eqHigh;
            set
            {
                eqHigh = Math.Clamp(value, 0f, 2f);
                if (eqProvider != null) eqProvider.HighGain = eqHigh;
            }
        }

        // Position properties - use FILE position, not output sample count!
        // This is critical for tempo changes - we need to know where we are IN THE SONG
        public TimeSpan CurrentPosition => audioFile?.CurrentTime ?? TimeSpan.Zero;
        public TimeSpan Duration => audioFile?.TotalTime ?? TimeSpan.Zero;
        public long SamplePosition => samplePosition;
        public double EffectiveBPM => BPM * Tempo;

        // Events
        public event EventHandler<TimeSpan>? PositionChanged;
        public event EventHandler? TrackEnded;

        public DeckSampleProvider(string deckName, int sampleRate = 44100, int channels = 2)
        {
            DeckName = deckName;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }

        public void LoadTrack(string filePath, double bpm = 120, double beatOffset = 0)
        {
            try
            {
                // Dispose old audio
                DisposeAudio();

                audioFile = new AudioFileReader(filePath);

                // Chain: AudioFile -> SoundTouch -> EQ -> Volume
                soundTouchProvider = new SoundTouchSampleProvider(audioFile);
                soundTouchProvider.Tempo = tempo;
                soundTouchProvider.Pitch = pitch;

                eqProvider = new EqualizerSampleProvider(soundTouchProvider);
                eqProvider.LowGain = eqLow;
                eqProvider.MidGain = eqMid;
                eqProvider.HighGain = eqHigh;

                volumeProvider = new VolumeSampleProvider(eqProvider);
                volumeProvider.Volume = volume;

                CurrentTrackPath = filePath;
                BPM = bpm <= 0 ? 120 : bpm;
                BeatOffset = beatOffset;
                samplePosition = 0;
                IsPlaying = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading track on {DeckName}: {ex.Message}");
                throw;
            }
        }

        public void SetPosition(TimeSpan position)
        {
            if (audioFile != null)
            {
                audioFile.CurrentTime = position;
                samplePosition = (long)(position.TotalSeconds * WaveFormat.SampleRate * WaveFormat.Channels);
                soundTouchProvider?.Clear();
            }
        }

        public void Stop()
        {
            IsPlaying = false;
            if (audioFile != null)
            {
                audioFile.CurrentTime = TimeSpan.Zero;
                samplePosition = 0;
            }
        }

        /// <summary>
        /// Read samples from this deck
        /// Called by the mixer at audio callback rate
        /// </summary>
        public int Read(float[] buffer, int offset, int count)
        {
            if (!IsPlaying || volumeProvider == null || audioFile == null)
            {
                // Return silence if not playing
                Array.Clear(buffer, offset, count);
                return count;
            }

            // Apply sync offset if present
            if (SyncOffsetSamples > 0)
            {
                // Need to delay (return silence and decrease offset)
                long samplesToSilence = Math.Min(SyncOffsetSamples, count);
                Array.Clear(buffer, offset, (int)samplesToSilence);
                SyncOffsetSamples -= samplesToSilence;

                // If we silenced the whole buffer, return
                if (samplesToSilence == count)
                {
                    return count;
                }

                // Read remaining samples
                int remaining = count - (int)samplesToSilence;
                int read = volumeProvider.Read(buffer, offset + (int)samplesToSilence, remaining);
                samplePosition += read;
                return count;
            }
            else if (SyncOffsetSamples < 0)
            {
                // Need to skip ahead (read and discard samples)
                long samplesToSkip = -SyncOffsetSamples;
                float[] tempBuffer = new float[Math.Min(samplesToSkip, 8192)];

                while (samplesToSkip > 0)
                {
                    int toRead = (int)Math.Min(samplesToSkip, tempBuffer.Length);
                    int skipped = volumeProvider.Read(tempBuffer, 0, toRead);
                    if (skipped == 0) break; // End of file
                    samplesToSkip -= skipped;
                    samplePosition += skipped;
                }

                SyncOffsetSamples = 0; // Done skipping
            }

            // Normal read
            int samplesRead = volumeProvider.Read(buffer, offset, count);

            if (samplesRead > 0)
            {
                samplePosition += samplesRead;

                // Check if track ended
                if (samplesRead < count)
                {
                    // Fill rest with silence
                    Array.Clear(buffer, offset + samplesRead, count - samplesRead);
                    TrackEnded?.Invoke(this, EventArgs.Empty);
                    IsPlaying = false;
                    return count;
                }
            }

            return samplesRead > 0 ? count : count; // Always return count to maintain timing
        }

        private void DisposeAudio()
        {
            audioFile?.Dispose();
            audioFile = null;
            soundTouchProvider = null;
            eqProvider = null;
            volumeProvider = null;
        }

        public void Dispose()
        {
            DisposeAudio();
        }
    }
}
