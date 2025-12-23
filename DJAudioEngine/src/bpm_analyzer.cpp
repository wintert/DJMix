// BPM Analyzer using MiniBPM library
// Provides accurate BPM detection and beat grid analysis

#include "dj_audio_engine.h"
#include "dj_audio_internal.h"
#include "MiniBpm.h"

#include <cmath>
#include <algorithm>
#include <vector>
#include <cstdio>

namespace dj {

// Analyze audio data for BPM using MiniBPM
double analyzeBPM(const float* samples, int64_t sampleCount, int sampleRate) {
    if (!samples || sampleCount == 0) return 0.0;
    
    // MiniBPM expects mono float samples
    // Convert stereo to mono if needed (assume stereo input)
    std::vector<float> monoSamples(sampleCount);
    for (int64_t i = 0; i < sampleCount; i++) {
        // Average left and right channels
        monoSamples[i] = (samples[i * 2] + samples[i * 2 + 1]) / 2.0f;
    }
    
    // Create MiniBPM analyzer
    breakfastquay::MiniBPM bpm(sampleRate);
    
    // SET BPM RANGE FOR DANCE MUSIC (100-160 BPM)
    // This helps avoid half-tempo detection
    bpm.setBPMRange(100.0, 160.0);
    
    // Process audio in chunks
    const int chunkSize = 16384;  // Process 16k samples at a time
    for (int64_t i = 0; i < sampleCount; i += chunkSize) {
        int64_t remaining = sampleCount - i;
        int thisChunk = static_cast<int>(std::min<int64_t>(remaining, chunkSize));
        bpm.process(monoSamples.data() + i, thisChunk);
    }
    
    // Get the estimated BPM and candidates
    double detected = bpm.estimateTempo();
    std::vector<double> candidates = bpm.getTempoCandidates();
    
    FILE* logFile = fopen("c:\\Apps\\DJApp\\cpp_debug.log", "a");
    if (logFile) {
        fprintf(logFile, "BPM ANALYSIS: detected=%.1f, candidates=[", detected);
        for (size_t i = 0; i < std::min(candidates.size(), (size_t)5); i++) {
            fprintf(logFile, "%.1f%s", candidates[i], (i < 4 && i < candidates.size()-1) ? ", " : "");
        }
        fprintf(logFile, "]\n");
        fclose(logFile);
    }
    
    return detected;
}

// Simple low-pass filter for bass frequencies
float lowPassFilter(float input, float& state, float alpha) {
    state = state + alpha * (input - state);
    return state;
}

// Detect the first strong kick/beat using bass-focused transient detection
// Returns time in seconds where the first kick/beat occurs
double detectFirstBeat(const float* samples, int64_t sampleCount, int sampleRate, double bpm) {
    if (!samples || sampleCount == 0 || bpm <= 0) return 0.0;
    
    // Calculate samples per beat
    double secondsPerBeat = 60.0 / bpm;
    int64_t samplesPerBeat = static_cast<int64_t>(secondsPerBeat * sampleRate);
    
    // Look at first 8 beats to find a reliable kick pattern
    int64_t searchLength = std::min<int64_t>(sampleCount, samplesPerBeat * 8);
    
    // Low-pass filter coefficient (cutoff ~100Hz at 44100)
    float lpAlpha = 0.01f;
    float lpState = 0;
    
    // Window for energy calculation (~23ms at 44100Hz - one hop)
    int windowSize = 1024;
    int hopSize = 512;
    
    // Calculate bass energy envelope
    std::vector<double> envelope;
    
    for (int64_t i = 0; i < searchLength - windowSize; i += hopSize) {
        double energy = 0;
        
        for (int j = 0; j < windowSize; j++) {
            int64_t idx = (i + j) * 2;
            if (idx + 1 >= sampleCount * 2) break;
            
            float mono = (samples[idx] + samples[idx + 1]) / 2.0f;
            
            // Low-pass filter to isolate bass
            float bass = lowPassFilter(mono, lpState, lpAlpha);
            energy += bass * bass;
        }
        
        envelope.push_back(energy);
    }
    
    if (envelope.empty()) return 0.0;
    
    // Find max energy for threshold calculation
    double maxEnergy = 0;
    for (const auto& e : envelope) {
        if (e > maxEnergy) maxEnergy = e;
    }
    
    // Threshold at 25% of max - look for significant kick energy
    double threshold = maxEnergy * 0.25;
    
    FILE* logFile = fopen("c:\\Apps\\DJApp\\cpp_debug.log", "a");
    if (logFile) {
        fprintf(logFile, "detectFirstBeat: bpm=%.1f, secondsPerBeat=%.3f, searching %lld samples\n", 
                bpm, secondsPerBeat, (long long)searchLength);
    }
    
    // Find the first strong transient (positive energy spike above threshold)
    for (size_t i = 1; i < envelope.size(); i++) {
        double transient = envelope[i] - envelope[i-1];
        if (transient > threshold && envelope[i] > threshold) {
            // Found a strong kick - calculate its time
            int64_t samplePos = i * hopSize;
            double posSeconds = static_cast<double>(samplePos) / sampleRate;
            
            if (logFile) {
                fprintf(logFile, "detectFirstBeat: FOUND first kick at %.3f seconds (sample %lld)\n", 
                        posSeconds, (long long)samplePos);
                fclose(logFile);
            }
            
            // Return the ACTUAL position of the first kick - this is where beats start!
            return posSeconds;
        }
    }
    
    if (logFile) {
        fprintf(logFile, "detectFirstBeat: No kick found, returning 0\n");
        fclose(logFile);
    }
    
    // Fallback: return 0 (assume beat at start)
    return 0.0;
}

} // namespace dj

// C API for BPM analysis
extern "C" {

// Analyze a loaded track for BPM
// Returns 0.0 if analysis fails
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
// Returns beat offset in seconds
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
