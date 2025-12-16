using NAudio.Wave;
using System;

namespace DJAutoMixApp.Services
{
    /// <summary>
    /// Mixer that combines two decks with sample-accurate synchronization
    /// This is the KEY to perfect beat matching - both decks feed into a single output
    /// </summary>
    public class MixerSampleProvider : ISampleProvider
    {
        public DeckSampleProvider DeckA { get; }
        public DeckSampleProvider DeckB { get; }
        public WaveFormat WaveFormat { get; }

        // Crossfader (0.0 = full Deck A, 1.0 = full Deck B)
        private double crossfaderPosition = 0.5; // Start at middle (equal mix)
        public double CrossfaderPosition
        {
            get => crossfaderPosition;
            set => crossfaderPosition = Math.Clamp(value, 0.0, 1.0);
        }

        // Sync state
        private DeckSampleProvider? syncSlaveDeck;
        private DeckSampleProvider? syncMasterDeck;
        private long samplesToWaitBeforeStart = 0;
        private bool isWaitingForBeat = false;
        private int phaseCheckCounter = 0;
        private bool initialSyncApplied = false;

        public MixerSampleProvider(DeckSampleProvider deckA, DeckSampleProvider deckB)
        {
            DeckA = deckA;
            DeckB = deckB;
            WaveFormat = deckA.WaveFormat;

            if (deckB.WaveFormat.SampleRate != WaveFormat.SampleRate ||
                deckB.WaveFormat.Channels != WaveFormat.Channels)
            {
                throw new ArgumentException("Both decks must have the same sample rate and channel count");
            }
        }

        /// <summary>
        /// Enable sync: slave deck will start quantized to master's next beat
        /// </summary>
        public void EnableSync(DeckSampleProvider slaveDeck, DeckSampleProvider masterDeck)
        {
            syncSlaveDeck = slaveDeck;
            syncMasterDeck = masterDeck;
            initialSyncApplied = false; // Reset for next sync

            // Match tempo
            double masterEffectiveBPM = masterDeck.BPM * masterDeck.Tempo;
            slaveDeck.Tempo = masterEffectiveBPM / slaveDeck.BPM;
        }

        /// <summary>
        /// Disable sync
        /// </summary>
        public void DisableSync(DeckSampleProvider deck)
        {
            if (syncSlaveDeck == deck)
            {
                syncSlaveDeck = null;
                syncMasterDeck = null;
                isWaitingForBeat = false;
                samplesToWaitBeforeStart = 0;
            }
        }

        /// <summary>
        /// Reset sync flag so next Play will re-sync
        /// </summary>
        public void ResetSyncFlag()
        {
            initialSyncApplied = false;
        }

        /// <summary>
        /// Prepare quantized start: calculate when slave should start
        /// Call this BEFORE setting IsPlaying = true on slave deck
        /// </summary>
        public void PrepareQuantizedStart(DeckSampleProvider slaveDeck, DeckSampleProvider masterDeck)
        {
            if (!masterDeck.IsPlaying)
            {
                // Master not playing - just start normally
                return;
            }

            // Calculate samples per beat for master
            int sampleRate = WaveFormat.SampleRate;
            int channels = WaveFormat.Channels;
            double masterBeatDurationSeconds = 60.0 / masterDeck.BPM;
            long masterSamplesPerBeat = (long)(masterBeatDurationSeconds * sampleRate * channels);

            // Calculate master's position within current beat
            double masterPosSeconds = masterDeck.CurrentPosition.TotalSeconds - masterDeck.BeatOffset;
            long masterSamplePos = (long)(masterPosSeconds * sampleRate * channels);
            long masterSamplesIntoBeat = masterSamplePos % masterSamplesPerBeat;
            if (masterSamplesIntoBeat < 0) masterSamplesIntoBeat += masterSamplesPerBeat;

            // Calculate samples until master's next beat
            samplesToWaitBeforeStart = masterSamplesPerBeat - masterSamplesIntoBeat;

            // If we're very close to the next beat (< 100ms), wait for the beat after that
            // This prevents starting mid-beat due to processing delays
            long minWaitSamples = (long)(0.100 * sampleRate * channels); // 100ms
            if (samplesToWaitBeforeStart < minWaitSamples)
            {
                samplesToWaitBeforeStart += masterSamplesPerBeat;
            }

            // Position slave deck to a beat boundary
            double slaveBeatDurationSeconds = 60.0 / slaveDeck.BPM;
            long slaveSamplesPerBeat = (long)(slaveBeatDurationSeconds * sampleRate * channels);

            double slavePosSeconds = slaveDeck.CurrentPosition.TotalSeconds - slaveDeck.BeatOffset;
            long slaveSamplePos = (long)(slavePosSeconds * sampleRate * channels);
            long slaveSamplesIntoBeat = slaveSamplePos % slaveSamplesPerBeat;
            if (slaveSamplesIntoBeat < 0) slaveSamplesIntoBeat += slaveSamplesPerBeat;

            // Move slave to next beat boundary
            long slaveSamplesToNextBeat = slaveSamplesPerBeat - slaveSamplesIntoBeat;
            double newSlavePosition = slaveDeck.CurrentPosition.TotalSeconds + (double)slaveSamplesToNextBeat / (sampleRate * channels);
            slaveDeck.SetPosition(TimeSpan.FromSeconds(newSlavePosition));

            // Debug output
            try
            {
                System.IO.File.AppendAllText("sync_debug.log",
                    $"[QUANTIZED PREP] Master@{masterPosSeconds:F3}s, WaitSamples={samplesToWaitBeforeStart} ({samplesToWaitBeforeStart / (double)(sampleRate * channels) * 1000:F0}ms)\n");
            }
            catch { }

            isWaitingForBeat = true;
            syncSlaveDeck = slaveDeck;
            syncMasterDeck = masterDeck;
        }

