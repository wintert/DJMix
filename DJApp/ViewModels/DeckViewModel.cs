using DJAutoMixApp.Services;
using System;
using System.Windows;
using System.Windows.Media;

namespace DJAutoMixApp.ViewModels
{
    /// <summary>
    /// ViewModel for a single deck with EQ and volume controls
    /// </summary>
    public class DeckViewModel : ViewModelBase
    {
        private readonly AudioDeck deck;
        public AudioDeck AudioDeck => deck;
        
        private bool isSyncActive;
        public bool IsSyncActive
        {
            get => isSyncActive;
            set
            {
                if (isSyncActive != value)
                {
                    isSyncActive = value;
                    OnPropertyChanged(nameof(IsSyncActive));
                }
            }
        }

        public string DeckName => deck.DeckName;

        private string trackName = "No Track Loaded";
        public string TrackName
        {
            get => trackName;
            set => SetProperty(ref trackName, value);
        }

        private TimeSpan currentPosition;
        public TimeSpan CurrentPosition
        {
            get => currentPosition;
            set => SetProperty(ref currentPosition, value);
        }

        private TimeSpan duration;
        public TimeSpan Duration
        {
            get => duration;
            set => SetProperty(ref duration, value);
        }

        private bool isPlaying;
        public bool IsPlaying
        {
            get => isPlaying;
            set => SetProperty(ref isPlaying, value);
        }

        private double bpm;
        public double BPM
        {
            get => bpm;
            set => SetProperty(ref bpm, value);
        }

        /// <summary>
        /// The actual BPM being played (accounts for tempo adjustment)
        /// </summary>
        public double EffectiveBPM => deck.EffectiveBPM;

        // Volume (0-100 for UI, converted to 0-1 for audio)
        private double volume = 70;
        public double Volume
        {
            get => volume;
            set
            {
                if (SetProperty(ref volume, Math.Clamp(value, 0, 100)))
                {
                    deck.Volume = (float)(volume / 100.0);
                }
            }
        }

        // EQ Controls (0-100 for UI, where 50 = unity, 0 = kill, 100 = +6dB)
        private double eqLow = 50;
        public double EqLow
        {
            get => eqLow;
            set
            {
                if (SetProperty(ref eqLow, Math.Clamp(value, 0, 100)))
                {
                    deck.EqLow = (float)(eqLow / 50.0); // 0->0, 50->1, 100->2
                }
            }
        }

        private double eqMid = 50;
        public double EqMid
        {
            get => eqMid;
            set
            {
                if (SetProperty(ref eqMid, Math.Clamp(value, 0, 100)))
                {
                    deck.EqMid = (float)(eqMid / 50.0);
                }
            }
        }

        private double eqHigh = 50;
        public double EqHigh
        {
            get => eqHigh;
            set
            {
                if (SetProperty(ref eqHigh, Math.Clamp(value, 0, 100)))
                {
                    deck.EqHigh = (float)(eqHigh / 50.0);
                }
            }
        }

        public string FormattedPosition => $"{CurrentPosition:mm\\:ss}";
        public string FormattedDuration => $"{Duration:mm\\:ss}";
        public string FormattedTimeRemaining
        {
            get
            {
                var remaining = Duration - CurrentPosition;
                return remaining.TotalSeconds >= 0 ? $"-{remaining:mm\\:ss}" : "00:00";
            }
        }

        public double ProgressPercentage
        {
            get
            {
                if (Duration.TotalSeconds == 0) return 0;
                return (CurrentPosition.TotalSeconds / Duration.TotalSeconds) * 100;
            }
        }

        public RelayCommand PlayPauseCommand { get; }
        public RelayCommand SyncCommand { get; private set; } = null!;

        // Reference to the other deck for syncing
        public void InitializeSyncCommand(DeckViewModel otherDeck)
        {
            SyncCommand = new RelayCommand(
                _ => SyncToDeck(otherDeck),
                _ => deck.IsTrackLoaded && otherDeck.BPM > 0
            );
        }

        private void SyncToDeck(DeckViewModel otherDeck)
        {
            DJAutoMixApp.App.Log($"SyncToDeck: otherDeck.BPM={otherDeck.BPM}, deck.IsTrackLoaded={deck.IsTrackLoaded}, IsSyncActive={IsSyncActive}");
            
            if (otherDeck.BPM > 0 && deck.IsTrackLoaded)
            {
                if (IsSyncActive)
                {
                    // Disable Sync
                    DJAutoMixApp.App.Log("SyncToDeck: Disabling sync");
                    deck.DisableSync();
                    IsSyncActive = false;
                }
                else
                {
                    // Enable Continuous Sync
                    DJAutoMixApp.App.Log("SyncToDeck: Enabling sync");
                    deck.EnableSync(otherDeck.AudioDeck);
                    IsSyncActive = true;
                    
                    // Update displayed BPM immediately
                    BPM = otherDeck.BPM;
                }
            }
            else
            {
                DJAutoMixApp.App.Log("SyncToDeck: Conditions not met, sync not executed");
            }
        }

