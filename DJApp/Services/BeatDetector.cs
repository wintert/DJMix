using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DJAutoMixApp.Services
{
    /// <summary>
    /// Beat detection service that analyzes audio files to detect BPM and beat positions
    /// Uses Hybrid approach: Low-Pass Filter -> Adaptive Peak Detection -> Interval Histogram
    /// </summary>
    public class BeatDetector
    {
        private const int SAMPLE_RATE = 44100;
        
        /// <summary>
        /// Analyzes an audio file and detects its BPM
        /// </summary>
        public Models.TrackInfo AnalyzeTrack(string filePath)
        {
            var trackInfo = new Models.TrackInfo
            {
                FilePath = filePath
            };

            try
            {
                using (var reader = new AudioFileReader(filePath))
                {
                    trackInfo.Duration = reader.TotalTime;
                    
                    // Analyze a sample (first 60 seconds)
                    // Longer analysis = More accurate histogram
                    var analysisLength = Math.Min(60, (int)reader.TotalTime.TotalSeconds);
                    var (bpm, offset) = DetectBPM(reader, analysisLength);
                    
                    trackInfo.BPM = bpm;
                    trackInfo.BPMConfidence = 0.85; 
                    trackInfo.FirstBeatOffset = offset;
                    
                    // Calculate beat positions based on detected BPM
                    trackInfo.BeatPositions = GenerateBeatGrid(bpm, reader.TotalTime.TotalSeconds, offset);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Beat detection error: {ex.Message}");
                trackInfo.BPM = 120; // Default BPM
                trackInfo.BPMConfidence = 0.0;
            }

            return trackInfo;
        }

        /// <summary>
        /// Detects BPM and First Beat Offset
        /// </summary>
        private (double bpm, double offset) DetectBPM(AudioFileReader reader, int analysisLengthSeconds)
        {
            // 1. Read Audio
            int secondsToRead = Math.Min(analysisLengthSeconds, (int)reader.TotalTime.TotalSeconds);
            var samplesToRead = SAMPLE_RATE * secondsToRead * reader.WaveFormat.Channels;
            var buffer = new float[samplesToRead];
            var samplesRead = reader.Read(buffer, 0, samplesToRead);

            // 2. Convert to Mono
            var monoSamples = ConvertToMono(buffer, samplesRead, reader.WaveFormat.Channels);

            // 3. Low-Pass Filter (150Hz) - Isolate Kicks
            // Helps significantly with preventing high-hats from confusing the detector
            ApplyLowPassFilter(monoSamples, 150, SAMPLE_RATE);

            // 4. Calculate Energy Envelope
            var energyEnvelope = CalculateEnergyEnvelope(monoSamples);

            // 5. Adaptive Peak Detection
            var peaks = DetectPeaksAdaptive(energyEnvelope);

            // 6. Interval Histogram BPM
            var bpm = CalculateBPMFromIntervals(peaks);

            // 7. Calculate offset using phase-aligned first beat detection
            // Instead of just taking first peak, find the beat position that best aligns
            // with the detected BPM grid
            double offset = CalculateFirstBeatOffset(peaks, bpm);

            return (bpm, offset);
        }

        private float[] ConvertToMono(float[] buffer, int samplesRead, int channels)
        {
            if (channels == 1)
                return buffer.Take(samplesRead).ToArray();

            var monoSamples = new float[samplesRead / channels];
            for (int i = 0; i < monoSamples.Length; i++)
            {
                monoSamples[i] = (buffer[i * channels] + buffer[i * channels + 1]) / 2f;
            }
            return monoSamples;
        }

        private void ApplyLowPassFilter(float[] samples, float cutoff, int sampleRate)
        {
            float alpha = 2 * MathF.PI * cutoff / sampleRate; // Simple One-Pole
            float lastVal = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                lastVal += alpha * (samples[i] - lastVal);
                samples[i] = lastVal;
            }
        }

        private List<double> CalculateEnergyEnvelope(float[] samples)
        {
            var energyEnvelope = new List<double>();
            int windowSize = 1024; 
            int hopSize = 512;

            for (int i = 0; i < samples.Length - windowSize; i += hopSize)
            {
                double energy = 0;
                for (int j = 0; j < windowSize; j++)
                {
                    float s = samples[i + j];
                    energy += s * s;
                }
                energyEnvelope.Add(Math.Sqrt(energy / windowSize));
            }

            return energyEnvelope;
        }

        private List<int> DetectPeaksAdaptive(List<double> envelope)
        {
            var peaks = new List<int>();
            int window = 43; // ~0.5s window at 86Hz (44100/512)
            
            for (int i = window; i < envelope.Count - window; i++)
            {
                // Calculate local average around i
                double localSum = 0;
                for (int j = i - window; j <= i + window; j++)
                {
                    localSum += envelope[j];
                }
                double localAvg = localSum / (2 * window + 1);
                double threshold = localAvg * 1.3; // 1.3x local average

                if (envelope[i] > threshold &&
                    envelope[i] > envelope[i - 1] &&
                    envelope[i] > envelope[i + 1]) // Local Maxima
                {
                    peaks.Add(i);
                }
            }

            return peaks;
        }

        /// <summary>
        /// Calculate the first beat offset by finding the phase that best aligns with detected peaks
        /// </summary>
        private double CalculateFirstBeatOffset(List<int> peaks, double bpm)
        {
            if (peaks.Count < 4 || bpm <= 0) return 0;

            // Convert BPM to beat interval in peak indices (hop size = 512)
            double beatIntervalSamples = (60.0 / bpm) * SAMPLE_RATE;
            double beatIntervalPeaks = beatIntervalSamples / 512.0;

            // Only consider peaks in the first 10 seconds as candidates for first beat
            int maxFirstBeatIndex = (int)((10.0 * SAMPLE_RATE) / 512.0);
            var earlyCandidates = peaks.Where(p => p < maxFirstBeatIndex).Take(20).ToList();

            if (earlyCandidates.Count == 0) return 0;

            double bestOffset = 0;
            double bestScore = double.MaxValue;

            // For each early peak, calculate how well the beat grid aligns with all peaks
            foreach (var candidate in earlyCandidates)
            {
                double candidateTime = (candidate * 512.0) / SAMPLE_RATE;
                double totalError = 0;
                int matchCount = 0;

                // Check alignment with subsequent peaks (first 30 seconds worth)
                foreach (var peak in peaks.Take(100))
                {
                    double peakTime = (peak * 512.0) / SAMPLE_RATE;
                    double timeSinceCandidate = peakTime - candidateTime;

                    if (timeSinceCandidate < 0) continue;

                    // Calculate distance to nearest beat grid line
                    double beatDuration = 60.0 / bpm;
                    double beatsElapsed = timeSinceCandidate / beatDuration;
                    double fractionalBeat = beatsElapsed - Math.Round(beatsElapsed);
                    double error = Math.Abs(fractionalBeat) * beatDuration; // Error in seconds

                    // Only count peaks that are reasonably close to a beat
                    if (error < beatDuration * 0.25)
                    {
                        totalError += error;
                        matchCount++;
                    }
                }

                // Score: lower is better (more matches with less total error)
                if (matchCount > 0)
                {
                    double score = totalError / matchCount - (matchCount * 0.001);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestOffset = candidateTime;
                    }
                }
            }

            return bestOffset;
        }

        private double CalculateBPMFromIntervals(List<int> peaks)
        {
            if (peaks.Count < 2) return 120;

            var intervals = new List<int>();
            for (int i = 1; i < peaks.Count; i++)
            {
                intervals.Add(peaks[i] - peaks[i - 1]);
            }

            // Group intervals (with tolerance of +/- 2)
            // Bin size 5 makes sense for 86Hz sampling (5 samples = ~60ms)
            var groups = intervals
                .GroupBy(x => (int)Math.Round(x / 3.0) * 3) 
                .OrderByDescending(g => g.Count())
                .Take(3) // Check top 3 candidates
                .ToList();

            if (!groups.Any()) return 120;

            // Pick the best group that fits our range bias (70-140)
            double bestBpm = 120;
            int maxEvidence = -1;

            foreach (var group in groups)
            {
                double avgInterval = group.Average();
                double intervalSec = (avgInterval * 512.0) / SAMPLE_RATE;
                double bpm = 60.0 / intervalSec;

                // Normalize BPM
                while (bpm < 70) bpm *= 2;
                while (bpm > 140) bpm /= 2;

                // Weight score by count
                // If we folded (doubled/halved), we essentially map to this BPM
                // Simple logic: Take the First one (highest count)
                if (maxEvidence == -1)
                {
                    bestBpm = bpm;
                    maxEvidence = group.Count();
                }
            }

            return Math.Round(bestBpm, 3); // 3 decimal precision for tight sync
        }

        private List<double> GenerateBeatGrid(double bpm, double durationSeconds, double offset = 0)
        {
            var beatGrid = new List<double>();
            var beatInterval = 60.0 / bpm; 

            for (double time = offset; time < durationSeconds; time += beatInterval)
            {
                beatGrid.Add(time);
            }

            return beatGrid;
        }

        public TimeSpan CalculateMixOutPoint(double bpm, TimeSpan duration, int barsBeforeEnd = 16)
        {
            var beatDuration = 60.0 / bpm;
            var barDuration = beatDuration * 4; 
            var mixOutTime = duration.TotalSeconds - (barDuration * barsBeforeEnd);
            return TimeSpan.FromSeconds(Math.Max(0, mixOutTime));
        }

        public TimeSpan CalculateMixInPoint(double bpm, int barsFromStart = 8)
        {
            var beatDuration = 60.0 / bpm;
            var barDuration = beatDuration * 4;
            return TimeSpan.FromSeconds(barDuration * barsFromStart);
        }
    }
}
