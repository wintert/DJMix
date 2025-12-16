#include "dj_audio_internal.h"
#include <SoundTouch.h>
#include <algorithm>
#include <cmath>

namespace dj {

Deck::Deck(int sample_rate)
    : sample_rate_(sample_rate)
    , audio_file_(std::make_unique<AudioFile>())
    , soundtouch_(std::make_unique<soundtouch::SoundTouch>())
    , is_playing_(false)
    , sample_position_(0)
    , volume_(1.0f)
    , tempo_(1.0)
    , pitch_semitones_(0.0)
    , bpm_(120.0)
    , beat_offset_(0.0)
    , eq_low_(1.0f)
    , eq_mid_(1.0f)
    , eq_high_(1.0f)
{
    soundtouch_->setSampleRate(sample_rate);
    soundtouch_->setChannels(2);
    soundtouch_->setTempo(1.0);
    soundtouch_->setPitch(1.0);
}

Deck::~Deck() {
}

bool Deck::loadTrack(const char* filepath) {
    std::lock_guard<std::mutex> lock(deck_mutex_);
    
    if (!audio_file_->load(filepath)) {
        return false;
    }
    
    // Reset playback state
    sample_position_ = 0;
    is_playing_ = false;
    
    // Clear SoundTouch buffer
    soundtouch_->clear();
    
    return true;
}

void Deck::unloadTrack() {
    std::lock_guard<std::mutex> lock(deck_mutex_);
    stop();
    audio_file_->unload();
    soundtouch_->clear();
}

void Deck::play(int64_t startPosition) {
    if (startPosition >= 0) {
        // Set position and clear buffer BEFORE starting playback
        sample_position_ = startPosition;
        soundtouch_->clear();
    }
    is_playing_ = true;
}

void Deck::pause() {
    is_playing_ = false;
}

void Deck::stop() {
    is_playing_ = false;
    sample_position_ = 0;
    soundtouch_->clear();
}

void Deck::setPosition(double seconds) {
    std::lock_guard<std::mutex> lock(deck_mutex_);
    
    int64_t new_pos = static_cast<int64_t>(seconds * sample_rate_);
    new_pos = std::max<int64_t>(0, std::min(new_pos, audio_file_->getTotalSamples()));
    sample_position_ = new_pos;
    
    // Clear SoundTouch buffer when seeking
    soundtouch_->clear();
}

double Deck::getPosition() const {
    return static_cast<double>(sample_position_) / sample_rate_;
}

double Deck::getDuration() const {
    return audio_file_->getDurationSeconds();
}

void Deck::setTempo(double tempo) {
    tempo_ = std::max(0.5, std::min(tempo, 2.0));
    soundtouch_->setTempo(tempo_);
}

void Deck::setPitch(double semitones) {
    pitch_semitones_ = std::max(-12.0, std::min(semitones, 12.0));
    soundtouch_->setPitchSemiTones(pitch_semitones_);
}

void Deck::setSamplePosition(int64_t pos, bool forceSync) {
    int64_t old_pos = sample_position_;
    sample_position_ = pos;
    
    // For sync operations, ALWAYS clear the buffer to ensure new samples
    // For normal seeks, only clear on large jumps to avoid clicks
    if (forceSync) {
        soundtouch_->clear();
    } else {
        int64_t jump_size = std::abs(pos - old_pos);
        if (jump_size > sample_rate_) { // More than 1 second jump
            soundtouch_->clear();
        }
    }
}

double Deck::getPhase() const {
    if (bpm_ <= 0.0) return 0.0;
    
    // Calculate samples per beat
    double seconds_per_beat = 60.0 / bpm_;
    int64_t samples_per_beat = static_cast<int64_t>(seconds_per_beat * sample_rate_);
    
    if (samples_per_beat <= 0) return 0.0;
    
    // Apply beat offset
    int64_t offset_samples = static_cast<int64_t>(beat_offset_ * sample_rate_);
    int64_t adjusted_position = sample_position_ - offset_samples;
    
    // Calculate phase (0.0 to 1.0)
    int64_t samples_into_beat = adjusted_position % samples_per_beat;
    if (samples_into_beat < 0) samples_into_beat += samples_per_beat;
    
    return static_cast<double>(samples_into_beat) / samples_per_beat;
}

int Deck::readSamples(float* output, int frames) {
    // Always zero-initialize output to prevent noise from uninitialized data
    memset(output, 0, frames * 2 * sizeof(float));
    
    if (!is_playing_ || audio_file_->getTotalSamples() == 0) {
        // Already zeroed, just return
        return frames;
    }
    
    std::lock_guard<std::mutex> lock(deck_mutex_);
    
    // Bypass SoundTouch when tempo is 1.0 - read directly from audio file
    // This eliminates SoundTouch's internal latency for perfect sync
    if (std::abs(tempo_ - 1.0) < 0.001 && std::abs(pitch_semitones_) < 0.1) {
        int64_t remaining = audio_file_->getTotalSamples() - sample_position_;
        if (remaining <= 0) {
            is_playing_ = false;
            return frames;
        }
        
        int to_read = std::min<int>(frames, static_cast<int>(remaining));
        const float* source = audio_file_->getData() + (sample_position_ * 2);
        
        // Copy directly to output
        memcpy(output, source, to_read * 2 * sizeof(float));
        sample_position_ += to_read;
        
        // Apply volume and EQ
        applyEQ(output, to_read);
        for (int i = 0; i < to_read * 2; ++i) {
            output[i] *= volume_;
        }
        
        return frames;
    }
    
    // Feed SoundTouch with source samples
    const int CHUNK_SIZE = 4096;
    while (soundtouch_->numSamples() < static_cast<unsigned int>(frames)) {
        int64_t remaining = audio_file_->getTotalSamples() - sample_position_;
        if (remaining <= 0) {
            // End of track
            is_playing_ = false;
            break;
        }
        
        int to_read = std::min<int>(CHUNK_SIZE, static_cast<int>(remaining));
        const float* source = audio_file_->getData() + (sample_position_ * 2);
        
        soundtouch_->putSamples(source, to_read);
        sample_position_ += to_read;
    }
    
    // Read processed samples from SoundTouch
    int received = soundtouch_->receiveSamples(output, frames);
    
    // Apply volume and EQ only to received samples
    if (received > 0) {
        applyEQ(output, received);
        for (int i = 0; i < received * 2; i++) {
            output[i] *= volume_;
        }
    }
    
    // Remainder is already zeroed from memset above
    
    return frames;
}

void Deck::applyEQ(float* buffer, int frames) {
    // Simple 3-band EQ using biquad filters
    // For now, just apply gain to approximate frequency bands
    // TODO: Implement proper biquad filters
    
    // This is a simplified version - in production you'd want proper filters
    for (int i = 0; i < frames * 2; i += 2) {
        float left = buffer[i];
        float right = buffer[i + 1];
        
        // Apply EQ gains (simplified - not frequency-specific yet)
        float avg_gain = (eq_low_ + eq_mid_ + eq_high_) / 3.0f;
        buffer[i] = left * avg_gain;
        buffer[i + 1] = right * avg_gain;
    }
}

} // namespace dj
