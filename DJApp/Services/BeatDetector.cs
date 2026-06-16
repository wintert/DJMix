using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DJAutoMixApp.Services
{
    public class BeatDetector
    {
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
                }

                double bpm = AudioEngineInterop.audio_analyze_bpm_from_file(filePath);
                double offset = 0;

                if (bpm > 0)
                {
                    offset = AudioEngineInterop.audio_analyze_beat_offset_from_file(filePath, bpm);
                    trackInfo.BPM = bpm;
                    trackInfo.BPMConfidence = 0.95;
                }
                else
                {
                    using (var reader = new AudioFileReader(filePath))
                    {
                        var analysisLength = Math.Min(60, (int)reader.TotalTime.TotalSeconds);
                        var result = DetectBPM(reader, analysisLength);
                        bpm = result.bpm;
                        offset = result.offset;
                    }
                    trackInfo.BPM = bpm;
                    trackInfo.BPMConfidence = bpm > 0 ? 0.75 : 0.0;
                }

                trackInfo.FirstBeatOffset = offset;
                trackInfo.BeatPositions = GenerateBeatGrid(trackInfo.BPM, trackInfo.Duration.TotalSeconds, offset);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Beat detection error: {ex.Message}");
                trackInfo.BPM = 0;
                trackInfo.BPMConfidence = 0.0;
            }

            return trackInfo;
        }

        private (double bpm, double offset) DetectBPM(AudioFileReader reader, int analysisLengthSeconds)
        {
            int fileSampleRate = reader.WaveFormat.SampleRate;
            int channels = reader.WaveFormat.Channels;

            int secondsToRead = Math.Min(analysisLengthSeconds, (int)reader.TotalTime.TotalSeconds);
            int samplesPerChannel = secondsToRead * fileSampleRate;
            int totalSamplesToRead = samplesPerChannel * channels;
            var buffer = new float[totalSamplesToRead];
            int samplesRead = reader.Read(buffer, 0, totalSamplesToRead);
            int framesRead = samplesRead / channels;

            var mono = ConvertToMono(buffer, samplesRead, channels);

            // Downsample to ~1kHz for efficient autocorrelation
            int downsampleFactor = Math.Max(1, fileSampleRate / 1000);
            int downsampledLength = mono.Length / downsampleFactor;
            var downsampled = new float[downsampledLength];
            for (int i = 0; i < downsampledLength; i++)
            {
                float sum = 0;
                int start = i * downsampleFactor;
                for (int j = 0; j < downsampleFactor && (start + j) < mono.Length; j++)
                    sum += Math.Abs(mono[start + j]);
                downsampled[i] = sum / downsampleFactor;
            }

            // Normalize
            float maxVal = 1e-10f;
            for (int i = 0; i < downsampled.Length; i++)
                if (downsampled[i] > maxVal) maxVal = downsampled[i];
            if (maxVal > 1e-10f)
                for (int i = 0; i < downsampled.Length; i++)
                    downsampled[i] /= maxVal;

            // Compute onset envelope: half-wave rectified difference
            var onset = new float[downsampled.Length - 1];
            for (int i = 0; i < onset.Length; i++)
            {
                float diff = downsampled[i + 1] - downsampled[i];
                onset[i] = diff > 0 ? diff : 0;
            }

            // Compute autocorrelation of onset envelope
            int maxLag = onset.Length / 2;
            int minLag = (int)((60.0 / 180.0) * (fileSampleRate / (double)downsampleFactor)); // 180 BPM
            int maxSearchLag = (int)((60.0 / 60.0) * (fileSampleRate / (double)downsampleFactor)); // 60 BPM
            minLag = Math.Max(2, minLag);
            maxSearchLag = Math.Min(maxLag, maxSearchLag);

            if (minLag >= maxSearchLag)
                return (0, 0);

            double bestBpm = 0;
            double bestScore = 0;
            float[] onsetMeanRemoved = new float[onset.Length];
            float mean = 0;
            for (int i = 0; i < onset.Length; i++) mean += onset[i];
            mean /= onset.Length;
            for (int i = 0; i < onset.Length; i++) onsetMeanRemoved[i] = onset[i] - mean;

            for (int lag = minLag; lag < maxSearchLag; lag++)
            {
                double corr = 0;
                double norm1 = 0, norm2 = 0;
                for (int i = 0; i < onset.Length - lag; i++)
                {
                    corr += onsetMeanRemoved[i] * onsetMeanRemoved[i + lag];
                    norm1 += onsetMeanRemoved[i] * onsetMeanRemoved[i];
                    norm2 += onsetMeanRemoved[i + lag] * onsetMeanRemoved[i + lag];
                }
                if (norm1 > 0 && norm2 > 0)
                {
                    double normalized = corr / Math.Sqrt(norm1 * norm2);
                    if (normalized > bestScore)
                    {
                        bestScore = normalized;
                        bestBpm = 60.0 / (lag * downsampleFactor / (double)fileSampleRate);
                    }
                }
            }

            // Check half/double BPM
            if (bestBpm > 0)
            {
                while (bestBpm < 70) bestBpm *= 2;
                while (bestBpm > 180) bestBpm /= 2;

                // Refine: check if double or half gives stronger alignment
                double doubled = bestBpm * 2;
                double halved = bestBpm / 2;
                if (doubled <= 180)
                {
                    double score = CheckBpmAlignment(onset, bestBpm, downsampleFactor, fileSampleRate);
                    double score2 = CheckBpmAlignment(onset, doubled, downsampleFactor, fileSampleRate);
                    if (score2 > score * 1.15 && score2 > 0.01)
                        bestBpm = doubled;
                }
                if (halved >= 70)
                {
                    double score = CheckBpmAlignment(onset, bestBpm, downsampleFactor, fileSampleRate);
                    double scoreH = CheckBpmAlignment(onset, halved, downsampleFactor, fileSampleRate);
                    if (scoreH > score * 1.15 && scoreH > 0.01)
                        bestBpm = halved;
                }
            }

            // Try interval-based method as secondary verification
            if (bestBpm > 0)
            {
                var peaks = DetectPeaksSimple(onset, 0.15f);
                if (peaks.Count >= 4)
                {
                    double intervalBpm = CalculateBPMFromPeakIntervals(peaks, downsampleFactor, fileSampleRate);
                    if (intervalBpm > 0)
                    {
                        double diff = Math.Abs(bestBpm - intervalBpm);
                        if (diff > 5)
                        {
                            if (CheckBpmAlignment(onset, intervalBpm, downsampleFactor, fileSampleRate) >
                                CheckBpmAlignment(onset, bestBpm, downsampleFactor, fileSampleRate))
                                bestBpm = intervalBpm;
                        }
                    }
                    double offset = CalculateFirstBeatOffset(peaks, bestBpm, downsampleFactor, fileSampleRate);
                    return (Math.Round(bestBpm, 1), offset);
                }
            }

            return (Math.Round(bestBpm, 1), 0);
        }

        private double CheckBpmAlignment(float[] onset, double bpm, int downsampleFactor, int sampleRate)
        {
            double beatPeriod = 60.0 / bpm;
            double beatSamples = beatPeriod * (sampleRate / (double)downsampleFactor);
            if (beatSamples <= 0) return 0;

            double score = 0;
            int count = 0;
            for (double pos = beatSamples; pos < onset.Length; pos += beatSamples)
            {
                int idx = (int)Math.Round(pos);
                if (idx >= 0 && idx < onset.Length)
                {
                    score += onset[idx];
                    count++;
                }
            }
            return count > 0 ? score / count : 0;
        }

        private List<int> DetectPeaksSimple(float[] onset, float threshold)
        {
            var peaks = new List<int>();
            float mean = 0;
            for (int i = 0; i < onset.Length; i++) mean += onset[i];
            mean /= onset.Length;
            float dynamicThreshold = mean + (onset.Max() - mean) * 0.2f;
            if (dynamicThreshold < threshold) dynamicThreshold = threshold;

            int minDistance = Math.Max(2, onset.Length / 500);
            for (int i = 2; i < onset.Length - 2; i++)
            {
                if (onset[i] > dynamicThreshold &&
                    onset[i] >= onset[i - 1] && onset[i] >= onset[i + 1] &&
                    onset[i] >= onset[i - 2] && onset[i] >= onset[i + 2])
                {
                    if (peaks.Count == 0 || (i - peaks[peaks.Count - 1]) >= minDistance)
                        peaks.Add(i);
                }
            }
            return peaks;
        }

        private double CalculateBPMFromPeakIntervals(List<int> peaks, int downsampleFactor, int sampleRate)
        {
            if (peaks.Count < 4) return 0;

            var intervals = new List<double>();
            for (int i = 1; i < peaks.Count; i++)
                intervals.Add(peaks[i] - peaks[i - 1]);

            var bpmCandidates = new List<(double bpm, int weight)>();

            // Weighted histogram of intervals
            var grouped = intervals
                .GroupBy(x => (int)Math.Round(x / 2.0) * 2)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .ToList();

            foreach (var group in grouped)
            {
                double avgInterval = group.Average();
                double intervalSec = avgInterval * downsampleFactor / (double)sampleRate;
                double bpm = 60.0 / intervalSec;

                while (bpm < 70) bpm *= 2;
                while (bpm > 180) bpm /= 2;

                bpmCandidates.Add((bpm, group.Count()));
            }

            if (bpmCandidates.Count == 0) return 0;

            // Score each candidate by how well it aligns with all peaks
            double bestScore2 = 0;
            double bestCand = 0;
            foreach (var cand in bpmCandidates)
            {
                double period = 60.0 / cand.bpm;
                double periodSamples = period * sampleRate / downsampleFactor;
                if (periodSamples <= 0) continue;

                double score = 0;
                int matchCount = 0;
                for (int i = 1; i < peaks.Count; i++)
                {
                    double dist = peaks[i] - peaks[0];
                    double beats = dist / periodSamples;
                    double error = Math.Abs(beats - Math.Round(beats));
                    if (error < 0.2)
                    {
                        score += 1.0 - error;
                        matchCount++;
                    }
                }

                double weighted = matchCount > 0 ? (score / matchCount) * cand.weight : 0;
                if (weighted > bestScore2)
                {
                    bestScore2 = weighted;
                    bestCand = cand.bpm;
                }
            }

            return bestCand > 0 ? bestCand : bpmCandidates[0].bpm;
        }

        private float[] ConvertToMono(float[] buffer, int samplesRead, int channels)
        {
            if (channels == 1)
                return buffer.Take(samplesRead).ToArray();

            var monoSamples = new float[samplesRead / channels];
            for (int i = 0; i < monoSamples.Length; i++)
            {
                float sum = 0;
                for (int ch = 0; ch < channels; ch++)
                    sum += buffer[i * channels + ch];
                monoSamples[i] = sum / channels;
            }
            return monoSamples;
        }

        private double CalculateFirstBeatOffset(List<int> peaks, double bpm, int downsampleFactor, int sampleRate)
        {
            if (peaks.Count < 4 || bpm <= 0) return 0;

            double beatSamples = (60.0 / bpm) * (sampleRate / (double)downsampleFactor);
            if (beatSamples <= 0) return 0;

            // Try each early peak as candidate for first beat
            var earlyPeaks = peaks.Take(Math.Min(15, peaks.Count)).ToList();
            double bestOffset = 0;
            double bestError = double.MaxValue;

            foreach (var candidate in earlyPeaks)
            {
                double candidateTime = candidate * downsampleFactor / (double)sampleRate;
                double totalError = 0;
                int matchCount = 0;

                foreach (var peak in peaks.Take(50))
                {
                    double peakTime = peak * downsampleFactor / (double)sampleRate;
                    double timeSinceCandidate = peakTime - candidateTime;
                    if (timeSinceCandidate < 0) continue;

                    double beatsElapsed = timeSinceCandidate / (60.0 / bpm);
                    double fractionalBeat = beatsElapsed - Math.Round(beatsElapsed);
                    double error = Math.Abs(fractionalBeat);

                    if (error < 0.2)
                    {
                        totalError += error;
                        matchCount++;
                    }
                }

                if (matchCount > 3)
                {
                    double score = totalError / matchCount - (matchCount * 0.001);
                    if (score < bestError)
                    {
                        bestError = score;
                        bestOffset = candidateTime;
                    }
                }
            }

            return bestOffset;
        }

        private List<double> GenerateBeatGrid(double bpm, double durationSeconds, double offset = 0)
        {
            var beatGrid = new List<double>();
            if (bpm <= 0) return beatGrid;
            var beatInterval = 60.0 / bpm;

            for (double time = offset; time < durationSeconds; time += beatInterval)
                beatGrid.Add(time);

            return beatGrid;
        }

        public TimeSpan CalculateMixOutPoint(double bpm, TimeSpan duration, int barsBeforeEnd = 16)
        {
            if (bpm <= 0) return TimeSpan.Zero;
            var beatDuration = 60.0 / bpm;
            var barDuration = beatDuration * 4;
            var mixOutTime = duration.TotalSeconds - (barDuration * barsBeforeEnd);
            return TimeSpan.FromSeconds(Math.Max(0, mixOutTime));
        }

        public TimeSpan CalculateMixInPoint(double bpm, int barsFromStart = 8)
        {
            if (bpm <= 0) return TimeSpan.Zero;
            var beatDuration = 60.0 / bpm;
            var barDuration = beatDuration * 4;
            return TimeSpan.FromSeconds(barDuration * barsFromStart);
        }
    }
}
