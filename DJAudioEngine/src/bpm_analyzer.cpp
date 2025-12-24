// BPM Analyzer using QM DSP TempoTrackV2 (same library as Mixxx)
// Provides accurate BPM detection and beat grid analysis

#include "dj_audio_engine.h"
#include "dj_audio_internal.h"

// QM DSP includes
#include "dsp/tempotracking/TempoTrackV2.h"
#include "dsp/onsets/DetectionFunction.h"

#include <cmath>
#include <algorithm>
#include <vector>
#include <cstdio>
#include <numeric>

namespace dj {

// Analyze audio data for BPM using QM DSP TempoTrackV2
// This is the same algorithm used by Mixxx for accurate tempo detection
double analyzeBPM(const float* samples, int64_t sampleCount, int sampleRate) {
    FILE* logFile = fopen("c:\\Apps\\DJApp\\cpp_debug.log", "a");
    
    // Early logging to diagnose crash
    if (logFile) {
        fprintf(logFile, "\n=== BPM ANALYSIS START ===\n");
        fprintf(logFile, "samples=%p, count=%lld, rate=%d\n", 
                (void*)samples, (long long)sampleCount, sampleRate);
        fflush(logFile);
    }
    
    if (!samples || sampleCount == 0 || sampleRate == 0) {
        if (logFile) {
            fprintf(logFile, "ERROR: Invalid input parameters\n");
            fclose(logFile);
        }
        return 0.0;
    }
    
    try {
        if (logFile) {
            fprintf(logFile, "Input: %lld samples at %d Hz (%.1f seconds)\n", 
                    (long long)sampleCount, sampleRate, (double)sampleCount / sampleRate);
            fflush(logFile);
        }
        
        // QM DSP detection function parameters
        const int stepSize = 512;           // Hop size
        const int frameLength = 1024;       // Frame size
        
        if (logFile) {
            fprintf(logFile, "Creating DFConfig...\n");
            fflush(logFile);
        }
        
        // Configure detection function (Complex Spectral Difference - best for beats)
        DFConfig dfConfig;
        dfConfig.stepSize = stepSize;
        dfConfig.frameLength = frameLength;
        dfConfig.DFType = DF_COMPLEXSD;     // Complex spectral difference
        dfConfig.dbRise = 3.0;
        dfConfig.adaptiveWhitening = false;
        dfConfig.whiteningRelaxCoeff = -1;
        dfConfig.whiteningFloor = -1;
        
        if (logFile) {
            fprintf(logFile, "Creating DetectionFunction...\n");
            fflush(logFile);
        }
        
        DetectionFunction df(dfConfig);
    
    // Convert stereo to mono and calculate detection function
    std::vector<double> detectionFunction;
    std::vector<double> frame(frameLength);
    
    // sampleCount = number of stereo sample frames
    // Buffer has sampleCount * 2 floats (stereo interleaved: L0,R0,L1,R1,...)
    // We need enough frames to process: (sampleCount - frameLength) / stepSize
    int64_t numFrames = (sampleCount - frameLength) / stepSize;
    if (numFrames <= 0) {
        if (logFile) {
            fprintf(logFile, "ERROR: Not enough samples for analysis (numFrames=%lld)\n", (long long)numFrames);
            fclose(logFile);
        }
        return 0.0;
    }
    
    if (logFile) {
        fprintf(logFile, "Processing %lld frames (step=%d, frameLen=%d)\n", 
                (long long)numFrames, stepSize, frameLength);
    }
    
    // Process audio frames
    for (int64_t f = 0; f < numFrames; f++) {
        int64_t startFrame = f * stepSize;  // Starting frame index (mono)
        
        // Convert stereo to mono for this frame
        for (int i = 0; i < frameLength; i++) {
            int64_t frameIdx = startFrame + i;
            if (frameIdx < sampleCount) {
                int64_t bufIdx = frameIdx * 2;  // stereo buffer index (L channel)
                frame[i] = (samples[bufIdx] + samples[bufIdx + 1]) / 2.0;
            } else {
                frame[i] = 0.0;
            }
        }
        
        // Calculate detection function value for this frame
        double dfValue = df.processTimeDomain(frame.data());
        detectionFunction.push_back(dfValue);
    }
    
    if (logFile) {
        fprintf(logFile, "Detection function: %zu frames computed\n", detectionFunction.size());
    }
    
    if (detectionFunction.size() < 100) {
        if (logFile) {
            fprintf(logFile, "ERROR: Not enough frames for tempo tracking\n");
            fclose(logFile);
        }
        return 0.0;
    }
    
    // Use TempoTrackV2 to find tempo
    TempoTrackV2 tempoTracker(static_cast<float>(sampleRate), stepSize);
    
    std::vector<double> beatPeriod;
    std::vector<double> tempi;
    
    // Calculate beat period (tempo)
    tempoTracker.calculateBeatPeriod(detectionFunction, beatPeriod, tempi, 120.0, false);
    
    // Calculate median tempo from the tempi array
    double detectedBPM = 0.0;
    if (!tempi.empty()) {
        // Take median of tempi for stability
        std::vector<double> tempiSorted = tempi;
        std::sort(tempiSorted.begin(), tempiSorted.end());
        
        // Remove outliers (tempi < 60 or > 200)
        std::vector<double> filteredTempi;
        for (double t : tempiSorted) {
            if (t >= 60.0 && t <= 200.0) {
                filteredTempi.push_back(t);
            }
        }
        
        if (!filteredTempi.empty()) {
            // Use median
            size_t midIdx = filteredTempi.size() / 2;
            detectedBPM = filteredTempi[midIdx];
        } else if (!tempiSorted.empty()) {
            // Fallback to median of all tempi
            detectedBPM = tempiSorted[tempiSorted.size() / 2];
        }
    }
    
    // Normalize BPM to reasonable DJ range (70-160)
    while (detectedBPM > 0 && detectedBPM < 70) detectedBPM *= 2;
    while (detectedBPM > 160) detectedBPM /= 2;
    
    if (logFile) {
        fprintf(logFile, "BPM ANALYSIS RESULT: %.1f BPM (raw tempi count: %zu)\n", 
                detectedBPM, tempi.size());
        
        // Log first few and last few tempi for debugging
        if (tempi.size() > 10) {
            fprintf(logFile, "  First 5 tempi: ");
            for (size_t i = 0; i < 5 && i < tempi.size(); i++) {
                fprintf(logFile, "%.1f ", tempi[i]);
            }
            fprintf(logFile, "\n  Last 5 tempi: ");
            for (size_t i = tempi.size() - 5; i < tempi.size(); i++) {
                fprintf(logFile, "%.1f ", tempi[i]);
            }
            fprintf(logFile, "\n");
        }
        
        fclose(logFile);
    }
    
    return detectedBPM;
    
    } catch (const std::exception& e) {
        FILE* errLog = fopen("c:\\Apps\\DJApp\\cpp_debug.log", "a");
        if (errLog) {
            fprintf(errLog, "EXCEPTION in analyzeBPM: %s\n", e.what());
            fclose(errLog);
        }
        return 0.0;
    } catch (...) {
        FILE* errLog = fopen("c:\\Apps\\DJApp\\cpp_debug.log", "a");
        if (errLog) {
            fprintf(errLog, "UNKNOWN EXCEPTION in analyzeBPM\n");
            fclose(errLog);
        }
        return 0.0;
    }
}

// Detect beat positions using QM DSP
std::vector<double> detectBeats(const float* samples, int64_t sampleCount, int sampleRate) {
    std::vector<double> beatTimes;
    
    if (!samples || sampleCount == 0) return beatTimes;
    
    const int stepSize = 512;
    const int frameLength = 1024;
    
    // Configure detection function
    DFConfig dfConfig;
    dfConfig.stepSize = stepSize;
    dfConfig.frameLength = frameLength;
    dfConfig.DFType = DF_COMPLEXSD;
    dfConfig.dbRise = 3.0;
    dfConfig.adaptiveWhitening = false;
    dfConfig.whiteningRelaxCoeff = -1;
    dfConfig.whiteningFloor = -1;
    
    DetectionFunction df(dfConfig);
    
    // Calculate detection function
    std::vector<double> detectionFunction;
    std::vector<double> frame(frameLength);
    
    // sampleCount = number of stereo sample frames
    int64_t numFrames = (sampleCount - frameLength) / stepSize;
    if (numFrames <= 0) return beatTimes;
    
    for (int64_t f = 0; f < numFrames; f++) {
        int64_t startFrame = f * stepSize;
        
        for (int i = 0; i < frameLength; i++) {
            int64_t frameIdx = startFrame + i;
            if (frameIdx < sampleCount) {
                int64_t bufIdx = frameIdx * 2;
                frame[i] = (samples[bufIdx] + samples[bufIdx + 1]) / 2.0;
            } else {
                frame[i] = 0.0;
            }
        }
        
        double dfValue = df.processTimeDomain(frame.data());
        detectionFunction.push_back(dfValue);
    }
    
    if (detectionFunction.size() < 100) return beatTimes;
    
    // Use TempoTrackV2 for beat positions
    TempoTrackV2 tempoTracker(static_cast<float>(sampleRate), stepSize);
    
    std::vector<double> beatPeriod;
    std::vector<double> tempi;
    std::vector<double> beats;
    
    tempoTracker.calculateBeatPeriod(detectionFunction, beatPeriod, tempi);
    tempoTracker.calculateBeats(detectionFunction, beatPeriod, beats);
    
    // Convert beat positions (in df frames) to seconds
    for (double beatFrame : beats) {
        double beatTime = (beatFrame * stepSize) / static_cast<double>(sampleRate);
        beatTimes.push_back(beatTime);
    }
    
    return beatTimes;
}

// Detect the first beat position
double detectFirstBeat(const float* samples, int64_t sampleCount, int sampleRate, double bpm) {
    if (!samples || sampleCount == 0 || bpm <= 0) return 0.0;
    
    auto beats = detectBeats(samples, sampleCount, sampleRate);
    
    FILE* logFile = fopen("c:\\Apps\\DJApp\\cpp_debug.log", "a");
    if (logFile) {
        fprintf(logFile, "detectFirstBeat (QM DSP): Found %zu beats\n", beats.size());
    }
    
    if (!beats.empty()) {
        if (logFile) {
            fprintf(logFile, "detectFirstBeat (QM DSP): First beat at %.3f seconds\n", beats[0]);
            fclose(logFile);
        }
        return beats[0];
    }
    
    if (logFile) {
        fprintf(logFile, "detectFirstBeat (QM DSP): No beats found, returning 0\n");
        fclose(logFile);
    }
    
    return 0.0;
}

} // namespace dj