        public DeckViewModel(AudioDeck deck)
        {
            this.deck = deck;

            // Set initial volume
            deck.Volume = (float)(volume / 100.0);

            // Subscribe to events
            deck.PositionChanged += Deck_PositionChanged;

            deck.PlaybackStarted += (s, e) => IsPlaying = true;
            deck.PlaybackPaused += (s, e) => IsPlaying = false;
            deck.PlaybackStopped += (s, e) => IsPlaying = false;

            PlayPauseCommand = new RelayCommand(
                _ =>
                {
                    if (deck.IsPlaying)
                        deck.Pause();
                    else
                        deck.Play();
                },
                _ => deck.IsTrackLoaded
            );

            NudgeForwardCommand = new RelayCommand(
                _ => deck.Nudge(TimeSpan.FromMilliseconds(-50)),
                _ => deck.IsTrackLoaded
            );

            NudgeBackwardCommand = new RelayCommand(
                _ => deck.Nudge(TimeSpan.FromMilliseconds(50)),
                _ => deck.IsTrackLoaded
            );
        }
        
        public RelayCommand NudgeForwardCommand { get; }
        public RelayCommand NudgeBackwardCommand { get; }

        public void UpdateTrackInfo(string name, TimeSpan trackDuration, double trackBpm)
        {
            TrackName = name;
            Duration = trackDuration;
            BPM = trackBpm;
            OnPropertyChanged(nameof(FormattedDuration));
        }

        /// <summary>
        /// Load a track directly onto this deck
        /// </summary>
        private readonly Services.WaveformService waveformService = new Services.WaveformService();

        private PointCollection? waveformPoints;
        public PointCollection? WaveformPoints
        {
            get => waveformPoints;
            set
            {
                waveformPoints = value;
                OnPropertyChanged();
            }
        }

        private Geometry? beatGridGeometry;
        public Geometry? BeatGridGeometry
        {
            get => beatGridGeometry;
            set
            {
                beatGridGeometry = value;
                OnPropertyChanged(nameof(BeatGridGeometry));
            }
        }

        public async void LoadTrack(string filePath)
        {
            try
            {
                // Let's use BeatDetector here properly.
                var beatDetector = new Services.BeatDetector();
                var trackInfo = beatDetector.AnalyzeTrack(filePath); // This is slow-ish, potentially block UI? 
                // Should run analysis async too.

                await Task.Run(() => 
                {
                    deck.LoadTrack(filePath, trackInfo.BPM, trackInfo.FirstBeatOffset); 
                });
                
                // Update properties
                TrackName = System.IO.Path.GetFileNameWithoutExtension(filePath);
                Duration = deck.Duration;
                BPM = trackInfo.BPM;

                WaveformPoints = await waveformService.GenerateWaveformAsync(filePath);
                
                // Generate Beat Grid
                GenerateBeatGrid(trackInfo);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading track: {ex.Message}");
            }
        }

        private void GenerateBeatGrid(DJAutoMixApp.Models.TrackInfo trackInfo)
        {
            if (WaveformPoints == null || WaveformPoints.Count == 0 || trackInfo.Duration.TotalSeconds <= 0) return;

            var geometry = new GeometryGroup();
            double maxX = WaveformPoints.Count; 
            double duration = trackInfo.Duration.TotalSeconds;

            foreach (var beatTime in trackInfo.BeatPositions)
            {
                 double x = (beatTime / duration) * maxX;
                 // Create vertical line from 0 to 1
                 geometry.Children.Add(new LineGeometry(new Point(x, 0), new Point(x, 1)));
            }
            
            geometry.Freeze();
            BeatGridGeometry = geometry;
        }

        private void Deck_PositionChanged(object? sender, TimeSpan position)
        {
            CurrentPosition = position;
            OnPropertyChanged(nameof(FormattedPosition));
            OnPropertyChanged(nameof(FormattedTimeRemaining));
            OnPropertyChanged(nameof(EffectiveBPM)); // Update when tempo changes during sync
            if (Duration.TotalSeconds > 0)
            {
                Progress = position.TotalSeconds / Duration.TotalSeconds;
            }
        }

        private double progress;
        public double Progress
        {
            get => progress;
            set
            {
                progress = value;
                OnPropertyChanged();
            }
        }

        public void Reset()
        {
            TrackName = "No Track Loaded";
            CurrentPosition = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
            BPM = 0;
            IsPlaying = false;
        }
    }
}
