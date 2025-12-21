using System;
using System.Timers;

namespace DJAutoMixApp.Services
{
    /// <summary>
    /// Controller for a single deck - wraps C++ audio engine
    /// Provides high-level C# API for UI
    /// </summary>
    public class AudioDeck
    {
        private System.Timers.Timer? positionTimer;
        private readonly int deckId;

        public string DeckName { get; private set; }
        public string? CurrentTrackPath { get; private set; }
        
        public TimeSpan CurrentPosition
        {
            get => TimeSpan.FromSeconds(AudioEngineInterop.deck_get_position(deckId));
            set => AudioEngineInterop.deck_set_position(deckId, value.TotalSeconds);
        }

        public TimeSpan Duration =>
            TimeSpan.FromSeconds(AudioEngineInterop.deck_get_duration(deckId));

        public bool IsPlaying => AudioEngineInterop.deck_is_playing(deckId) != 0;

        public double BPM
        {
            get => AudioEngineInterop.deck_get_bpm(deckId);
            set => AudioEngineInterop.deck_set_bpm(deckId, value);
        }

        public double BeatOffset
        {
            get; 
            set => AudioEngineInterop.deck_set_beat_offset(deckId, value);
        }

        public bool IsTrackLoaded => !string.IsNullOrEmpty(CurrentTrackPath);
        public double EffectiveBPM => BPM * Tempo;

        // Volume (0.0 to 1.0)
        public float Volume
        {
            get; 
            set => AudioEngineInterop.deck_set_volume(deckId, value);
        }

        // EQ Controls (0.0 to 2.0, 1.0 = neutral)
        public float EqLow
        {
            get; 
            set => AudioEngineInterop.deck_set_eq_low(deckId, value);
        }

        public float EqMid
        {
            get; 
            set => AudioEngineInterop.deck_set_eq_mid(deckId, value);
        }

        public float EqHigh
        {
            get; 
            set => AudioEngineInterop.deck_set_eq_high(deckId, value);
        }

        // Tempo adjustment (1.0 = normal speed)
        public double Tempo
        {
            get; 
            set => AudioEngineInterop.deck_set_tempo(deckId, value);
        }

        // Pitch adjustment (semitones)
        public double Pitch
        {
            get; 
            set => AudioEngineInterop.deck_set_pitch(deckId, value);
        }

        // Events
        public event EventHandler<TimeSpan>? PositionChanged;
        public event EventHandler? PlaybackStarted;
        public event EventHandler? PlaybackPaused;
        public event EventHandler? PlaybackStopped;
        public event EventHandler? TrackEnded;

        // Sync properties
        private AudioDeck? masterDeckRef;
        public bool IsSyncEnabled { get; private set; }

        public AudioDeck(int deckId)
        {
            this.deckId = deckId;
            DeckName = deckId == 0 ? "Deck A" : "Deck B";
            Volume = 1.0f;
            EqLow = 1.0f;
            EqMid = 1.0f;
            EqHigh = 1.0f;
            Tempo = 1.0;
            Pitch = 0.0;

            // Timer for position updates (UI feedback)
            positionTimer = new System.Timers.Timer(50);
            positionTimer.Elapsed += (s, e) =>
            {
                PositionChanged?.Invoke(this, CurrentPosition);
            };
        }

