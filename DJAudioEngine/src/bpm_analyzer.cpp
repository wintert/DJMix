// BPM Analyzer using MiniBPM library
// Provides accurate BPM detection and beat grid analysis

#include "dj_audio_engine.h"
#include "dj_audio_internal.h"
#include "MiniBpm.h"

#include <cmath>
#include <algorithm>
#include <vector>

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
    
    // Process audio in chunks
    const int chunkSize = 16384;  // Process 16k samples at a time
    for (int64_t i = 0; i < sampleCount; i += chunkSize) {
        int64_t remaining = sampleCount - i;
        int thisChunk = static_cast<int>(std::min<int64_t>(remaining, chunkSize));
        bpm.process(monoSamples.data() + i, thisChunk);
    }
    
    // Get the estimated BPM
    return bpm.estimateTempo();
}

// Detect the first beat position using onset detection
// Returns time in seconds of the first strong beat
double detectFirstBeat(const float* samples, int64_t sampleCount, int sampleRate, double bpm) {
    if (!samples || sampleCount == 0 || bpm <= 0) return 0.0;
    
    // Calculate samples per beat
    double secondsPerBeat = 60.0 / bpm;
    int64_t samplesPerBeat = static_cast<int64_t>(secondsPerBeat * sampleRate);
    
    // Simple onset detection using energy difference
    // Look at first 10 seconds to find the first strong beat
    int64_t searchLength = std::min<int64_t>(sampleCount, sampleRate * 10);
    
    // Window size for energy calculation (about 10ms)
    int windowSize = sampleRate / 100;
    
    double maxEnergy = 0;
    int64_t maxPos = 0;
    
    // Find the position with highest energy in the first beat period
    for (int64_t i = 0; i < std::min<int64_t>(searchLength, samplesPerBeat * 2); i += windowSize / 2) {
        double energy = 0;
        for (int j = 0; j < windowSize && (i + j) * 2 + 1 < sampleCount * 2; j++) {
            float left = samples[(i + j) * 2];
            float right = samples[(i + j) * 2 + 1];
            energy += left * left + right * right;
        }
        
        if (energy > maxEnergy) {
            maxEnergy = energy;
            maxPos = i;
        }
    }
    
    // Return position in seconds
    return static_cast<double>(maxPos) / sampleRate;
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
