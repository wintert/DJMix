using DJAutoMixApp.Models;
using DJAutoMixApp.Services;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;

namespace DJAutoMixApp.ViewModels
{
    /// <summary>
    /// ViewModel for playlist panel
    /// </summary>
    public class PlaylistViewModel : ViewModelBase
    {
        private readonly PlaylistManager playlistManager;
        private readonly BeatDetector beatDetector;

        public ObservableCollection<PlaylistItem> Tracks { get; }

        private PlaylistItem? selectedTrack;
        public PlaylistItem? SelectedTrack
        {
            get => selectedTrack;
            set => SetProperty(ref selectedTrack, value);
        }

        private int currentTrackIndex = -1;
        public int CurrentTrackIndex
        {
            get => currentTrackIndex;
            set => SetProperty(ref currentTrackIndex, value);
        }

        public RelayCommand AddTracksCommand { get; }
        public RelayCommand RemoveTrackCommand { get; }
        public RelayCommand ClearPlaylistCommand { get; }
        public RelayCommand SavePlaylistCommand { get; }
        public RelayCommand LoadPlaylistCommand { get; }
        public RelayCommand ReAnalyzeBPMCommand { get; }

        public PlaylistViewModel(PlaylistManager playlistManager, BeatDetector beatDetector)
        {
            this.playlistManager = playlistManager;
            this.beatDetector = beatDetector;
            
            Tracks = new ObservableCollection<PlaylistItem>();

            // Subscribe to events
            playlistManager.PlaylistChanged += OnPlaylistChanged;
            playlistManager.TrackChanged += OnTrackChanged;

            // Commands
            AddTracksCommand = new RelayCommand(_ => AddTracks());
            RemoveTrackCommand = new RelayCommand(_ => RemoveTrack(), _ => SelectedTrack != null);
            ClearPlaylistCommand = new RelayCommand(_ => ClearPlaylist(), _ => Tracks.Count > 0);
            SavePlaylistCommand = new RelayCommand(_ => SavePlaylist(), _ => Tracks.Count > 0);
            LoadPlaylistCommand = new RelayCommand(_ => LoadPlaylist());
            ReAnalyzeBPMCommand = new RelayCommand(_ => ReAnalyzeBPM(), _ => SelectedTrack != null);
        }

        private void OnPlaylistChanged(object? sender, EventArgs e)
        {
            Tracks.Clear();
            foreach (var track in playlistManager.Playlist)
            {
                Tracks.Add(track);
            }
        }

        /// <summary>
        /// Handles files dropped onto the playlist
        /// </summary>
        public void HandleFileDrop(string[] filePaths)
        {
            foreach (var filePath in filePaths)
            {
                // Check if it's an audio file
                var ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
                if (ext == ".mp3" || ext == ".wav" || ext == ".m4a" || ext == ".ogg" || ext == ".flac")
                {
                    var item = new PlaylistItem(filePath);
                    
                    try
                    {
                        var trackInfo = beatDetector.AnalyzeTrack(filePath);
                        item.BPM = trackInfo.BPM;
                        item.Duration = trackInfo.Duration;
                        item.MixOutPoint = beatDetector.CalculateMixOutPoint(item.BPM, item.Duration, 16);
                        item.MixInPoint = beatDetector.CalculateMixInPoint(item.BPM, 8);
                    }
                    catch
                    {
                        item.BPM = 120;
                    }

                    playlistManager.AddTrack(item);
                }
            }
        }

        private void OnTrackChanged(object? sender, PlaylistItem track)
        {
            CurrentTrackIndex = playlistManager.CurrentIndex;
        }

        private void AddTracks()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Audio Files|*.mp3;*.wav;*.m4a;*.ogg;*.flac|All Files|*.*",
                Multiselect = true,
                Title = "Add Tracks to Playlist"
            };

            if (dialog.ShowDialog() == true)
            {
                foreach (var filePath in dialog.FileNames)
                {
                    var item = new PlaylistItem(filePath);
                    
                    // Analyze in background (simplified - in production, use async/await)
                    try
                    {
                        var trackInfo = beatDetector.AnalyzeTrack(filePath);
                        item.BPM = trackInfo.BPM;
                        item.Duration = trackInfo.Duration;
                        item.MixOutPoint = beatDetector.CalculateMixOutPoint(item.BPM, item.Duration, 16);
                        item.MixInPoint = beatDetector.CalculateMixInPoint(item.BPM, 8);
                    }
                    catch
                    {
                        // Use defaults if analysis fails
                        item.BPM = 120;
                    }

                    playlistManager.AddTrack(item);
                }
            }
        }

        private void RemoveTrack()
        {
            if (SelectedTrack != null)
            {
                playlistManager.RemoveTrack(SelectedTrack);
            }
        }

        private void ClearPlaylist()
        {
            var result = MessageBox.Show(
                "Are you sure you want to clear the entire playlist?",
                "Clear Playlist",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                playlistManager.Clear();
            }
        }

        private void SavePlaylist()
        {
            var dialog = new SaveFileDialog
            {
                Filter = "Playlist Files|*.json|All Files|*.*",
                DefaultExt = ".json",
                Title = "Save Playlist"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    playlistManager.SaveToFile(dialog.FileName);
                    MessageBox.Show("Playlist saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error saving playlist: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void LoadPlaylist()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Playlist Files|*.json|All Files|*.*",
                Title = "Load Playlist"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    playlistManager.LoadFromFile(dialog.FileName);
                    MessageBox.Show("Playlist loaded successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error loading playlist: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        /// <summary>
        /// Re-analyze the selected track's BPM using the C++ audio engine (BTrack)
        /// </summary>
        private void ReAnalyzeBPM()
        {
            if (SelectedTrack == null) return;

            try
            {
                // Load track into a temporary deck for analysis
                int tempDeckId = 0; // Use deck A for analysis
                int result = AudioEngineInterop.deck_load_track(tempDeckId, SelectedTrack.FilePath);
                
                if (result == 0)
                {
                    // Analyze using C++ BTrack
                    double newBpm = AudioEngineInterop.audio_analyze_bpm(tempDeckId);
                    
                    if (newBpm > 0)
                    {
                        double oldBpm = SelectedTrack.BPM;
                        SelectedTrack.BPM = newBpm;
                        
                        // Re-calculate beat offset
                        double beatOffset = AudioEngineInterop.audio_analyze_beat_offset(tempDeckId, newBpm);
                        SelectedTrack.BeatOffset = beatOffset;
                        
                        // Re-calculate mix points
                        SelectedTrack.MixOutPoint = beatDetector.CalculateMixOutPoint(newBpm, SelectedTrack.Duration, 16);
                        SelectedTrack.MixInPoint = beatDetector.CalculateMixInPoint(newBpm, 8);
                        
                        // Force UI update
                        var index = Tracks.IndexOf(SelectedTrack);
                        if (index >= 0)
                        {
                            Tracks[index] = SelectedTrack;
                        }
                        
                        MessageBox.Show($"BPM re-analyzed: {oldBpm:F1} → {newBpm:F1}", "BPM Analysis", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show("BPM analysis failed - could not detect tempo.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    
                    // Unload temp deck
                    AudioEngineInterop.deck_unload_track(tempDeckId);
                }
                else
                {
                    MessageBox.Show("Failed to load track for analysis.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error analyzing BPM: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
