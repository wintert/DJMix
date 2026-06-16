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
        
        // QM DSP detection function parameters - matching Mixxx's approach
        // Larger frame sizes give more accurate tempo detection
        const int stepSize = 1024;          // Hop size (Mixxx uses larger hop)
        const int frameLength = 2048;       // Frame size (larger = more frequency resolution)
        
        if (logFile) {
            fprintf(logFile, "Creating DFConfig (Mixxx-style settings)...\n");
            fflush(logFile);
        }
        
        // Configure detection function - using settings closer to Mixxx
        DFConfig dfConfig;
        dfConfig.stepSize = stepSize;
        dfConfig.frameLength = frameLength;
        dfConfig.DFType = DF_COMPLEXSD;     // Complex spectral difference
        dfConfig.dbRise = 3.0;
        dfConfig.adaptiveWhitening = true;  // Enable for better detection
        dfConfig.whiteningRelaxCoeff = 0.9997;  // Mixxx-style whitening
        dfConfig.whiteningFloor = 0.01;
        
        if (logFile) {
            fprintf(logFile, "Creating DetectionFunction...\n");
            fflush(logFile);
        }
        
        DetectionFunction df(dfConfig);
        
        if (logFile) {
            fprintf(logFile, "DetectionFunction created successfully!\n");
            fflush(logFile);
        }
    
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
        
        // Log first frame processing
        if (f == 0 && logFile) {
            fprintf(logFile, "Processing first frame (startFrame=%lld)...\n", (long long)startFrame);
            fflush(logFile);
        }
        
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
        
        // Log before first processTimeDomain call
        if (f == 0 && logFile) {
            fprintf(logFile, "Calling df.processTimeDomain for first frame...\n");
            fflush(logFile);
        }
        
        // Calculate detection function value for this frame
        double dfValue = df.processTimeDomain(frame.data());
        detectionFunction.push_back(dfValue);
        
        // Log first frame success
        if (f == 0 && logFile) {
            fprintf(logFile, "First frame processed successfully, dfValue=%.4f\n", dfValue);
            fflush(logFile);
        }
        
        // Log progress every 100 frames
        if (logFile && f > 0 && f % 100 == 0) {
            fprintf(logFile, "Processed %lld/%lld frames...\n", (long long)f, (long long)numFrames);
            fflush(logFile);
        }
    }
    
    if (logFile) {
        fprintf(logFile, "All %zu frames processed successfully!\n", detectionFunction.size());
        fflush(logFile);
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
    
    if (logFile) {
        fprintf(logFile, "Creating TempoTrackV2...\n");
        fflush(logFile);
    }
    
    // Use TempoTrackV2 to find tempo
    TempoTrackV2 tempoTracker(static_cast<float>(sampleRate), stepSize);
    
    if (logFile) {
        fprintf(logFile, "TempoTrackV2 created successfully!\n");
        fflush(logFile);
    }
    
    // IMPORTANT: beatPeriod must be pre-sized to match detection function size
    // because viterbi_decode writes to it via indexed access, not push_back
    std::vector<double> beatPeriod(detectionFunction.size(), 0.0);
    std::vector<double> tempi;
    
    if (logFile) {
        fprintf(logFile, "Calling calculateBeatPeriod...\n");
        fflush(logFile);
    }
    
    // Calculate beat period (tempo)
    tempoTracker.calculateBeatPeriod(detectionFunction, beatPeriod, tempi, 120.0, false);
    
    if (logFile) {
        fprintf(logFile, "calculateBeatPeriod completed! beatPeriod.size=%zu, tempi.size=%zu\n", 
                beatPeriod.size(), tempi.size());
        fflush(logFile);
    }
    
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
    // Normalize BPM to reasonable DJ range (120-160 for dance music)
    // First handle extreme cases
    while (detectedBPM > 0 && detectedBPM < 60) detectedBPM *= 2;
    while (detectedBPM > 200) detectedBPM /= 2;
    
    // Handle common subdivision errors:
    // - BPM in 85-100 range often means 1.5x is the actual tempo (triplet feel detected)
    // - BPM in 60-85 range often means 2x is the actual tempo (half-time detected)
    if (detectedBPM >= 85 && detectedBPM < 100) {
        // Likely detected 2/3 of actual tempo (e.g., 94 instead of 140)
        detectedBPM *= 1.5;
    } else if (detectedBPM >= 60 && detectedBPM < 85) {
        // Half-time detection
        detectedBPM *= 2;
    } else if (detectedBPM > 180) {
        detectedBPM /= 2;
    }
    
    // AFTER initial corrections, check if result is 4/3 of house tempo
    // This is a SEPARATE check because the above corrections might push BPM into this range
    // (e.g., 42.4 -> 84.8 -> 169.6, which is 4/3 of 127)
    if (detectedBPM > 155 && detectedBPM <= 180) {
        double corrected = detectedBPM * 0.75;  // Divide by 4/3
        if (logFile) {
            fprintf(logFile, "BPM CORRECTION: %.1f in 155-180 range, corrected=%.1f\n", detectedBPM, corrected);
        }
        if (corrected >= 117 && corrected <= 140) {  // Expanded range slightly
            if (logFile) {
                fprintf(logFile, "BPM CORRECTION: Applying! %.1f -> %.1f\n", detectedBPM, corrected);
            }
            detectedBPM = corrected;
        }
    }
    
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
    
    const int stepSize = 1024;
    const int frameLength = 2048;
    
    // Configure detection function - matching Mixxx settings
    DFConfig dfConfig;
    dfConfig.stepSize = stepSize;
    dfConfig.frameLength = frameLength;
    dfConfig.DFType = DF_COMPLEXSD;
    dfConfig.dbRise = 3.0;
    dfConfig.adaptiveWhitening = true;
    dfConfig.whiteningRelaxCoeff = 0.9997;
    dfConfig.whiteningFloor = 0.01;
    
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
    
    // IMPORTANT: beatPeriod must be pre-sized to match detection function size
    std::vector<double> beatPeriod(detectionFunction.size(), 0.0);
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

// File-based BPM analysis (creates internal AudioFile, safe to use while decks are playing)
double dj::analyzeBPMFromFile(const char* filepath) {
    if (!filepath) return 0.0;
    
    AudioFile tempFile;
    if (!tempFile.load(filepath)) {
        FILE* logFile = fopen("c:\\Apps\\DJApp\\cpp_debug.log", "a");
        if (logFile) { fprintf(logFile, "analyzeBPMFromFile: FAILED to load file: %s\n", filepath); fclose(logFile); }
        return 0.0;
    }
    
    const float* data = tempFile.getData();
    int64_t totalSamples = tempFile.getTotalSamples();
    int sampleRate = tempFile.getSampleRate();
    
    FILE* logFile = fopen("c:\\Apps\\DJApp\\cpp_debug.log", "a");
    if (logFile) {
        fprintf(logFile, "analyzeBPMFromFile: loaded '%s' - %lld frames, %d Hz\n", filepath, (long long)totalSamples, sampleRate);
        fclose(logFile);
    }
    
    double result = dj::analyzeBPM(data, totalSamples, sampleRate);
    
    logFile = fopen("c:\\Apps\\DJApp\\cpp_debug.log", "a");
    if (logFile) {
        fprintf(logFile, "analyzeBPMFromFile: result=%.1f BPM\n", result);
        fclose(logFile);
    }
    
    return result;
}

double dj::detectFirstBeatFromFile(const char* filepath, double bpm) {
    if (!filepath || bpm <= 0) return 0.0;
    
    AudioFile tempFile;
    if (!tempFile.load(filepath)) return 0.0;
    
    const float* data = tempFile.getData();
    int64_t totalSamples = tempFile.getTotalSamples();
    int sampleRate = tempFile.getSampleRate();
    
    return dj::detectFirstBeat(data, totalSamples, sampleRate, bpm);
}

// C API for file-based BPM analysis
extern "C" {

DJ_API double audio_analyze_bpm_from_file(const char* filepath) {
    return dj::analyzeBPMFromFile(filepath);
}

DJ_API double audio_analyze_beat_offset_from_file(const char* filepath, double bpm) {
    return dj::detectFirstBeatFromFile(filepath, bpm);
}

} // extern "C"
