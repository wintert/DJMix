namespace DJAutoMixApp.Models
{
    /// <summary>
    /// Represents a track in the playlist
    /// </summary>
    public class PlaylistItem
    {
        public string FilePath { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public TimeSpan Duration { get; set; }
        public double BPM { get; set; }
        public double BeatOffset { get; set; } // First beat position in seconds
        public TimeSpan MixInPoint { get; set; }
        public TimeSpan MixOutPoint { get; set; }

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
