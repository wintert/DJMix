using System;
using System.Timers;

namespace DJAutoMixApp.Services
{
    /// <summary>
    /// Auto-mixing engine that orchestrates transitions between tracks
    /// </summary>
    public class AutoMixEngine : IDisposable
    {
        private readonly AudioDeck deckA;
        private readonly AudioDeck deckB;
        private readonly PlaylistManager playlistManager;
        private readonly BeatDetector beatDetector;
        private System.Timers.Timer? mixTimer;
        private System.Timers.Timer? tempoRecoveryTimer;
        private double recoveryStartTempo = 1.0;
        private const int TEMPO_RECOVERY_SECONDS = 30; // Time to return to original tempo
        
        private AudioDeck? activeDeck;
        private AudioDeck? nextDeck;
        private bool isMixing = false;
        private bool isAutoMixEnabled = false;
        private bool disposed = false;
        private int mixDurationSeconds = 10; // Duration of crossfade in seconds
        
        public event EventHandler<double>? CrossfaderPositionChanged;
        public event EventHandler<string>? StatusChanged;
        public event EventHandler? MixStarted;
        public event EventHandler? MixCompleted;
        public event EventHandler<double>? TempoRecoveryProgressChanged;
        public event EventHandler<Models.PlaylistItem>? TrackStarted;

        private double crossfaderPosition = 0; // 0 = Deck A, 100 = Deck B
        public double CrossfaderPosition
        {
            get => crossfaderPosition;
            set
            {
                crossfaderPosition = Math.Clamp(value, 0, 100);
                CrossfaderPositionChanged?.Invoke(this, crossfaderPosition);
                // Note: Actual C++ mixer_set_crossfader is called by MainViewModel
                // in response to CrossfaderPositionChanged to avoid double-setting.
            }
        }

        public bool IsAutoMixEnabled
        {
            get => isAutoMixEnabled;
            set
            {
                isAutoMixEnabled = value;
                if (value)
                    StartAutoMix();
                else
                    StopAutoMix();
            }
        }

        public int MixDurationSeconds
        {
            get => mixDurationSeconds;
            set => mixDurationSeconds = Math.Max(5, Math.Min(30, value));
        }

        public AutoMixEngine(AudioDeck deckA, AudioDeck deckB, PlaylistManager playlistManager, BeatDetector beatDetector)
        {
            this.deckA = deckA;
            this.deckB = deckB;
            this.playlistManager = playlistManager;
            this.beatDetector = beatDetector;

            // Subscribe to deck events
            deckA.PositionChanged += OnDeckPositionChanged;
            deckB.PositionChanged += OnDeckPositionChanged;
            deckA.TrackEnded += OnTrackEnded;
            deckB.TrackEnded += OnTrackEnded;

            // Initialize with Deck A as active
            activeDeck = deckA;
            nextDeck = deckB;
        }

        public void StartAutoMix()
        {
            if (playlistManager.Playlist.Count == 0)
            {
                StatusChanged?.Invoke(this, "No tracks in playlist");
                return;
            }

            // Load first track if nothing is loaded
            if (!activeDeck!.IsTrackLoaded)
            {
                LoadNextTrackOnActiveDeck();
            }

            // Start playback
            if (!activeDeck.IsPlaying)
            {
                activeDeck.Play();
            }
            
            // Notify UI of the current track (fixes first track metadata not showing)
            var currentTrack = playlistManager.CurrentTrack;
            if (currentTrack != null)
            {
                TrackStarted?.Invoke(this, currentTrack);
            }

            StatusChanged?.Invoke(this, "Auto-mix enabled");
        }

        public void StopAutoMix()
        {
            mixTimer?.Stop();
            isMixing = false;
            StatusChanged?.Invoke(this, "Auto-mix disabled");
        }

