using DJAutoMixApp.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DJAutoMixApp.Services
{
    /// <summary>
    /// Manages the playlist of tracks
    /// </summary>
    public class PlaylistManager
    {
        private List<PlaylistItem> playlist = new List<PlaylistItem>();
        private int currentIndex = -1;

        public IReadOnlyList<PlaylistItem> Playlist => playlist.AsReadOnly();
        public int CurrentIndex => currentIndex;
        public PlaylistItem? CurrentTrack => currentIndex >= 0 && currentIndex < playlist.Count 
            ? playlist[currentIndex] 
            : null;
        public PlaylistItem? NextTrack => currentIndex + 1 < playlist.Count 
            ? playlist[currentIndex + 1] 
            : null;

        public bool HasNext => currentIndex + 1 < playlist.Count;
        public bool HasPrevious => currentIndex > 0;

        public event EventHandler? PlaylistChanged;
        public event EventHandler<PlaylistItem>? TrackChanged;

        public void AddTrack(PlaylistItem item)
        {
            playlist.Add(item);
            PlaylistChanged?.Invoke(this, EventArgs.Empty);
        }

        public void AddTracks(IEnumerable<PlaylistItem> items)
        {
            playlist.AddRange(items);
            PlaylistChanged?.Invoke(this, EventArgs.Empty);
        }

        public void RemoveTrack(int index)
        {
            if (index >= 0 && index < playlist.Count)
            {
                playlist.RemoveAt(index);
                
                // Adjust current index if needed
                if (currentIndex >= playlist.Count)
                    currentIndex = playlist.Count - 1;
                
                PlaylistChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void RemoveTrack(PlaylistItem item)
        {
            var index = playlist.IndexOf(item);
            if (index >= 0)
                RemoveTrack(index);
        }

        public void Clear()
        {
            playlist.Clear();
            currentIndex = -1;
            PlaylistChanged?.Invoke(this, EventArgs.Empty);
        }

        public void MoveTrack(int fromIndex, int toIndex)
        {
            if (fromIndex >= 0 && fromIndex < playlist.Count &&
                toIndex >= 0 && toIndex < playlist.Count)
            {
                var item = playlist[fromIndex];
                playlist.RemoveAt(fromIndex);
                playlist.Insert(toIndex, item);

                // Update current index if needed
                if (currentIndex == fromIndex)
                    currentIndex = toIndex;
                else if (fromIndex < currentIndex && toIndex >= currentIndex)
                    currentIndex--;
                else if (fromIndex > currentIndex && toIndex <= currentIndex)
                    currentIndex++;

                PlaylistChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public void MoveNext()
        {
            if (HasNext)
            {
                currentIndex++;
                TrackChanged?.Invoke(this, CurrentTrack!);
            }
        }

        public void MovePrevious()
        {
            if (HasPrevious)
            {
                currentIndex--;
                TrackChanged?.Invoke(this, CurrentTrack!);
            }
        }

        public void SetCurrentIndex(int index)
        {
            if (index >= -1 && index < playlist.Count)
            {
                currentIndex = index;
                if (currentIndex >= 0)
                    TrackChanged?.Invoke(this, CurrentTrack!);
            }
        }

        public void SaveToFile(string filePath)
        {
            try
            {
                var json = JsonConvert.SerializeObject(playlist, Formatting.Indented);
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving playlist: {ex.Message}");
                throw;
            }
        }

        public void LoadFromFile(string filePath)
        {
            try
            {
                var json = File.ReadAllText(filePath);
                var loadedPlaylist = JsonConvert.DeserializeObject<List<PlaylistItem>>(json);
                
                if (loadedPlaylist != null)
                {
                    playlist = loadedPlaylist;
                    currentIndex = -1;
                    PlaylistChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading playlist: {ex.Message}");
                throw;
            }
        }
    }
}
