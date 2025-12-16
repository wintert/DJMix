#include "dj_audio_internal.h"
#include <cstring>
#include <cmath>

namespace dj {

Mixer::Mixer()
    : crossfader_position_(0.5f)
{
}

void Mixer::mix(Deck* deck_a, Deck* deck_b, float* output, int frames) {
    // Read from both decks
    std::vector<float> buffer_a(frames * 2);
    std::vector<float> buffer_b(frames * 2);
    
    deck_a->readSamples(buffer_a.data(), frames);
    deck_b->readSamples(buffer_b.data(), frames);
    
    // Apply crossfader with power curve
    // Power curve ensures constant power during transition
    float angle = crossfader_position_ * 1.5707963f;  // 0 to π/2
    float gain_a = std::cos(angle);
    float gain_b = std::sin(angle);
    
    // Mix the buffers
    for (int i = 0; i < frames * 2; i++) {
        output[i] = buffer_a[i] * gain_a + buffer_b[i] * gain_b;
        
        // Soft clipping to prevent harsh distortion
        if (output[i] > 1.0f) {
            output[i] = 1.0f - std::exp(1.0f - output[i]);
        }
        else if (output[i] < -1.0f) {
            output[i] = -1.0f + std::exp(1.0f + output[i]);
        }
    }
}

} // namespace dj