        /// <summary>
        /// Complete reset - stops playback and resets playlist to beginning
        /// </summary>
        public void ResetPlaybackState()
        {
            // Stop auto-mix
            StopAutoMix();
            isAutoMixEnabled = false;
            
            // Stop tempo recovery
            tempoRecoveryTimer?.Stop();
            tempoRecoveryTimer?.Dispose();
            tempoRecoveryTimer = null;
            
            // Stop both decks
            deckA.Stop();
            deckB.Stop();
            
            // Reset tempos
            deckA.Tempo = 1.0;
            deckB.Tempo = 1.0;
            
            // Reset playlist to beginning
            playlistManager.SetCurrentIndex(-1);
            
            // Reset deck references
            activeDeck = deckA;
            nextDeck = deckB;
            
            // Reset crossfader to Deck A side (0 position)
            CrossfaderPosition = 0;
            
            StatusChanged?.Invoke(this, "Stopped - Ready");
        }

        private void OnDeckPositionChanged(object? sender, TimeSpan position)
        {
            if (!isAutoMixEnabled || isMixing) return;

            var deck = sender as AudioDeck;
            if (deck == null || deck != activeDeck) return;

            // Check if we should start mixing
            var timeRemaining = deck.Duration - position;
            var mixStartTime = TimeSpan.FromSeconds(mixDurationSeconds + 5); // Start 5s buffer before crossfade

            if (timeRemaining <= mixStartTime && playlistManager.HasNext)
            {
                StartMixTransition();
            }
        }

        private void OnTrackEnded(object? sender, EventArgs e)
        {
            if (!isAutoMixEnabled) return;

            var deck = sender as AudioDeck;
            
            // If we're mixing and the OLD deck (activeDeck) ended, just complete the transition
            if (isMixing && deck == activeDeck)
            {
                // Force complete the transition immediately
                CompleteMixTransition();
                return;
            }
            
            // If we weren't mixing, advance playlist and play next track
            if (!isMixing)
            {
                if (playlistManager.HasNext)
                {
                    playlistManager.MoveNext();
                    LoadNextTrackOnActiveDeck();
                    activeDeck?.Play();
                    TrackStarted?.Invoke(this, playlistManager.CurrentTrack!);
                }
            }
        }

        private readonly object mixLock = new object();

