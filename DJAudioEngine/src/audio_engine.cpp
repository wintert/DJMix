#include "dj_audio_engine.h"
#include "dj_audio_internal.h"
#include <portaudio.h>
#include <memory>
#include <vector>

namespace dj {

// Global engine state
struct EngineState {
    std::unique_ptr<Deck> decks[2];
    std::unique_ptr<Mixer> mixer;
    std::unique_ptr<SyncManager> sync_manager;
    
    PaStream* stream;
    int sample_rate;
    int buffer_size;
    
    position_callback_t position_callback;
    track_ended_callback_t track_ended_callback;
    
    int callback_counter;  // For throttling UI callbacks
};

static EngineState* g_engine = nullptr;

// PortAudio callback
static int audioCallback(
    const void* inputBuffer,
    void* outputBuffer,
    unsigned long framesPerBuffer,
    const PaStreamCallbackTimeInfo* timeInfo,
    PaStreamCallbackFlags statusFlags,
    void* userData)
{
    EngineState* engine = static_cast<EngineState*>(userData);
    float* output = static_cast<float*>(outputBuffer);
    
    // Update sync before mixing
    Deck* deck_array[2] = { engine->decks[0].get(), engine->decks[1].get() };
    engine->sync_manager->update(deck_array);
    
    // Mix both decks
    engine->mixer->mix(
        engine->decks[0].get(),
        engine->decks[1].get(),
        output,
        framesPerBuffer
    );
    
    // Throttle position callbacks (update every ~10 callbacks = ~100ms at 512 samples)
    engine->callback_counter++;
    if (engine->callback_counter >= 10) {
        engine->callback_counter = 0;
        
        if (engine->position_callback) {
            engine->position_callback(0, engine->decks[0]->getPosition());
            engine->position_callback(1, engine->decks[1]->getPosition());
        }
    }
    
    return paContinue;
}

} // namespace dj

