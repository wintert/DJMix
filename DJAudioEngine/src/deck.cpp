#include "dj_audio_internal.h"
#include <SoundTouch.h>
#include <algorithm>
#include <cmath>
#include <cstdio>

namespace dj {

Deck::Deck(int sample_rate)
    : sample_rate_(sample_rate)
    , audio_file_(std::make_unique<AudioFile>())
    , soundtouch_(std::make_unique<soundtouch::SoundTouch>())
    , is_playing_(false)
    , sample_position_(0)
    , output_sample_position_(0)
    , track_ended_(false)
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
    
    // DJ-optimized settings for MINIMUM LATENCY
    // Trade some quality for tighter beat sync
    soundtouch_->setSetting(SETTING_USE_AA_FILTER, 1);       // Anti-alias filter on
    soundtouch_->setSetting(SETTING_AA_FILTER_LENGTH, 16);   // Shorter AA filter (was 32)
    soundtouch_->setSetting(SETTING_SEQUENCE_MS, 20);        // Shorter sequences (was 40)
    soundtouch_->setSetting(SETTING_SEEKWINDOW_MS, 8);       // Smaller seek window (was 15)
    soundtouch_->setSetting(SETTING_OVERLAP_MS, 4);          // Less overlap (was 8)
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
    output_sample_position_ = 0;
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
    
    // Log tempo change
    FILE* logFile = fopen("c:\\Apps\\DJApp\\cpp_debug.log", "a");
    if (logFile) {
        fprintf(logFile, "Deck::setTempo: tempo=%.3f (%.1f%% speed)\\n", tempo_, tempo_ * 100);
        fclose(logFile);
    }
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
    
    // Calculate samples per beat using EFFECTIVE BPM (accounting for tempo)
    double effective_bpm = bpm_ * tempo_;
    double seconds_per_beat = 60.0 / effective_bpm;
    int64_t samples_per_beat = static_cast<int64_t>(seconds_per_beat * sample_rate_);
    
    if (samples_per_beat <= 0) return 0.0;
    
    // Use OUTPUT position (real playback time), not source position
    // Also adjust beat offset for tempo
    int64_t offset_samples = static_cast<int64_t>((beat_offset_ / tempo_) * sample_rate_);
    int64_t adjusted_position = output_sample_position_ - offset_samples;
    
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
            track_ended_ = true;
            return frames;
        }
        
        int to_read = std::min<int>(frames, static_cast<int>(remaining));
        const float* source = audio_file_->getData() + (sample_position_ * 2);
        
        // Copy directly to output
        memcpy(output, source, to_read * 2 * sizeof(float));
        sample_position_ += to_read;
        output_sample_position_ += to_read;  // Track output time (same as source when tempo=1.0)
        
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
            track_ended_ = true;
            break;
        }
        
        int to_read = std::min<int>(CHUNK_SIZE, static_cast<int>(remaining));
        const float* source = audio_file_->getData() + (sample_position_ * 2);
        
        soundtouch_->putSamples(source, to_read);
        sample_position_ += to_read;
    }
    
    // Read processed samples from SoundTouch
    int received = soundtouch_->receiveSamples(output, frames);
    
    // Track output time - this is the ACTUAL samples sent to speakers
    output_sample_position_ += received;
    
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

// Biquad filter implementations for 3-band EQ
void Deck::BiquadFilter::reset() {
    lx1 = lx2 = ly1 = ly2 = 0;
    rx1 = rx2 = ry1 = ry2 = 0;
}

void Deck::BiquadFilter::process(float* buffer, int frames) {
    for (int i = 0; i < frames; i++) {
        float left_in = buffer[i * 2];
        float left_out = static_cast<float>(b0 * left_in + b1 * lx1 + b2 * lx2 - a1 * ly1 - a2 * ly2);
        lx2 = lx1; lx1 = left_in;
        ly2 = ly1; ly1 = left_out;
        buffer[i * 2] = left_out;
        
        float right_in = buffer[i * 2 + 1];
        float right_out = static_cast<float>(b0 * right_in + b1 * rx1 + b2 * rx2 - a1 * ry1 - a2 * ry2);
        rx2 = rx1; rx1 = right_in;
        ry2 = ry1; ry1 = right_out;
        buffer[i * 2 + 1] = right_out;
    }
}

