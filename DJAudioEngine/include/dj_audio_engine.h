#ifndef DJ_AUDIO_ENGINE_H
#define DJ_AUDIO_ENGINE_H

#ifdef __cplusplus
extern "C" {
#endif

#ifdef DJ_AUDIO_ENGINE_EXPORTS
#define DJ_API __declspec(dllexport)
#else
#define DJ_API __declspec(dllimport)
#endif

// Engine lifecycle
DJ_API int engine_init(int sample_rate, int buffer_size);
DJ_API void engine_shutdown();
DJ_API int engine_start();
DJ_API void engine_stop();

// Deck operations (deck_id: 0 = Deck A, 1 = Deck B)
DJ_API int deck_load_track(int deck_id, const char* file_path);
DJ_API void deck_unload_track(int deck_id);
DJ_API void deck_play(int deck_id);
DJ_API void deck_pause(int deck_id);
DJ_API void deck_stop(int deck_id);
DJ_API void deck_set_position(int deck_id, double position_seconds);
DJ_API double deck_get_position(int deck_id);
DJ_API double deck_get_duration(int deck_id);
DJ_API int deck_is_playing(int deck_id);

// Deck parameters
DJ_API void deck_set_volume(int deck_id, float volume);  // 0.0 - 1.0
DJ_API void deck_set_tempo(int deck_id, double tempo);   // 0.5 - 2.0
DJ_API void deck_set_pitch(int deck_id, double semitones); // -12 to +12
DJ_API void deck_set_bpm(int deck_id, double bpm);
DJ_API double deck_get_bpm(int deck_id);
DJ_API void deck_set_beat_offset(int deck_id, double offset_seconds);

// EQ
DJ_API void deck_set_eq_low(int deck_id, float gain);   // 0.0 - 2.0
DJ_API void deck_set_eq_mid(int deck_id, float gain);
DJ_API void deck_set_eq_high(int deck_id, float gain);

// Mixer
DJ_API void mixer_set_crossfader(float position);  // 0.0 = A, 1.0 = B

// Sync
DJ_API void sync_enable(int slave_deck_id, int master_deck_id);
DJ_API void sync_disable(int deck_id);
DJ_API void sync_align_now(int slave_deck_id, int master_deck_id);  // Immediate one-time alignment

// Callbacks (for UI updates)
typedef void (*position_callback_t)(int deck_id, double position);
typedef void (*track_ended_callback_t)(int deck_id);
DJ_API void set_position_callback(position_callback_t callback);
DJ_API void set_track_ended_callback(track_ended_callback_t callback);

#ifdef __cplusplus
}
#endif

#endif // DJ_AUDIO_ENGINE_H