// C API for BPM analysis
extern "C" {

// Analyze a loaded track for BPM
DJ_API double audio_analyze_bpm(int deck_id) {
    if (!dj::g_engine || deck_id < 0 || deck_id > 1) return 0.0;
    
    auto& deck = dj::g_engine->decks[deck_id];
    if (!deck || !deck->isLoaded()) return 0.0;
    
    auto* audioFile = deck->getAudioFile();
    if (!audioFile) return 0.0;
    
    const float* data = audioFile->getData();
    int64_t totalSamples = audioFile->getTotalSamples();
    int sampleRate = audioFile->getSampleRate();
    
    return dj::analyzeBPM(data, totalSamples, sampleRate);
}

// Analyze a loaded track for first beat position
DJ_API double audio_analyze_beat_offset(int deck_id, double bpm) {
    if (!dj::g_engine || deck_id < 0 || deck_id > 1 || bpm <= 0) return 0.0;
    
    auto& deck = dj::g_engine->decks[deck_id];
    if (!deck || !deck->isLoaded()) return 0.0;
    
    auto* audioFile = deck->getAudioFile();
    if (!audioFile) return 0.0;
    
    const float* data = audioFile->getData();
    int64_t totalSamples = audioFile->getTotalSamples();
    int sampleRate = audioFile->getSampleRate();
    
    return dj::detectFirstBeat(data, totalSamples, sampleRate, bpm);
}

} // extern "C"
