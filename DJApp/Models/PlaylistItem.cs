using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DJAutoMixApp.Models
{
    public class PlaylistItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (!Equals(field, value))
            {
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }

        private string filePath = string.Empty;
        public string FilePath
        {
            get => filePath;
            set => Set(ref filePath, value);
        }

        private string title = string.Empty;
        public string Title
        {
            get => title;
            set => Set(ref title, value);
        }

        private string artist = string.Empty;
        public string Artist
        {
            get => artist;
            set => Set(ref artist, value);
        }

        private TimeSpan duration;
        public TimeSpan Duration
        {
            get => duration;
            set => Set(ref duration, value);
        }

        private double bpm;
        public double BPM
        {
            get => bpm;
            set => Set(ref bpm, value);
        }

        private double beatOffset;
        public double BeatOffset
        {
            get => beatOffset;
            set => Set(ref beatOffset, value);
        }

        private TimeSpan mixInPoint;
        public TimeSpan MixInPoint
        {
            get => mixInPoint;
            set => Set(ref mixInPoint, value);
        }

        private TimeSpan mixOutPoint;
        public TimeSpan MixOutPoint
        {
            get => mixOutPoint;
            set => Set(ref mixOutPoint, value);
        }

        public PlaylistItem()
        {
        }

        public PlaylistItem(string filePath)
        {
            FilePath = filePath;
            Title = System.IO.Path.GetFileNameWithoutExtension(filePath);
        }
    }
}
