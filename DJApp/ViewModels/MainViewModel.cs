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

        private double crossfaderPosition = 50;
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

            // Initialize sync commands (each deck can sync to the other)
            DeckA.InitializeSyncCommand(DeckB);
            DeckB.InitializeSyncCommand(DeckA);

            // Subscribe to auto-mix events
            autoMixEngine.CrossfaderPositionChanged += (s, pos) =>
            {
                CrossfaderPosition = pos;
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

            ToggleAutoMixCommand = new RelayCommand(
                _ => IsAutoMixEnabled = !IsAutoMixEnabled,
                _ => Playlist.Tracks.Count > 0
            );
        }
    }
}