        private void StartMixTransition()
        {
            lock (mixLock)
            {
                if (disposed) return;
                if (isMixing)
                {
                    DJAutoMixApp.App.Log("StartMix: Already mixing, skipping");
                    return;
                }
                isMixing = true;
            }

            // Load next track onto the inactive deck
            if (!LoadNextTrackOnNextDeck())
            {
                StatusChanged?.Invoke(this, "No more tracks in playlist");
                lock (mixLock) { isMixing = false; }
                return;
            }

            MixStarted?.Invoke(this, EventArgs.Empty);
            StatusChanged?.Invoke(this, "Mixing tracks...");

            var nextTrackItem = playlistManager.NextTrack;
            var currentTrack = playlistManager.CurrentTrack;

            try
            {
                if (nextTrackItem != null && nextTrackItem.BPM > 0 && nextDeck != null && activeDeck != null)
                {
                    var mixInPoint = beatDetector.CalculateMixInPoint(nextTrackItem.BPM, 8);

                    if (currentTrack != null && currentTrack.BPM > 0)
                    {
                        // Check if BPM gap is too large for a beat-synced mix
                        double maxBpm = Math.Max(currentTrack.BPM, nextTrackItem.BPM);
                        double minBpm = Math.Min(currentTrack.BPM, nextTrackItem.BPM);
                        double bpmRatio = maxBpm / minBpm;
                        
                        if (bpmRatio > 1.10)
                        {
                            DJAutoMixApp.App.Log($"BPM gap too large ({currentTrack.BPM:F0} vs {nextTrackItem.BPM:F0}), doing simple crossfade without sync");
                            StatusChanged?.Invoke(this, $"BPM gap too large ({currentTrack.BPM:F0} vs {nextTrackItem.BPM:F0}), crossfading without sync");
                            // Don't sync — each deck plays at its own tempo
                        }
                        else
                        {
                            var masterPosition = activeDeck.CurrentPosition.TotalSeconds;
                            var masterBeatPeriod = 60.0 / currentTrack.BPM;
                            var masterPhase = (masterPosition % masterBeatPeriod) / masterBeatPeriod;

                            var slaveBeatPeriod = 60.0 / nextTrackItem.BPM;
                            var slavePhase = (mixInPoint.TotalSeconds % slaveBeatPeriod) / slaveBeatPeriod;

                            var phaseDiff = masterPhase - slavePhase;
                            if (phaseDiff > 0.5) phaseDiff -= 1.0;
                            if (phaseDiff < -0.5) phaseDiff += 1.0;

                            var adjustment = phaseDiff * slaveBeatPeriod;
                            var alignedMixIn = mixInPoint.TotalSeconds + adjustment;
                            if (alignedMixIn < 0) alignedMixIn = slaveBeatPeriod + alignedMixIn;

                            mixInPoint = TimeSpan.FromSeconds(alignedMixIn);

                            nextDeck.EnableSync(activeDeck);
                            StatusChanged?.Invoke(this, $"Beat-syncing: {nextTrackItem.BPM:F1} → {currentTrack.BPM:F1} BPM");
                        }
                    }

                    nextDeck.SetPosition(mixInPoint);
                }

                // Set crossfader to correct starting position
                CrossfaderPosition = (activeDeck == deckA) ? 0 : 100;

                nextDeck?.Play();

                // Start crossfade timer
                var mixSteps = mixDurationSeconds * 10;
                var currentStep = 0;
                var capturedActiveDeck = activeDeck;

                mixTimer = new System.Timers.Timer(100);
                mixTimer.Elapsed += (s, e) =>
                {
                    bool shouldComplete = false;
                    lock (mixLock)
                    {
                        if (!isMixing || disposed) return;
                        currentStep++;
                        var progress = (double)currentStep / mixSteps;
                        CrossfaderPosition = (capturedActiveDeck == deckA)
                            ? progress * 100
                            : (1 - progress) * 100;
                        if (currentStep >= mixSteps)
                            shouldComplete = true;
                    }
                    if (shouldComplete)
                        CompleteMixTransition();
                };
                mixTimer.Start();
            }
            catch (Exception ex)
            {
                DJAutoMixApp.App.Log($"StartMix ERROR: {ex.Message}");
                lock (mixLock) { isMixing = false; }
            }
        }

        private void CompleteMixTransition()
        {
            lock (mixLock)
            {
                if (!isMixing || disposed) return;

                var timer = mixTimer;
                mixTimer = null;
                isMixing = false;
                timer?.Stop();
                timer?.Dispose();
            }

            var oldDeck = activeDeck;
            SwitchDecks();
            
            // Snap crossfader to the new active deck's side so it's fully audible
            CrossfaderPosition = (activeDeck == deckA) ? 0 : 100;
            
            oldDeck?.Stop();
            activeDeck?.DisableSync();
            StartTempoRecovery();
            playlistManager.MoveNext();

            MixCompleted?.Invoke(this, EventArgs.Empty);
            StatusChanged?.Invoke(this, $"Now playing: {playlistManager.CurrentTrack?.Title}");
        }

        private void SwitchDecks()
        {
            var temp = activeDeck;
            activeDeck = nextDeck;
            nextDeck = temp;
        }

