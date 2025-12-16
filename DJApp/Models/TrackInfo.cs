namespace DJAutoMixApp.Models
{
    /// <summary>
    /// Extended track information from beat analysis
    /// </summary>
    public class TrackInfo
    {
        public double BPM { get; set; }
        public double BPMConfidence { get; set; }
        /// <summary>
        /// Time in seconds of the first beat (offset)
        /// </summary>
        public double FirstBeatOffset { get; set; }
        public List<double> BeatPositions { get; set; } = new List<double>();
        public TimeSpan Duration { get; set; }
        public string FilePath { get; set; } = string.Empty;
    }
}
