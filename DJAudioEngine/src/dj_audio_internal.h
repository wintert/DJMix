#ifndef DJ_AUDIO_INTERNAL_H
#define DJ_AUDIO_INTERNAL_H

#include <cstdint>
#include <vector>
#include <mutex>
#include <atomic>
#include <memory>

// Forward declarations
namespace soundtouch {
    class SoundTouch;
}

namespace dj {

// Audio file loader
class AudioFile {
public:
    AudioFile();
    ~AudioFile();
    
    bool load(const char* filepath);
    void unload();
    
    int64_t getTotalSamples() const { return total_samples_; }
    int getSampleRate() const { return sample_rate_; }
    int getChannels() const { return channels_; }
    double getDurationSeconds() const;
    
    const float* getData() const { return audio_data_.data(); }
    
private:
    std::vector<float> audio_data_;  // Interleaved stereo
    int64_t total_samples_;          // Total sample frames
    int sample_rate_;
    int channels_;
};

// Deck class
class Deck {
public:
    Deck(int sample_rate);
    ~Deck();
    
    bool loadTrack(const char* filepath);
    void unloadTrack();
    
    void play(int64_t startPosition = -1);
    void pause();
    void stop();
    bool isPlaying() const { return is_playing_; }
    
    void setPosition(double seconds);
    double getPosition() const;
    double getDuration() const;
    
    void setVolume(float volume) { volume_ = volume; }
    void setTempo(double tempo);
    void setPitch(double semitones);
    void setBPM(double bpm) { bpm_ = bpm; }
    double getBPM() const { return bpm_; }
    void setBeatOffset(double offset) { beat_offset_ = offset; }
    double getBeatOffset() const { return beat_offset_; }
    
    void setEQLow(float gain) { eq_low_ = gain; }
    void setEQMid(float gain) { eq_mid_ = gain; }
    void setEQHigh(float gain) { eq_high_ = gain; }
    
    // Audio processing
    int readSamples(float* output, int frames);
    
    // Access to loaded audio data (for BPM analysis)
    bool isLoaded() const { return audio_file_ != nullptr && audio_file_->getTotalSamples() > 0; }
    AudioFile* getAudioFile() const { return audio_file_.get(); }
    
    // Sync support
    int64_t getSamplePosition() const { return sample_position_; }
    void setSamplePosition(int64_t pos, bool forceSync = false);
    double getPhase() const;  // 0.0 to 1.0 within beat
    
private:
    void applyEQ(float* buffer, int frames);
    
    int sample_rate_;
    std::unique_ptr<AudioFile> audio_file_;
    std::unique_ptr<soundtouch::SoundTouch> soundtouch_;
    
    std::atomic<bool> is_playing_;
    std::atomic<int64_t> sample_position_;  // In source samples
    
    float volume_;
    double tempo_;
    double pitch_semitones_;
    double bpm_;
    double beat_offset_;  // In seconds
    
    float eq_low_;
    float eq_mid_;
    float eq_high_;
    
    std::mutex deck_mutex_;
};

// Mixer class
class Mixer {
public:
    Mixer();
    
    void setCrossfader(float position) { crossfader_position_ = position; }
    float getCrossfader() const { return crossfader_position_; }
    
    void mix(Deck* deck_a, Deck* deck_b, float* output, int frames);
    
private:
    float crossfader_position_;  // 0.0 = A, 1.0 = B
};

// Sync manager
class SyncManager {
public:
    SyncManager();
    
    void enable(int slave_deck_id, int master_deck_id);
    void disable(int deck_id);
    void alignNow(Deck* slave, Deck* master);  // Immediate one-time alignment
    
    void update(Deck* decks[2]);
    
private:
    struct SyncState {
        bool enabled;
        int master_deck_id;
        int slave_deck_id;
    };
    
    SyncState sync_state_;
    std::mutex sync_mutex_;
};

// Global engine state - shared across all source files
struct EngineState {
    std::unique_ptr<Deck> decks[2];
    std::unique_ptr<Mixer> mixer;
    std::unique_ptr<SyncManager> sync_manager;
    
    void* stream;  // PaStream*, using void* to avoid PortAudio include in header
    int sample_rate;
    int buffer_size;
    
    void* position_callback;  // Callbacks stored as void*
    void* track_ended_callback;
    
    int callback_counter;
};

extern EngineState* g_engine;

} // namespace dj

#endif // DJ_AUDIO_INTERNAL_H
