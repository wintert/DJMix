#include "dj_audio_engine.h"
#include "dj_audio_internal.h"
#include <portaudio.h>
#include <memory>
#include <vector>

namespace dj {

// Global engine state
EngineState* g_engine = nullptr;

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
            auto callback = reinterpret_cast<position_callback_t>(engine->position_callback);
            callback(0, engine->decks[0]->getPosition());
            callback(1, engine->decks[1]->getPosition());
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
    
    FILE* logFile = fopen("c:\\Apps\\DJApp\\cpp_debug.log", "a");
    
    dj::Deck* master = dj::g_engine->decks[master_deck_id].get();
    dj::Deck* slave = dj::g_engine->decks[deck_id].get();
    
    double master_bpm = master->getBPM();
    double slave_bpm = slave->getBPM();
    
    if (logFile) {
        fprintf(logFile, "deck_play_synced: master_bpm=%.1f, slave_bpm=%.1f\n", master_bpm, slave_bpm);
    }
    
    if (master_bpm <= 0 || slave_bpm <= 0) {
        if (logFile) {
            fprintf(logFile, "deck_play_synced: No BPM, just playing\n");
            fclose(logFile);
        }
        slave->play();
        return;
    }
    
    // Match tempo - slave will play at master's BPM
    double tempo_ratio = master_bpm / slave_bpm;
    slave->setTempo(tempo_ratio);
    
    // For same or very similar BPMs (tempo_ratio ~1.0), alignNow already set the position
    // Just play without additional phase adjustment - this ensures same-song sync works
    if (std::abs(tempo_ratio - 1.0) < 0.01) {  // Within 1%
        if (logFile) {
            fprintf(logFile, "deck_play_synced: tempo_ratio=%.3f (~1.0), using alignNow position, just playing\n", tempo_ratio);
            fclose(logFile);
        }
        slave->play();
        return;
    }
    
    int sample_rate = 44100;
    
    // Get beat offsets (where first beat is in each track)
    double master_offset = master->getBeatOffset();
    double slave_offset = slave->getBeatOffset();
    
    // Calculate beat lengths
    double master_spb = 60.0 / master_bpm;  // master seconds per beat
    double slave_spb = 60.0 / slave_bpm;    // slave seconds per beat (before tempo change)
    
    // Master's position on its beat grid
    double master_pos = master->getPosition();
    double master_grid_pos = master_pos - master_offset;  // Position relative to beat grid
    double master_phase = fmod(master_grid_pos, master_spb);  // Time since last beat
    if (master_phase < 0) master_phase += master_spb;
    
    // How long until master's next beat?
    double master_time_to_next_beat = master_spb - master_phase;
    
    // The slave should be positioned so that it ALSO has the same time to its next beat
    // But slave's beat grid has a different timing (slave_spb)
    // We want: slave_time_to_next_beat = master_time_to_next_beat (after tempo adjustment)
    
    // After tempo adjustment, slave plays at master_spb timing
    // So we need slave to have: time_to_beat = master_time_to_next_beat
    
    // Current slave position (set by automix to mix-in point)
    double slave_pos = slave->getPosition();
    double slave_grid_pos = slave_pos - slave_offset;
    double slave_phase = fmod(slave_grid_pos, slave_spb);
    if (slave_phase < 0) slave_phase += slave_spb;
    double slave_time_to_next_beat = slave_spb - slave_phase;
    
    // How much to adjust slave? (in slave's original time domain)
    // We want slave's next beat to occur at the same real-time as master's
    double adjustment = slave_time_to_next_beat - master_time_to_next_beat;
    
    // Wrap to [-half_beat, +half_beat] for shortest adjustment
    if (adjustment > slave_spb / 2) adjustment -= slave_spb;
    if (adjustment < -slave_spb / 2) adjustment += slave_spb;
    
    int64_t adjustment_samples = static_cast<int64_t>(adjustment * sample_rate);
    
    // Calculate new position (moving backwards if adjustment is negative)
    int64_t slave_current_samples = static_cast<int64_t>(slave_pos * sample_rate);
    int64_t target_pos = slave_current_samples - adjustment_samples;  // Note: MINUS because we're adjusting time-to-beat
    
    // Ensure valid
    if (target_pos < 0) target_pos = 0;
    
    if (logFile) {
        fprintf(logFile, "deck_play_synced: master_offset=%.3f, slave_offset=%.3f\n", master_offset, slave_offset);
        fprintf(logFile, "deck_play_synced: master_time_to_beat=%.3f, slave_time_to_beat=%.3f\n", 
                master_time_to_next_beat, slave_time_to_next_beat);
        fprintf(logFile, "deck_play_synced: adjustment=%.3f sec, samples=%lld\n", 
                adjustment, (long long)adjustment_samples);
        fprintf(logFile, "deck_play_synced: starting slave at pos=%lld (%.2f sec)\n",
                (long long)target_pos, target_pos / (double)sample_rate);
        fclose(logFile);
    }
    
    slave->play(target_pos);
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