        /// <summary>
        /// Main mixing method - called by audio output thread
        /// This is where the magic happens!
        /// </summary>
        public int Read(float[] buffer, int offset, int count)
        {
            // Temporary buffers for each deck
            float[] deckABuffer = new float[count];
            float[] deckBBuffer = new float[count];

            // Handle quantized start waiting
            if (isWaitingForBeat && syncSlaveDeck != null)
            {
                if (samplesToWaitBeforeStart > 0)
                {
                    long samplesToRead = Math.Min(count, samplesToWaitBeforeStart);

                    // Read from master (and other deck if playing)
                    DeckA.Read(deckABuffer, 0, count);
                    DeckB.Read(deckBBuffer, 0, count);

                    // Don't start slave yet - keep it paused
                    if (syncSlaveDeck == DeckA)
                        Array.Clear(deckABuffer, 0, count);
                    else
                        Array.Clear(deckBBuffer, 0, count);

                    samplesToWaitBeforeStart -= samplesToRead;

                    // Mix and return
                    MixBuffers(buffer, offset, deckABuffer, deckBBuffer, count);
                    return count;
                }
                else
                {
                    // Time to start! Enable playback on slave
                    syncSlaveDeck.IsPlaying = true;
                    isWaitingForBeat = false;
                    // Fall through to normal mixing
                }
            }

            // Keep synced decks matched in tempo AND phase
            if (syncSlaveDeck != null && syncMasterDeck != null)
            {
                if (syncSlaveDeck.IsPlaying && syncMasterDeck.IsPlaying)
                {
                    // INITIAL SYNC: Calculate offset BEFORE first read
                    if (!initialSyncApplied)
                    {
                        CalculateAndApplyInitialSync(count);
                        initialSyncApplied = true;
                    }

                    // Keep tempo matched
                    double masterEffectiveBPM = syncMasterDeck.BPM * syncMasterDeck.Tempo;
                    double targetTempo = masterEffectiveBPM / syncSlaveDeck.BPM;
                    syncSlaveDeck.Tempo = targetTempo;

                    // Only check/correct when no correction is in progress
                    if (syncSlaveDeck.SyncOffsetSamples == 0)
                    {
                        CheckAndCorrectPhase();
                    }
                }
            }

            // Normal operation: read from both decks (slave will apply sync offset during read)
            DeckA.Read(deckABuffer, 0, count);
            DeckB.Read(deckBBuffer, 0, count);

            // Mix the buffers
            MixBuffers(buffer, offset, deckABuffer, deckBBuffer, count);

            return count;
        }

        /// <summary>
        /// Simple sync - copy master's position exactly
        /// Let the continuous correction handle fine-tuning
        /// </summary>
        private void CalculateAndApplyInitialSync(int bufferSize)
        {
            if (syncSlaveDeck == null || syncMasterDeck == null) return;

            // Just copy master's position - continuous correction will fine-tune
            double masterPos = syncMasterDeck.CurrentPosition.TotalSeconds;
            syncSlaveDeck.SetPosition(TimeSpan.FromSeconds(masterPos));
        }

        /// <summary>
        /// Continuously monitor and correct phase drift using sample offset
        /// This avoids seeking (which causes glitches) and uses direct sample manipulation
        /// </summary>
        private void CheckAndCorrectPhase()
        {
            if (syncSlaveDeck == null || syncMasterDeck == null) return;
            if (!syncSlaveDeck.IsPlaying || !syncMasterDeck.IsPlaying) return;

            // For SAME SONG test: just compare positions directly
            double masterPos = syncMasterDeck.CurrentPosition.TotalSeconds;
            double slavePos = syncSlaveDeck.CurrentPosition.TotalSeconds;
            double diff = masterPos - slavePos; // positive = slave is behind

            // If difference > 5ms, apply correction via sample offset
            if (Math.Abs(diff) > 0.005)
            {
                int sampleRate = WaveFormat.SampleRate;
                int channels = WaveFormat.Channels;

                // Convert time difference to samples
                // Negative offset = skip samples (slave catches up)
                // Positive offset = output silence (slave waits)
                long offsetSamples = (long)(diff * sampleRate * channels);

                // Apply incrementally to avoid large jumps
                long maxAdjustment = sampleRate * channels / 10; // max 100ms per callback
                if (Math.Abs(offsetSamples) > maxAdjustment)
                {
                    offsetSamples = offsetSamples > 0 ? maxAdjustment : -maxAdjustment;
                }

                syncSlaveDeck.SyncOffsetSamples = offsetSamples;
            }
            else
            {
                syncSlaveDeck.SyncOffsetSamples = 0; // Close enough, no correction needed
            }
        }

        /// <summary>
        /// Mix two buffers based on crossfader position
        /// </summary>
        private void MixBuffers(float[] output, int offset, float[] deckABuffer, float[] deckBBuffer, int count)
        {
            // Power crossfade curve for smooth transition
            float gainA = (float)Math.Cos(crossfaderPosition * Math.PI / 2);
            float gainB = (float)Math.Sin(crossfaderPosition * Math.PI / 2);

            for (int i = 0; i < count; i++)
            {
                output[offset + i] = (deckABuffer[i] * gainA) + (deckBBuffer[i] * gainB);
            }
        }
    }
}
