using DJAutoMixApp.Services;
using System;

namespace DJAutoMixApp.ViewModels
{
    /// <summary>
    /// Main application ViewModel
    /// </summary>
    public class MainViewModel : ViewModelBase
    {
        private readonly AutoMixEngine autoMixEngine;

        public DeckViewModel DeckA { get; }
        public DeckViewModel DeckB { get; }
        public PlaylistViewModel Playlist { get; }

        private double crossfaderPosition = 0;  // Start at Deck A
        public double CrossfaderPosition
        {
            get => crossfaderPosition;
            set
            {
                if (SetProperty(ref crossfaderPosition, value))
                {
                    // Update C++ engine's mixer directly (0-100 UI range -> 0.0-1.0 mixer range)
                    AudioEngineInterop.mixer_set_crossfader((float)(value / 100.0));
                    // Also notify automix for its internal state
                    autoMixEngine.CrossfaderPosition = value;
                }
            }
        }

        // Set crossfader without re-entering through ViewModel property (used by auto-mix events)
        public void SetCrossfaderInternal(double position)
        {
            position = Math.Clamp(position, 0, 100);
            SetProperty(ref crossfaderPosition, position);
            AudioEngineInterop.mixer_set_crossfader((float)(position / 100.0));
        }

        private bool isAutoMixEnabled;
        public bool IsAutoMixEnabled
        {
            get => isAutoMixEnabled;
            set
            {
                if (SetProperty(ref isAutoMixEnabled, value))
                {
                    autoMixEngine.IsAutoMixEnabled = value;
                }
            }
        }

        private string statusMessage = "Ready";
        public string StatusMessage
        {
            get => statusMessage;
            set => SetProperty(ref statusMessage, value);
        }

        private string currentTrackName = "No Track Playing";
        public string CurrentTrackName
        {
            get => currentTrackName;
            set => SetProperty(ref currentTrackName, value);
        }

        // Crossfade duration in seconds (5-30)
        private int crossfadeDuration = 10;
        public int CrossfadeDuration
        {
            get => crossfadeDuration;
            set
            {
                if (SetProperty(ref crossfadeDuration, Math.Clamp(value, 5, 30)))
                {
                    autoMixEngine.MixDurationSeconds = crossfadeDuration;
                }
            }
        }

        // Tempo Recovery
        private double tempoRecoveryProgress;
        public double TempoRecoveryProgress
        {
            get => tempoRecoveryProgress;
            set => SetProperty(ref tempoRecoveryProgress, value);
        }

        private bool isTempoRecoveryActive;
        public bool IsTempoRecoveryActive
        {
            get => isTempoRecoveryActive;
            set => SetProperty(ref isTempoRecoveryActive, value);
        }

        // Active Deck (for Single Player View)
        private DeckViewModel activeDeckViewModel;
        public DeckViewModel ActiveDeckViewModel
        {
            get => activeDeckViewModel;
            set => SetProperty(ref activeDeckViewModel, value);
        }

        public RelayCommand ToggleAutoMixCommand { get; }

        public MainViewModel(
            AudioDeck deckA,
            AudioDeck deckB,
            PlaylistManager playlistManager,
            BeatDetector beatDetector,
            AutoMixEngine autoMixEngine)
        {
            this.autoMixEngine = autoMixEngine;

            // Initialize C++ engine's mixer crossfader to middle position (UI is 0-100, mixer is 0.0-1.0)
            AudioEngineInterop.mixer_set_crossfader(0.5f);

            // Create ViewModels
            DeckA = new DeckViewModel(deckA);
            DeckB = new DeckViewModel(deckB);
            Playlist = new PlaylistViewModel(playlistManager, beatDetector);
            
            // Default to Deck A
            ActiveDeckViewModel = DeckA;

            // Initialize sync commands (each deck can sync to the other)
            DeckA.InitializeSyncCommand(DeckB);
            DeckB.InitializeSyncCommand(DeckA);

            // Subscribe to auto-mix events
            autoMixEngine.CrossfaderPositionChanged += (s, pos) =>
            {
                // Use internal setter to avoid re-entering autoMixEngine.CrossfaderPosition
                SetCrossfaderInternal(pos);
                
                // Switch active deck view based on crossfader dominance
                if (pos < 50 && ActiveDeckViewModel != DeckA)
                {
                    ActiveDeckViewModel = DeckA;
                }
                else if (pos > 50 && ActiveDeckViewModel != DeckB)
                {
                    ActiveDeckViewModel = DeckB;
                }
            };

            autoMixEngine.StatusChanged += (s, status) =>
            {
                StatusMessage = status;
            };

            autoMixEngine.MixStarted += (s, e) =>
            {
                StatusMessage = "Mixing tracks...";
            };

            autoMixEngine.MixCompleted += (s, e) =>
            {
                var currentTrack = playlistManager.CurrentTrack;
                if (currentTrack != null)
                {
                    CurrentTrackName = currentTrack.Title;
                    
                    // Update deck info
                    var activeDeck = autoMixEngine.CrossfaderPosition < 50 ? DeckA : DeckB;
                    activeDeck.UpdateTrackInfo(currentTrack.Title, currentTrack.Duration, currentTrack.BPM);
                }
            };

            autoMixEngine.TempoRecoveryProgressChanged += (s, progress) =>
            {
                // Update on UI thread
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    IsTempoRecoveryActive = progress < 1.0;
                    TempoRecoveryProgress = progress * 100; // Convert 0-1 to percentage
                    
                    if (progress >= 1.0)
                    {
                        StatusMessage = "Tempo Recovery Complete";
                    }
                    else
                    {
                        StatusMessage = $"Recovering Tempo: {progress:P0}";
                    }
                });
            };

            autoMixEngine.TrackStarted += (s, track) =>
            {
                // Update the active deck's info when a new track starts
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    // Determine which deck is active based on crossfader
                    var activeDeck = CrossfaderPosition < 50 ? DeckA : DeckB;
                    activeDeck.UpdateTrackInfo(track.Title, track.Duration, track.BPM);
                    ActiveDeckViewModel = activeDeck;
                    CurrentTrackName = track.Title;
                });
            };

            ToggleAutoMixCommand = new RelayCommand(
                _ => IsAutoMixEnabled = !IsAutoMixEnabled,
                _ => Playlist.Tracks.Count > 0
            );

            StopAllCommand = new RelayCommand(_ =>
            {
                // Use the engine's complete reset function
                autoMixEngine.ResetPlaybackState();
                
                // Update UI state
                IsAutoMixEnabled = false;
                
                // Clear deck displays
                DeckA.Reset();
                DeckB.Reset();
                
                // Reset active deck view to Deck A
                ActiveDeckViewModel = DeckA;
                
                StatusMessage = "Stopped - Ready";
            });
        }

        public RelayCommand StopAllCommand { get; }
    }
}