void Deck::BiquadFilter::configureLowShelf(double sampleRate, double gain_linear) {
    double gain_db = (gain_linear > 0.001f) ? 20.0 * log10(gain_linear) : -60.0;
    double A = pow(10.0, gain_db / 40.0);
    double w0 = 2.0 * M_PI * 250.0 / sampleRate;
    double alpha = sin(w0) / (2.0 * 0.707);
    double cos_w0 = cos(w0);
    
    double norm = (A + 1.0) + (A - 1.0) * cos_w0 + 2.0 * sqrt(A) * alpha;
    b0 = A * ((A + 1.0) - (A - 1.0) * cos_w0 + 2.0 * sqrt(A) * alpha) / norm;
    b1 = 2.0 * A * ((A - 1.0) - (A + 1.0) * cos_w0) / norm;
    b2 = A * ((A + 1.0) - (A - 1.0) * cos_w0 - 2.0 * sqrt(A) * alpha) / norm;
    a1 = -2.0 * ((A + 1.0) + (A - 1.0) * cos_w0) / norm;
    a2 = ((A + 1.0) + (A - 1.0) * cos_w0 - 2.0 * sqrt(A) * alpha) / norm;
}

void Deck::BiquadFilter::configurePeaking(double sampleRate, double gain_linear) {
    double gain_db = (gain_linear > 0.001f) ? 20.0 * log10(gain_linear) : -60.0;
    double A = pow(10.0, gain_db / 40.0);
    double w0 = 2.0 * M_PI * 1000.0 / sampleRate;
    double alpha = sin(w0) / (2.0 * 0.707);
    double cos_w0 = cos(w0);
    
    double norm = 1.0 + alpha / A;
    b0 = (1.0 + alpha * A) / norm;
    b1 = (-2.0 * cos_w0) / norm;
    b2 = (1.0 - alpha * A) / norm;
    a1 = (-2.0 * cos_w0) / norm;
    a2 = (1.0 + alpha / A) / norm;
}

void Deck::BiquadFilter::configureHighShelf(double sampleRate, double gain_linear) {
    double gain_db = (gain_linear > 0.001f) ? 20.0 * log10(gain_linear) : -60.0;
    double A = pow(10.0, gain_db / 40.0);
    double w0 = 2.0 * M_PI * 2500.0 / sampleRate;
    double alpha = sin(w0) / (2.0 * 0.707);
    double cos_w0 = cos(w0);
    
    double norm = (A + 1.0) - (A - 1.0) * cos_w0 + 2.0 * sqrt(A) * alpha;
    b0 = A * ((A + 1.0) + (A - 1.0) * cos_w0 + 2.0 * sqrt(A) * alpha) / norm;
    b1 = -2.0 * A * ((A - 1.0) + (A + 1.0) * cos_w0) / norm;
    b2 = A * ((A + 1.0) + (A - 1.0) * cos_w0 - 2.0 * sqrt(A) * alpha) / norm;
    a1 = 2.0 * ((A - 1.0) - (A + 1.0) * cos_w0) / norm;
    a2 = ((A + 1.0) + (A - 1.0) * cos_w0 - 2.0 * sqrt(A) * alpha) / norm;
}

void Deck::applyEQ(float* buffer, int frames) {
    // Reset filter state on first call or after track load
    if (eq_filters_dirty_) {
        lowFilter_ = BiquadFilter();
        midFilter_ = BiquadFilter();
        highFilter_ = BiquadFilter();
        eq_filters_dirty_ = false;
    }
    
    if (last_eq_low_ != eq_low_) {
        lowFilter_.configureLowShelf(sample_rate_, eq_low_);
        last_eq_low_ = eq_low_;
    }
    if (last_eq_mid_ != eq_mid_) {
        midFilter_.configurePeaking(sample_rate_, eq_mid_);
        last_eq_mid_ = eq_mid_;
    }
    if (last_eq_high_ != eq_high_) {
        highFilter_.configureHighShelf(sample_rate_, eq_high_);
        last_eq_high_ = eq_high_;
    }
    
    lowFilter_.process(buffer, frames);
    midFilter_.process(buffer, frames);
    highFilter_.process(buffer, frames);
}

} // namespace dj
