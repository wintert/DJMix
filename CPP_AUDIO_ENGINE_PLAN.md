# DJ App - C++ Audio Engine Implementation Plan

## Overview

Replace the C#/NAudio audio engine with a native C++ engine using ASIO for sample-accurate beat synchronization. Keep the existing C# WPF UI.

## Why This Change?

The C#/NAudio approach has fundamental limitations:
- Garbage collection causes unpredictable pauses
- NAudio adds abstraction layers with variable latency
- Cannot achieve sample-accurate sync needed for DJ software
- No professional DJ software uses C# for audio - they all use C++

## Architecture

```
┌─────────────────────────────────────────────────────┐
│                 C# WPF UI (Keep)                    │
│   - MainWindow.xaml                                 │
│   - ViewModels (DeckViewModel, MainViewModel, etc)  │
│   - Playlist management                             │
└─────────────────────┬───────────────────────────────┘
                      │ P/Invoke calls
                      ▼
┌─────────────────────────────────────────────────────┐
│            DJAudioEngine.dll (NEW - C++)            │
│   - PortAudio (ASIO support)                        │
│   - SoundTouch (tempo/pitch)                        │
│   - libsndfile or dr_libs (audio file loading)      │
│   - Deck, Mixer, Sync logic                         │
└─────────────────────────────────────────────────────┘
                      │
                      ▼
┌─────────────────────────────────────────────────────┐
│              ASIO Driver (Low Latency)              │
└─────────────────────────────────────────────────────┘
```

## Project Structure

```
C:\Apps\DJApp\
├── DJApp\                      # Existing C# WPF project
│   ├── Services\
│   │   └── AudioEngineInterop.cs   # NEW: P/Invoke wrapper
│   ├── ViewModels\
│   └── ...
├── DJAudioEngine\              # NEW: C++ DLL project
│   ├── include\
│   │   └── dj_audio_engine.h   # Public C API
│   ├── src\
│   │   ├── audio_engine.cpp    # Main engine, PortAudio setup
│   │   ├── audio_file.cpp      # Audio file loading
│   │   ├── deck.cpp            # Deck implementation
│   │   ├── mixer.cpp           # Mixer with crossfader
│   │   ├── sync.cpp            # Beat sync logic
│   │   └── soundtouch_wrap.cpp # SoundTouch wrapper
│   ├── libs\                   # Third-party libraries
│   │   ├── portaudio\
│   │   ├── soundtouch\
│   │   └── dr_libs\            # Single-header audio loading
│   └── CMakeLists.txt
└── DJApp.sln
```

## Dependencies

### 1. PortAudio (Audio I/O with ASIO)
- Website: http://www.portaudio.com/
- Provides cross-platform audio with ASIO support on Windows
- Download pre-built or build from source with ASIO SDK

### 2. ASIO SDK (from Steinberg)
- Website: https://www.steinberg.net/developers/
- Required for ASIO support in PortAudio
- Free to download, requires registration

### 3. SoundTouch (Tempo/Pitch)
- Website: https://www.surina.net/soundtouch/
- Same library we use in C#, but native C++
- vcpkg: `vcpkg install soundtouch`

### 4. dr_libs (Audio File Loading)
- GitHub: https://github.com/mackron/dr_libs
- Single-header libraries for MP3, WAV, FLAC
- Just drop header files into project

## Implementation Steps

### Phase 1: Project Setup

#### Step 1.1: Create C++ Project
```
1. Open Visual Studio 2022
2. File > New > Project
3. Select "Dynamic-Link Library (DLL)" for C++
4. Name: DJAudioEngine
5. Location: C:\Apps\DJApp\
6. Create
```

#### Step 1.2: Install Dependencies via vcpkg
```powershell
# Install vcpkg if not already installed
git clone https://github.com/Microsoft/vcpkg.git C:\vcpkg
cd C:\vcpkg
.\bootstrap-vcpkg.bat
.\vcpkg integrate install

# Install dependencies
.\vcpkg install portaudio:x64-windows
.\vcpkg install soundtouch:x64-windows
```

#### Step 1.3: Download ASIO SDK
```
1. Go to https://www.steinberg.net/developers/
2. Register/Login
3. Download ASIO SDK
4. Extract to C:\Apps\DJApp\DJAudioEngine\libs\asiosdk
```

#### Step 1.4: Download dr_libs
```powershell
# Download single-header audio libraries
cd C:\Apps\DJApp\DJAudioEngine\include
curl -O https://raw.githubusercontent.com/mackron/dr_libs/master/dr_mp3.h
curl -O https://raw.githubusercontent.com/mackron/dr_libs/master/dr_wav.h
curl -O https://raw.githubusercontent.com/mackron/dr_libs/master/dr_flac.h
```

### Phase 2: C++ Audio Engine Core

