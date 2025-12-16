using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace DJAutoMixApp.Services
{
    public class WaveformService
    {
        private const int Resolution = 2000; // Total points to generate per track

        /// <summary>
        /// Generates a collection of points for a waveform display
        /// </summary>
        public async Task<PointCollection> GenerateWaveformAsync(string filePath)
        {
            return await Task.Run(() =>
            {
                var points = new PointCollection();
                try
                {
                    using (var reader = new AudioFileReader(filePath))
                    {
                        var channelCount = reader.WaveFormat.Channels;
                        var totalSamples = reader.Length / (reader.WaveFormat.BitsPerSample / 8);
                        var samplesPerPoint = (int)(totalSamples / Resolution);
                        
                        // Ensure minimal sample step
                        if (samplesPerPoint < 1) samplesPerPoint = 1;

                        var buffer = new float[samplesPerPoint];
                        var maxPeaks = new List<double>();
                        
                        // Read through the file
                        int read;
                        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            // Find max peak in this chunk
                            float max = 0;
                            for (int i = 0; i < read; i++)
                            {
                                var abs = Math.Abs(buffer[i]);
                                if (abs > max) max = abs;
                            }
                            maxPeaks.Add(max);
                        }

                        // Create points for Polyline (Top only, mirrored by UI likely, or full)
                        // Let's generate a normalize 0-1 set, let UI scale it.
                        // Actually WPF PointCollection needs X,Y.
                        // We will map X to 0..Resolution, Y to Amplitude.
                        
                        double maxVal = maxPeaks.Count > 0 ? maxPeaks.Max() : 1.0;
                        if (maxVal == 0) maxVal = 1.0;

                        // Create a mirrored waveform or just top half?
                        // Top half is standard for small displays.
                        // Let's do top and bottom (mirrored) for "Professional" look, or just top.
                        // Let's do a centered waveform. Center = 0.
                        // Y moves from -1 to 1?
                        
                        // For Polyline, usually simpler to just do Top profile and scale Y.
                        // Let's return just the amplitude indices (0 to 1).
                        // Wait, PointCollection requires X,Y.
                        
                        for (int i = 0; i < maxPeaks.Count; i++)
                        {
                            // Normalize 0-1
                            double val = maxPeaks[i] / maxVal;
                            
                            // Let's assume height is 100 units. Center is 50.
                            // But Viewport scaling is better.
                            // Let's simply return (i, val). X=index, Y=Normalized Amplitude (0..1)
                            points.Add(new Point(i, val));
                        }
                        
                        points.Freeze(); // Freezing for cross-thread access
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Waveform generation error: {ex.Message}");
                }
                return points;
            });
        }
    }
}
