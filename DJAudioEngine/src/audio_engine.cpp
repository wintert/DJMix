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
        fprintf(logFile, "=== DJ-STYLE SYNC ===\n");
        fprintf(logFile, "MASTER (deck %d): BPM=%.1f, tempo=%.3f, effective_BPM=%.1f\n", 
                master_deck_id, master_bpm, master->getTempo(), master_bpm * master->getTempo());
        fprintf(logFile, "SLAVE  (deck %d): BPM=%.1f (next song)\n", deck_id, slave_bpm);
    }
    
    if (master_bpm <= 0 || slave_bpm <= 0) {
        if (logFile) {
            fprintf(logFile, "No BPM, just playing\n");
            fclose(logFile);
        }
        slave->play();
        return;
    }
    
    // Step 1: Match tempo
    double master_tempo = master->getTempo();
    double effective_master_bpm = master_bpm * master_tempo;
    double tempo_ratio = effective_master_bpm / slave_bpm;
    double effective_slave_bpm = slave_bpm * tempo_ratio;
    
    if (logFile) {
        fprintf(logFile, "TEMPO CALC: effective_master=%.1f / slave=%.1f = ratio %.4f\n", 
                effective_master_bpm, slave_bpm, tempo_ratio);
        fprintf(logFile, "RESULT: Slave will play at tempo=%.3f (%.1f%% speed), effective_BPM=%.1f\n",
                tempo_ratio, tempo_ratio * 100, effective_slave_bpm);
        fflush(logFile);
    }
    
    slave->setTempo(tempo_ratio);
    
    // Same tempo - alignNow handles it
    if (std::abs(tempo_ratio - 1.0) < 0.01) {
        if (logFile) {
            fprintf(logFile, "Same tempo, using alignNow\n");
            fclose(logFile);
        }
        slave->play();
        return;
    }
    
    int sample_rate = 44100;
    
    // Step 2: Get beat offsets - the ACTUAL position of the first kick in each track
    double master_first_kick = master->getBeatOffset();  // e.g., 0.058 sec
    double slave_first_kick = slave->getBeatOffset();    // e.g., 0.449 sec
    
    // Step 3: Calculate beat intervals
    // CRITICAL: Use EFFECTIVE master BPM (accounting for tempo) for real-time calculations
    double master_spb = 60.0 / effective_master_bpm;  // Seconds per beat in master (REAL-TIME)
    double slave_spb = 60.0 / slave_bpm;              // Seconds per beat in slave (SOURCE time)
    
    // Step 4: Find where master is in its beat cycle
    // Master's beat grid starts at master_first_kick and repeats every master_spb
    double master_pos = master->getPosition();
    double master_time_since_first_kick = master_pos - master_first_kick;
    
    // How far into the current beat cycle? (phase)
    double master_phase = fmod(master_time_since_first_kick, master_spb);
    if (master_phase < 0) master_phase += master_spb;
    
    // Time until master's next kick
    double time_to_master_kick = master_spb - master_phase;
    
    if (logFile) {
        fprintf(logFile, "Master: pos=%.3f, first_kick=%.3f, tempo=%.3f, eff_bpm=%.1f, phase=%.3fms, time_to_kick=%.1fms\n",
                master_pos, master_first_kick, master_tempo, effective_master_bpm, master_phase * 1000, time_to_master_kick * 1000);
    }
    
    // Step 5: DJ-STYLE CUE AND START
    // The C# code has already cued the slave to the mix-in point (e.g., 14.86s)
    // We need to find the nearest beat-aligned position to START from
    // AND account for where the master currently is in its beat cycle
    
    double slave_current_pos = slave->getPosition();  // Current cue position from C#
    // slave_spb already calculated above
    
    // Find which beat number we're closest to
    double time_since_first_beat = slave_current_pos - slave_first_kick;
    int beat_number = static_cast<int>(round(time_since_first_beat / slave_spb));
    if (beat_number < 0) beat_number = 0;
    
    // Calculate the exact beat position
    double slave_beat_pos = slave_first_kick + (beat_number * slave_spb);
    
    // KEY FIX: Offset by master's current phase
    // If master is 200ms into a 476ms beat, slave should also start 200ms into its beat
    // But we need to convert master's phase (real-time) to slave's source time
    double master_phase_in_source_time = master_phase / tempo_ratio;  // Convert to slave source time
    double slave_start_pos = slave_beat_pos + master_phase_in_source_time;
    
    int64_t slave_start_samples = static_cast<int64_t>(slave_start_pos * sample_rate);
    
    if (logFile) {
        fprintf(logFile, "Slave: cued_pos=%.3f, first_kick=%.3f, spb=%.3f\n", 
                slave_current_pos, slave_first_kick, slave_spb);
        fprintf(logFile, "Slave: beat_number=%d, beat_pos=%.3f, +master_phase=%.3f => start=%.3f sec\n",
                beat_number, slave_beat_pos, master_phase_in_source_time, slave_start_pos);
        fclose(logFile);
    }
    
    slave->play(slave_start_samples);
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

DJ_API double deck_get_tempo(int deck_id) {
    if (!dj::g_engine || deck_id < 0 || deck_id > 1) return 1.0;
    return dj::g_engine->decks[deck_id]->getTempo();
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