// C API Implementation
extern "C" {

DJ_API int engine_init(int sample_rate, int buffer_size) {
    if (dj::g_engine) {
        return -1;  // Already initialized
    }
    
    // Initialize PortAudio
    PaError err = Pa_Initialize();
    if (err != paNoError) {
        return -1;
    }
    
    // Create engine state
    dj::g_engine = new dj::EngineState();
    dj::g_engine->sample_rate = sample_rate;
    dj::g_engine->buffer_size = buffer_size;
    dj::g_engine->stream = nullptr;
    dj::g_engine->position_callback = nullptr;
    dj::g_engine->track_ended_callback = nullptr;
    dj::g_engine->callback_counter = 0;
    
    // Create decks
    dj::g_engine->decks[0] = std::make_unique<dj::Deck>(sample_rate);
    dj::g_engine->decks[1] = std::make_unique<dj::Deck>(sample_rate);
    
    // Create mixer and sync manager
    dj::g_engine->mixer = std::make_unique<dj::Mixer>();
    dj::g_engine->sync_manager = std::make_unique<dj::SyncManager>();
    
    return 0;
}

DJ_API void engine_shutdown() {
    if (!dj::g_engine) return;
    
    engine_stop();
    
    delete dj::g_engine;
    dj::g_engine = nullptr;
    
    Pa_Terminate();
}

DJ_API int engine_start() {
    if (!dj::g_engine || dj::g_engine->stream) {
        return -1;
    }
    
    // Open default output device with ASIO if available
    PaStreamParameters outputParams;
    outputParams.device = Pa_GetDefaultOutputDevice();
    
    // Try to find ASIO device
    int deviceCount = Pa_GetDeviceCount();
    for (int i = 0; i < deviceCount; i++) {
        const PaDeviceInfo* deviceInfo = Pa_GetDeviceInfo(i);
        const PaHostApiInfo* hostApiInfo = Pa_GetHostApiInfo(deviceInfo->hostApi);
        
        if (hostApiInfo->type == paASIO) {
            outputParams.device = i;
            break;
        }
    }
    
    if (outputParams.device == paNoDevice) {
        return -1;
    }
    
    const PaDeviceInfo* deviceInfo = Pa_GetDeviceInfo(outputParams.device);
    outputParams.channelCount = 2;
    outputParams.sampleFormat = paFloat32;
    outputParams.suggestedLatency = deviceInfo->defaultLowOutputLatency;
    outputParams.hostApiSpecificStreamInfo = nullptr;
    
    // Open stream
    PaError err = Pa_OpenStream(
        &dj::g_engine->stream,
        nullptr,  // No input
        &outputParams,
        dj::g_engine->sample_rate,
        dj::g_engine->buffer_size,
        paClipOff,
        dj::audioCallback,
        dj::g_engine
    );
    
    if (err != paNoError) {
        return -1;
    }
    
    // Start stream
    err = Pa_StartStream(dj::g_engine->stream);
    if (err != paNoError) {
        Pa_CloseStream(dj::g_engine->stream);
        dj::g_engine->stream = nullptr;
        return -1;
    }
    
    return 0;
}

DJ_API void engine_stop() {
    if (!dj::g_engine || !dj::g_engine->stream) return;
    
    Pa_StopStream(dj::g_engine->stream);
    Pa_CloseStream(dj::g_engine->stream);
    dj::g_engine->stream = nullptr;
}

// Deck operations
DJ_API int deck_load_track(int deck_id, const char* file_path) {
    if (!dj::g_engine || deck_id < 0 || deck_id > 1 || !file_path) {
        return -1;
    }
    
    return dj::g_engine->decks[deck_id]->loadTrack(file_path) ? 0 : -1;
}

DJ_API void deck_unload_track(int deck_id) {
    if (!dj::g_engine || deck_id < 0 || deck_id > 1) return;
    dj::g_engine->decks[deck_id]->unloadTrack();
}

DJ_API void deck_play(int deck_id) {
    if (!dj::g_engine || deck_id < 0 || deck_id > 1) return;
    dj::g_engine->decks[deck_id]->play();
}

DJ_API void deck_play_synced(int deck_id, int master_deck_id) {
    if (!dj::g_engine || deck_id < 0 || deck_id > 1 || master_deck_id < 0 || master_deck_id > 1) return;
    
    dj::Deck* master = dj::g_engine->decks[master_deck_id].get();
    dj::Deck* slave = dj::g_engine->decks[deck_id].get();
    
    // Match tempo
    double master_bpm = master->getBPM();
    double slave_bpm = slave->getBPM();
    if (master_bpm > 0 && slave_bpm > 0) {
        slave->setTempo(master_bpm / slave_bpm);
    }
    
    // Get master position and add latency compensation
    // Audio buffer latency = ~2-3 buffers * 512 samples = ~1536 samples (~35ms at 44100Hz)
    int64_t latency_compensation = 2048;  // ~46ms - tuned for minimal offset
    int64_t master_pos = master->getSamplePosition() + latency_compensation;
    slave->play(master_pos);
}

DJ_API void deck_pause(int deck_id) {
    if (!dj::g_engine || deck_id < 0 || deck_id > 1) return;
    dj::g_engine->decks[deck_id]->pause();
}

DJ_API void deck_stop(int deck_id) {
    if (!dj::g_engine || deck_id < 0 || deck_id > 1) return;
    dj::g_engine->decks[deck_id]->stop();
}

DJ_API void deck_set_position(int deck_id, double position_seconds) {
    if (!dj::g_engine || deck_id < 0 || deck_id > 1) return;
    dj::g_engine->decks[deck_id]->setPosition(position_seconds);
}

DJ_API double deck_get_position(int deck_id) {
    if (!dj::g_engine || deck_id < 0 || deck_id > 1) return 0.0;
    return dj::g_engine->decks[deck_id]->getPosition();
}

DJ_API double deck_get_duration(int deck_id) {
    if (!dj::g_engine || deck_id < 0 || deck_id > 1) return 0.0;
    return dj::g_engine->decks[deck_id]->getDuration();
}

DJ_API int deck_is_playing(int deck_id) {
    if (!dj::g_engine || deck_id < 0 || deck_id > 1) return 0;
    return dj::g_engine->decks[deck_id]->isPlaying() ? 1 : 0;
}

// Deck parameters
DJ_API void deck_set_volume(int deck_id, float volume) {
    if (!dj::g_engine || deck_id < 0 || deck_id > 1) return;
    dj::g_engine->decks[deck_id]->setVolume(volume);
}

DJ_API void deck_set_tempo(int deck_id, double tempo) {
    if (!dj::g_engine || deck_id < 0 || deck_id > 1) return;
    dj::g_engine->decks[deck_id]->setTempo(tempo);
}

DJ_API void deck_set_pitch(int deck_id, double semitones) {
    if (!dj::g_engine || deck_id < 0 || deck_id > 1) return;
    dj::g_engine->decks[deck_id]->setPitch(semitones);
}

DJ_API void deck_set_bpm(int deck_id, double bpm) {
    if (!dj::g_engine || deck_id < 0 || deck_id > 1) return;
    dj::g_engine->decks[deck_id]->setBPM(bpm);
}

DJ_API double deck_get_bpm(int deck_id) {
    if (!dj::g_engine || deck_id < 0 || deck_id > 1) return 0.0;
    return dj::g_engine->decks[deck_id]->getBPM();
}

DJ_API void deck_set_beat_offset(int deck_id, double offset_seconds) {
    if (!dj::g_engine || deck_id < 0 || deck_id > 1) return;
    dj::g_engine->decks[deck_id]->setBeatOffset(offset_seconds);
}

// EQ
DJ_API void deck_set_eq_low(int deck_id, float gain) {
    if (!dj::g_engine || deck_id < 0 || deck_id > 1) return;
    dj::g_engine->decks[deck_id]->setEQLow(gain);
}

DJ_API void deck_set_eq_mid(int deck_id, float gain) {
    if (!dj::g_engine || deck_id < 0 || deck_id > 1) return;
    dj::g_engine->decks[deck_id]->setEQMid(gain);
}

DJ_API void deck_set_eq_high(int deck_id, float gain) {
    if (!dj::g_engine || deck_id < 0 || deck_id > 1) return;
    dj::g_engine->decks[deck_id]->setEQHigh(gain);
}

// Mixer
DJ_API void mixer_set_crossfader(float position) {
    if (!dj::g_engine) return;
    dj::g_engine->mixer->setCrossfader(position);
}

// Sync
DJ_API void sync_enable(int slave_deck_id, int master_deck_id) {
    if (!dj::g_engine) return;
    dj::g_engine->sync_manager->enable(slave_deck_id, master_deck_id);
}

DJ_API void sync_disable(int deck_id) {
    if (!dj::g_engine) return;
    dj::g_engine->sync_manager->disable(deck_id);
}

DJ_API void sync_align_now(int slave_deck_id, int master_deck_id) {
    if (!dj::g_engine || slave_deck_id < 0 || slave_deck_id > 1 || 
        master_deck_id < 0 || master_deck_id > 1) return;
    
    dj::g_engine->sync_manager->alignNow(
        dj::g_engine->decks[slave_deck_id].get(),
        dj::g_engine->decks[master_deck_id].get()
    );
}

// Callbacks
DJ_API void set_position_callback(position_callback_t callback) {
    if (!dj::g_engine) return;
    dj::g_engine->position_callback = callback;
}

DJ_API void set_track_ended_callback(track_ended_callback_t callback) {
    if (!dj::g_engine) return;
    dj::g_engine->track_ended_callback = callback;
}

} // extern "C"