        private void StartTempoRecovery()
        {
            // Stop any existing recovery timer
            tempoRecoveryTimer?.Stop();
            tempoRecoveryTimer?.Dispose();

            double currentTempo = AudioEngineInterop.deck_get_tempo(activeDeck == deckA ? 0 : 1);
            recoveryStartTempo = currentTempo;

            if (Math.Abs(currentTempo - 1.0) < 0.001)
                return;

            var recoverySteps = TEMPO_RECOVERY_SECONDS * 10;
            var currentStep = 0;

            tempoRecoveryTimer = new System.Timers.Timer(100);
            tempoRecoveryTimer.AutoReset = true;
            tempoRecoveryTimer.Elapsed += (s, e) =>
            {
                lock (mixLock)
                {
                    if (disposed) return;
                    currentStep++;
                    var progress = (double)currentStep / recoverySteps;
                    var newTempo = recoveryStartTempo + (1.0 - recoveryStartTempo) * progress;

                    if (activeDeck != null)
                        activeDeck.Tempo = newTempo;

                    TempoRecoveryProgressChanged?.Invoke(this, progress);

                    if (currentStep >= recoverySteps)
                    {
                        tempoRecoveryTimer?.Stop();
                        tempoRecoveryTimer?.Dispose();
                        tempoRecoveryTimer = null;
                        if (activeDeck != null)
                            activeDeck.Tempo = 1.0;
                    }
                }
            };
            tempoRecoveryTimer.Start();
        }

        private void LoadNextTrackOnActiveDeck()
        {
            var currentTrack = playlistManager.CurrentTrack ?? playlistManager.NextTrack;
            if (currentTrack != null)
            {
                LoadTrackOnDeck(activeDeck!, currentTrack);
                if (playlistManager.CurrentIndex < 0)
                    playlistManager.SetCurrentIndex(0);
            }
        }

        private bool LoadNextTrackOnNextDeck()
        {
            var nextTrack = playlistManager.NextTrack;
            if (nextTrack != null)
            {
                LoadTrackOnDeck(nextDeck!, nextTrack);
                return true;
            }
            return false;
        }

        private void LoadTrackOnDeck(AudioDeck deck, Models.PlaylistItem track)
        {
            try
            {
                // Analyze track if BPM not already detected
                if (track.BPM == 0)
                {
                    StatusChanged?.Invoke(this, $"Analyzing: {track.Title}");
                    var trackInfo = beatDetector.AnalyzeTrack(track.FilePath);
                    track.BPM = trackInfo.BPM;
                    track.BeatOffset = trackInfo.FirstBeatOffset;
                    track.Duration = trackInfo.Duration;
                    if (track.BPM > 0)
                    {
                        track.MixOutPoint = beatDetector.CalculateMixOutPoint(track.BPM, track.Duration, 16);
                        track.MixInPoint = beatDetector.CalculateMixInPoint(track.BPM, 8);
                    }
                }

                // Pass beat offset for accurate phase sync
                deck.LoadTrack(track.FilePath, track.BPM, track.BeatOffset);

                // If the playlist item has no BPM but the deck determined one (e.g. via
                // MiniBPM or default 120), sync it back so transitions can tempo-match.
                if (track.BPM == 0 && deck.BPM > 0)
                {
                    track.BPM = deck.BPM;
                    track.BeatOffset = deck.BeatOffset;
                    if (track.Duration == TimeSpan.Zero)
                        track.Duration = deck.Duration;
                }

                StatusChanged?.Invoke(this, $"Loaded: {track.Title} ({track.BPM:F1} BPM)");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"Error loading track: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            if (isAutoMixEnabled)
                StopAutoMix();

            mixTimer?.Stop();
            mixTimer?.Dispose();
            mixTimer = null;

            tempoRecoveryTimer?.Stop();
            tempoRecoveryTimer?.Dispose();
            tempoRecoveryTimer = null;

            // Unsubscribe from deck events
            deckA.PositionChanged -= OnDeckPositionChanged;
            deckB.PositionChanged -= OnDeckPositionChanged;
            deckA.TrackEnded -= OnTrackEnded;
            deckB.TrackEnded -= OnTrackEnded;

            // Reset state
            activeDeck = null;
            nextDeck = null;
            isAutoMixEnabled = false;
            isMixing = false;
        }
    }
}
