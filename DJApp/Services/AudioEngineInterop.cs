using System;
using System.Runtime.InteropServices;

namespace DJAutoMixApp.Services
{
    /// <summary>
    /// P/Invoke wrapper for the C++ audio engine
    /// </summary>
    public static class AudioEngineInterop
    {
        private const string DllName = "DJAudioEngine.dll";

        // Engine lifecycle
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int engine_init(int sampleRate, int bufferSize);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void engine_shutdown();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int engine_start();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void engine_stop();

        // Deck operations
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int deck_load_track(int deckId, [MarshalAs(UnmanagedType.LPStr)] string filePath);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void deck_unload_track(int deckId);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void deck_play(int deckId);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void deck_play_synced(int deckId, int masterDeckId);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void deck_pause(int deckId);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void deck_stop(int deckId);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void deck_set_position(int deckId, double positionSeconds);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern double deck_get_position(int deckId);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern double deck_get_duration(int deckId);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int deck_is_playing(int deckId);

        // Deck parameters
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void deck_set_volume(int deckId, float volume);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void deck_set_tempo(int deckId, double tempo);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void deck_set_pitch(int deckId, double semitones);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void deck_set_bpm(int deckId, double bpm);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern double deck_get_bpm(int deckId);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void deck_set_beat_offset(int deckId, double offsetSeconds);

        // EQ
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void deck_set_eq_low(int deckId, float gain);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void deck_set_eq_mid(int deckId, float gain);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void deck_set_eq_high(int deckId, float gain);

        // Mixer
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mixer_set_crossfader(float position);

        // Sync
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sync_enable(int slaveDeckId, int masterDeckId);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sync_disable(int deckId);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sync_align_now(int slaveDeckId, int masterDeckId);

        // Callbacks
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void PositionCallback(int deckId, double position);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void TrackEndedCallback(int deckId);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void set_position_callback(PositionCallback callback);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void set_track_ended_callback(TrackEndedCallback callback);
    }
}