#### Step 2.1: Create C API Header (dj_audio_engine.h)
```cpp
// C:\Apps\DJApp\DJAudioEngine\include\dj_audio_engine.h
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

// Callbacks (for UI updates)
typedef void (*position_callback_t)(int deck_id, double position);
typedef void (*track_ended_callback_t)(int deck_id);
DJ_API void set_position_callback(position_callback_t callback);
DJ_API void set_track_ended_callback(track_ended_callback_t callback);

#ifdef __cplusplus
}
#endif

#endif // DJ_AUDIO_ENGINE_H
```

#### Step 2.2: Implement Audio Engine (audio_engine.cpp)
```cpp
// Core implementation with PortAudio
// - Initialize PortAudio with ASIO
// - Audio callback reads from decks, mixes, outputs
// - Sample-accurate timing in callback
```

#### Step 2.3: Implement Deck (deck.cpp)
```cpp
// Deck implementation
// - Load audio files using dr_libs
// - SoundTouch for tempo/pitch
// - Track position in samples (not time!)
// - Provide samples to mixer on request
```

#### Step 2.4: Implement Mixer (mixer.cpp)
```cpp
// Mixer implementation
// - Crossfader with power curve
// - Read from both decks
// - Mix and output
```

#### Step 2.5: Implement Sync (sync.cpp)
```cpp
// Beat sync implementation
// - Calculate phase from sample position and BPM
// - Adjust slave position to match master phase
// - All calculations in SAMPLES, not time
```

### Phase 3: C# Integration

#### Step 3.1: Create P/Invoke Wrapper (AudioEngineInterop.cs)
```csharp
// C:\Apps\DJApp\DJApp\Services\AudioEngineInterop.cs
using System;
using System.Runtime.InteropServices;

namespace DJAutoMixApp.Services
{
    public static class AudioEngineInterop
    {
        private const string DllName = "DJAudioEngine.dll";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int engine_init(int sampleRate, int bufferSize);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void engine_shutdown();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int engine_start();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void engine_stop();

        // Deck operations
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int deck_load_track(int deckId,
            [MarshalAs(UnmanagedType.LPStr)] string filePath);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void deck_play(int deckId);

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
        public static extern void deck_set_volume(int deckId, float volume);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void deck_set_tempo(int deckId, double tempo);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void deck_set_bpm(int deckId, double bpm);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void deck_set_eq_low(int deckId, float gain);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void deck_set_eq_mid(int deckId, float gain);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void deck_set_eq_high(int deckId, float gain);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mixer_set_crossfader(float position);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sync_enable(int slaveDeckId, int masterDeckId);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sync_disable(int deckId);

        // Callbacks
        public delegate void PositionCallback(int deckId, double position);
        public delegate void TrackEndedCallback(int deckId);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void set_position_callback(PositionCallback callback);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void set_track_ended_callback(TrackEndedCallback callback);
    }
}
```

#### Step 3.2: Create New AudioDeck Wrapper
```csharp
// Replace the existing AudioDeck.cs to use C++ engine
// Wrap the P/Invoke calls in a clean C# API
```

#### Step 3.3: Update App.xaml.cs
```csharp
// Initialize C++ engine on startup
// Shutdown on exit
// Remove NAudio references
```

### Phase 4: Testing

1. Test basic playback (load, play, pause, stop)
2. Test tempo/pitch adjustment
3. Test crossfader mixing
4. Test beat sync with SAME song (must be perfect!)
5. Test beat sync with DIFFERENT songs
6. Test with various audio formats (MP3, WAV, FLAC)
7. Test ASIO latency (should be <10ms)

## Key Differences from C# Implementation

### Position Tracking
```cpp
// C++: Track position in SAMPLES, always
int64_t sample_position = 0;  // Incremented in audio callback

// Calculate time only when needed for display
double get_position_seconds() {
    return (double)sample_position / sample_rate;
}
```

### Beat Phase Calculation
```cpp
// All in samples - no floating point time conversions
int64_t samples_per_beat = (int64_t)(60.0 / bpm * sample_rate);
int64_t samples_into_beat = sample_position % samples_per_beat;
double phase = (double)samples_into_beat / samples_per_beat;
```

### Sync in Audio Callback
```cpp
// Sync happens IN the audio callback - no latency!
void audio_callback(float* output, int frames) {
    // Calculate phase difference
    // Adjust slave position BEFORE reading samples
    // Read and mix samples
}
```

## Commands to Start Implementation

When ready to implement, tell Claude:

```
Read the file C:\Apps\DJApp\CPP_AUDIO_ENGINE_PLAN.md and implement Phase 1 (Project Setup) and Phase 2.1 (C API Header).
```

Then proceed phase by phase:
- "Implement Phase 2.2: Audio Engine core"
- "Implement Phase 2.3: Deck"
- "Implement Phase 2.4: Mixer"
- "Implement Phase 2.5: Sync"
- "Implement Phase 3: C# Integration"

## Notes

- ASIO requires an ASIO-compatible audio interface, or use ASIO4ALL for regular sound cards
- Keep BPM detection in C# for now (it works fine, not real-time critical)
- The C++ engine should be stateless from C#'s perspective - all state lives in the DLL
- Use callbacks to notify C# of position updates and track end events
