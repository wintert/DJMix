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
        
        private AudioDeck? activeDeck;
        private AudioDeck? nextDeck;
        private bool isMixing = false;
        private bool isAutoMixEnabled = false;
        private int mixDurationSeconds = 10; // Duration of crossfade in seconds
        
        public event EventHandler<double>? CrossfaderPositionChanged;
        public event EventHandler<string>? StatusChanged;
        public event EventHandler? MixStarted;
        public event EventHandler? MixCompleted;

        private double crossfaderPosition = 0; // 0 = Deck A, 100 = Deck B
        public double CrossfaderPosition
        {
            get => crossfaderPosition;
            set
            {
                crossfaderPosition = Math.Clamp(value, 0, 100);
                UpdateDeckVolumes();
                CrossfaderPositionChanged?.Invoke(this, crossfaderPosition);
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

            StatusChanged?.Invoke(this, "Auto-mix enabled");
        }

        public void StopAutoMix()
        {
            mixTimer?.Stop();
            isMixing = false;
            StatusChanged?.Invoke(this, "Auto-mix disabled");
        }

        private void OnDeckPositionChanged(object? sender, TimeSpan position)
        {
            if (!isAutoMixEnabled || isMixing) return;

            var deck = sender as AudioDeck;
            if (deck == null || deck != activeDeck) return;

            // Check if we should start mixing
            var timeRemaining = deck.Duration - position;
            var mixStartTime = TimeSpan.FromSeconds(mixDurationSeconds + 2); // Start 2 seconds before actual mix

            if (timeRemaining <= mixStartTime && playlistManager.HasNext)
            {
                StartMixTransition();
            }
        }

        private void OnTrackEnded(object? sender, EventArgs e)
        {
            if (!isAutoMixEnabled) return;

            // If we weren't mixing, just move to next track
            if (!isMixing)
            {
                SwitchDecks();
                LoadNextTrackOnActiveDeck();
                activeDeck?.Play();
            }
        }

        private void StartMixTransition()
        {
            if (isMixing) return;

            // Load next track onto the inactive deck
            if (!LoadNextTrackOnNextDeck())
            {
                StatusChanged?.Invoke(this, "No more tracks in playlist");
                return;
            }

            isMixing = true;
            MixStarted?.Invoke(this, EventArgs.Empty);
            StatusChanged?.Invoke(this, "Mixing tracks...");

            // Calculate mix start point for next track (8 bars in)
            var nextTrackItem = playlistManager.NextTrack;
            var currentTrack = playlistManager.CurrentTrack;

            if (nextTrackItem != null && nextTrackItem.BPM > 0 && nextDeck != null && activeDeck != null)
            {
                // First seek to mix-in point
                var mixInPoint = beatDetector.CalculateMixInPoint(nextTrackItem.BPM, 8);
                nextDeck.SetPosition(mixInPoint);

                // Enable sync to active deck - this will match BPM AND align phase
                if (currentTrack != null && currentTrack.BPM > 0)
                {
                    nextDeck.EnableSync(activeDeck);
                    StatusChanged?.Invoke(this, $"Beat-syncing: {nextTrackItem.BPM:F1} → {currentTrack.BPM:F1} BPM");
                }
            }

            // Start next track - Play() will call SyncToPhase with latency compensation
            nextDeck?.Play();

            // Start crossfade
            var mixSteps = mixDurationSeconds * 10; // Update 10 times per second
            var currentStep = 0;

            mixTimer = new System.Timers.Timer(100); // 100ms interval
            mixTimer.Elapsed += (s, e) =>
            {
                currentStep++;
                var progress = (double)currentStep / mixSteps;

                // Update crossfader position
                if (activeDeck == deckA)
                {
                    CrossfaderPosition = progress * 100; // Move from 0 to 100
                }
                else
                {
                    CrossfaderPosition = (1 - progress) * 100; // Move from 100 to 0
                }

                if (currentStep >= mixSteps)
                {
                    CompleteMixTransition();
                }
            };
            mixTimer.Start();
        }

        private void CompleteMixTransition()
        {
            mixTimer?.Stop();
            mixTimer?.Dispose();
            mixTimer = null;

            // Stop the old deck
            var oldDeck = activeDeck;

            // Switch decks
            SwitchDecks();

            oldDeck?.Stop();

            // Disable sync on the new active deck (it's now the master)
            activeDeck?.DisableSync();

            // Move to next track in playlist
            playlistManager.MoveNext();

            isMixing = false;
            MixCompleted?.Invoke(this, EventArgs.Empty);
            StatusChanged?.Invoke(this, $"Now playing: {playlistManager.CurrentTrack?.Title}");
        }

        private void SwitchDecks()
        {
            var temp = activeDeck;
            activeDeck = nextDeck;
            nextDeck = temp;
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
                    track.MixOutPoint = beatDetector.CalculateMixOutPoint(track.BPM, track.Duration, 16);
                    track.MixInPoint = beatDetector.CalculateMixInPoint(track.BPM, 8);
                }

                // Pass beat offset for accurate phase sync
                deck.LoadTrack(track.FilePath, track.BPM, track.BeatOffset);
                StatusChanged?.Invoke(this, $"Loaded: {track.Title} ({track.BPM:F1} BPM)");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"Error loading track: {ex.Message}");
            }
        }

        private void UpdateDeckVolumes()
        {
            // Update C++ engine's mixer crossfader directly
            AudioEngineInterop.mixer_set_crossfader((float)(crossfaderPosition / 100.0)); // 0-100 -> 0.0-1.0
        }

        public void Dispose()
        {
            mixTimer?.Stop();
            mixTimer?.Dispose();
        }
    }
}