        public void LoadTrack(string filePath, double bpm = 0, double beatOffset = 0)
        {
            try
            {
                Stop();
                
                int result = AudioEngineInterop.deck_load_track(deckId, filePath);
                if (result == 0)
                {
                    CurrentTrackPath = filePath;
                    
                    // Prefer playlist metadata BPM if provided (more reliable than detection)
                    if (bpm > 0)
                    {
                        BPM = bpm;
                        // Still analyze beat offset using MiniBPM
                        double analyzedOffset = AudioEngineInterop.audio_analyze_beat_offset(deckId, bpm);
                        BeatOffset = analyzedOffset > 0 ? analyzedOffset : beatOffset;
                        DJAutoMixApp.App.Log($"BPM: deck={deckId}, Using playlist BPM={bpm:F1}, offset={BeatOffset:F3}s");
                    }
                    else
                    {
                        // No playlist BPM - use MiniBPM to analyze
                        double analyzedBpm = AudioEngineInterop.audio_analyze_bpm(deckId);
                        if (analyzedBpm > 0)
                        {
                            BPM = analyzedBpm;
                            double analyzedOffset = AudioEngineInterop.audio_analyze_beat_offset(deckId, analyzedBpm);
                            BeatOffset = analyzedOffset;
                            DJAutoMixApp.App.Log($"BPM Analysis: deck={deckId}, BPM={analyzedBpm:F1}, offset={analyzedOffset:F3}s");
                        }
                        else
                        {
                            // Default
                            BPM = 120;
                            BeatOffset = 0;
                            DJAutoMixApp.App.Log($"BPM: deck={deckId}, defaulting to 120 BPM");
                        }
                    }
                }
                else
                {
                    throw new Exception("Failed to load track in C++ engine");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading track on {DeckName}: {ex.Message}");
                throw;
            }
        }

        public void EnableSync(AudioDeck master)
        {
            if (master == this) return;

            masterDeckRef = master;
            IsSyncEnabled = true;

            // Enable sync in C++ engine (this deck is slave, master deck is master)
            AudioEngineInterop.sync_enable(deckId, master.deckId);
            
            DJAutoMixApp.App.Log($"EnableSync: slave={deckId}, master={master.deckId}, slaveIsPlaying={IsPlaying}, masterIsPlaying={master.IsPlaying}");
            
            // Do immediate initial alignment ONLY if slave is not playing (to avoid clicks)
            // This aligns the stopped deck to the playing master's current phase
            if (!IsPlaying && master.IsPlaying)
            {
                DJAutoMixApp.App.Log("EnableSync: Calling sync_align_now!");
                AudioEngineInterop.sync_align_now(deckId, master.deckId);
            }
        }

        public void DisableSync()
        {
            IsSyncEnabled = false;
            AudioEngineInterop.sync_disable(deckId);
            masterDeckRef = null;
        }

        public void Play()
        {
            DJAutoMixApp.App.Log($"Play: deck={deckId}, IsTrackLoaded={IsTrackLoaded}, IsSyncEnabled={IsSyncEnabled}, masterRef={(masterDeckRef != null ? masterDeckRef.deckId.ToString() : "null")}");
            
            if (IsTrackLoaded)
            {
                // If sync is enabled, use deck_play_synced which atomically sets position and starts playback
                if (IsSyncEnabled && masterDeckRef != null && masterDeckRef.IsPlaying)
                {
                    DJAutoMixApp.App.Log($"Play: Using deck_play_synced to start at master position");
                    AudioEngineInterop.deck_play_synced(deckId, masterDeckRef.deckId);
                }
                else
                {
                    AudioEngineInterop.deck_play(deckId);
                }
                
                positionTimer?.Start();
                PlaybackStarted?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Pause()
        {
            AudioEngineInterop.deck_pause(deckId);
            positionTimer?.Stop();
            PlaybackPaused?.Invoke(this, EventArgs.Empty);
        }

        public void Stop()
        {
            AudioEngineInterop.deck_stop(deckId);
            positionTimer?.Stop();
            PlaybackStopped?.Invoke(this, EventArgs.Empty);
        }

        public void SetPosition(TimeSpan position)
        {
            AudioEngineInterop.deck_set_position(deckId, position.TotalSeconds);
        }

        public void MatchBPM(double targetBPM)
        {
            if (BPM <= 0) return;
            double factor = targetBPM / BPM;
            Tempo = factor;
        }

        // User nudge offset - kept for API compatibility
        public TimeSpan UserSyncOffset { get; set; } = TimeSpan.Zero;

        public void Nudge(TimeSpan amount)
        {
            var newPos = CurrentPosition + amount;
            if (newPos >= TimeSpan.Zero && newPos < Duration)
            {
                SetPosition(newPos);
            }
        }
    }
}
