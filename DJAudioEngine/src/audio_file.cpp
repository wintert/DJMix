#include "dj_audio_internal.h"

// dr_libs for audio file loading
#define DR_MP3_IMPLEMENTATION
#define DR_WAV_IMPLEMENTATION
#define DR_FLAC_IMPLEMENTATION
#include "dr_mp3.h"
#include "dr_wav.h"
#include "dr_flac.h"

#include <cstring>
#include <cctype>
#include <algorithm>

namespace dj {

AudioFile::AudioFile() 
    : total_samples_(0)
    , sample_rate_(0)
    , channels_(0)
{
}

AudioFile::~AudioFile() {
    unload();
}

bool AudioFile::load(const char* filepath) {
    unload();
    
    // Determine file type by extension
    const char* ext = strrchr(filepath, '.');
    if (!ext) return false;
    
    // Convert to lowercase for comparison
    char ext_lower[10] = {0};
    for (int i = 0; i < 9 && ext[i]; i++) {
        ext_lower[i] = tolower(ext[i]);
    }
    
    drwav_uint64 frames = 0;
    unsigned int channels = 0;
    unsigned int sample_rate = 0;
    float* data = nullptr;
    
    if (strcmp(ext_lower, ".mp3") == 0) {
        // Load MP3
        drmp3_config config;
        data = drmp3_open_file_and_read_pcm_frames_f32(filepath, &config, &frames, nullptr);
        if (!data) return false;
        channels = config.channels;
        sample_rate = config.sampleRate;
    }
    else if (strcmp(ext_lower, ".wav") == 0) {
        // Load WAV
        data = drwav_open_file_and_read_pcm_frames_f32(filepath, &channels, &sample_rate, &frames, nullptr);
        if (!data) return false;
    }
    else if (strcmp(ext_lower, ".flac") == 0) {
        // Load FLAC
        data = drflac_open_file_and_read_pcm_frames_f32(filepath, &channels, &sample_rate, &frames, nullptr);
        if (!data) return false;
    }
    else {
        return false;
    }
    
    // Convert to stereo if mono
    if (channels == 1) {
        // Allocate stereo buffer
        audio_data_.resize(frames * 2);
        for (drwav_uint64 i = 0; i < frames; i++) {
            audio_data_[i * 2] = data[i];      // Left
            audio_data_[i * 2 + 1] = data[i];  // Right
        }
        channels_ = 2;
    }
    else if (channels == 2) {
        // Already stereo
        audio_data_.assign(data, data + frames * 2);
        channels_ = 2;
    }
    else {
        // Unsupported channel count
        drwav_free(data, nullptr);
        return false;
    }
    
    total_samples_ = frames;
    sample_rate_ = sample_rate;
    
    // Free the original data
    drwav_free(data, nullptr);
    
    return true;
}

void AudioFile::unload() {
    audio_data_.clear();
    total_samples_ = 0;
    sample_rate_ = 0;
    channels_ = 0;
}

double AudioFile::getDurationSeconds() const {
    if (sample_rate_ == 0) return 0.0;
    return static_cast<double>(total_samples_) / sample_rate_;
}

} // namespace dj
