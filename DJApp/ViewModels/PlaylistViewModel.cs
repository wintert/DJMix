using DJAutoMixApp.Models;
using DJAutoMixApp.Services;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace DJAutoMixApp.ViewModels
{
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

        private bool isAnalyzing;
        public bool IsAnalyzing
        {
            get => isAnalyzing;
            set => SetProperty(ref isAnalyzing, value);
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

            playlistManager.PlaylistChanged += OnPlaylistChanged;
            playlistManager.TrackChanged += OnTrackChanged;

            AddTracksCommand = new RelayCommand(_ => AddTracks());
            RemoveTrackCommand = new RelayCommand(_ => RemoveTrack(), _ => SelectedTrack != null);
            ClearPlaylistCommand = new RelayCommand(_ => ClearPlaylist(), _ => Tracks.Count > 0);
            SavePlaylistCommand = new RelayCommand(_ => SavePlaylist(), _ => Tracks.Count > 0);
            LoadPlaylistCommand = new RelayCommand(_ => LoadPlaylist());
            ReAnalyzeBPMCommand = new RelayCommand(_ => _ = ReAnalyzeBPMAsync(), _ => SelectedTrack != null);
        }

        private void OnPlaylistChanged(object? sender, EventArgs e)
        {
            Tracks.Clear();
            foreach (var track in playlistManager.Playlist)
                Tracks.Add(track);
        }

        public async void HandleFileDrop(string[] filePaths)
        {
            foreach (var filePath in filePaths)
            {
                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                if (ext != ".mp3" && ext != ".wav" && ext != ".m4a" && ext != ".ogg" && ext != ".flac")
                    continue;

                var item = new PlaylistItem(filePath);

                // Add immediately (without BPM), then analyze in background
                playlistManager.AddTrack(item);
                await AnalyzeAndUpdateItem(item);
            }
        }

        private void OnTrackChanged(object? sender, PlaylistItem track)
        {
            CurrentTrackIndex = playlistManager.CurrentIndex;
        }

        private async void AddTracks()
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
                    playlistManager.AddTrack(item);
                    await AnalyzeAndUpdateItem(item);
                }
            }
        }

        private async Task AnalyzeAndUpdateItem(PlaylistItem item)
        {
            IsAnalyzing = true;
            try
            {
                var trackInfo = await Task.Run(() => beatDetector.AnalyzeTrack(item.FilePath));

                // UI will auto-update because PlaylistItem now implements INotifyPropertyChanged
                item.BPM = trackInfo.BPM;
                item.BeatOffset = trackInfo.FirstBeatOffset;
                item.Duration = trackInfo.Duration;

                if (trackInfo.BPM > 0)
                {
                    item.MixOutPoint = beatDetector.CalculateMixOutPoint(trackInfo.BPM, trackInfo.Duration, 16);
                    item.MixInPoint = beatDetector.CalculateMixInPoint(trackInfo.BPM, 8);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error analyzing track: {ex.Message}");
            }
            finally
            {
                IsAnalyzing = false;
            }
        }

        private void RemoveTrack()
        {
            if (SelectedTrack != null)
                playlistManager.RemoveTrack(SelectedTrack);
        }

        private void ClearPlaylist()
        {
            var result = MessageBox.Show(
                "Are you sure you want to clear the entire playlist?",
                "Clear Playlist",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
                playlistManager.Clear();
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

        private async Task ReAnalyzeBPMAsync()
        {
            if (SelectedTrack == null) return;

            var track = SelectedTrack;
            IsAnalyzing = true;

            try
            {
                double newBpm = await Task.Run(() => AudioEngineInterop.audio_analyze_bpm_from_file(track.FilePath));

                if (newBpm > 0)
                {
                    double oldBpm = track.BPM;
                    double offset = await Task.Run(() => AudioEngineInterop.audio_analyze_beat_offset_from_file(track.FilePath, newBpm));

                    // UI auto-updates via INotifyPropertyChanged
                    track.BPM = newBpm;
                    track.BeatOffset = offset;
                    track.MixOutPoint = beatDetector.CalculateMixOutPoint(newBpm, track.Duration, 16);
                    track.MixInPoint = beatDetector.CalculateMixInPoint(newBpm, 8);

                    MessageBox.Show($"BPM re-analyzed: {oldBpm:F1} → {newBpm:F1}", "BPM Analysis", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("BPM analysis failed - could not detect tempo.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error analyzing BPM: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzing = false;
            }
        }
    }
}
