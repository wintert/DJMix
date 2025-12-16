# DJ App Architecture Redesign for Perfect Beat Sync

## Current Problem
- Two independent `WaveOutEvent` outputs (Deck A, Deck B)
- Each has separate audio buffers and callbacks
- Cannot achieve sample-accurate synchronization
- Position seeking introduces latency and glitches

## Solution: Single Output Stream Architecture

### Architecture Overview
```
┌─────────────┐     ┌─────────────┐
│  Deck A     │     │  Deck B     │
│  Provider   │     │  Provider   │
└──────┬──────┘     └──────┬──────┘
       │                   │
       └─────────┬─────────┘
                 │
         ┌───────▼────────┐
         │  Mixer Sample  │
         │    Provider    │
         └───────┬────────┘
                 │
         ┌───────▼────────┐
         │  WaveOutEvent  │
         │  (Single)      │
         └────────────────┘
```

### Key Components

#### 1. **DeckSampleProvider** (new class)
- Wraps audio file + SoundTouch
- Maintains sample-accurate position counter
- Returns samples on demand (no separate thread)
- Properties:
  - `BPM`, `Tempo`, `BeatOffset`
  - `SamplePosition` (sample-accurate, not time-based)
  - `IsPlaying` (bool flag)
  - `Volume`, `EQ`

#### 2. **MixerSampleProvider** (new class)
- Implements `ISampleProvider`
- Contains both DeckSampleProvider instances
- **Single `Read()` method** that:
  - Reads samples from Deck A (if playing)
  - Reads samples from Deck B (if playing)
  - Mixes them together based on crossfader position
  - Returns mixed samples
- Sample-accurate synchronization happens HERE

#### 3. **Single WaveOutEvent**
- One output device for the entire app
- Initialized with MixerSampleProvider
- Continuously calls `mixer.Read()` for samples

### Synchronization Strategy

#### When enabling sync on Deck B:
1. Calculate phase difference in **samples** (not time)
2. When Play is pressed:
   - Don't start immediately
   - Wait until Deck A's next beat
   - Start Deck B exactly on that sample boundary
3. Both decks feed samples at same rate = perfect sync

#### Sample-accurate beat alignment:
```csharp
// Deck A at sample position 132300
// BPM = 120, Sample Rate = 44100
// Beat duration in samples = (60 / 120) * 44100 = 22050 samples

// Deck A phase:
long samplesIntoBeat = deckA.SamplePosition % samplesPerBeat;

// Deck B starts when Deck A hits next beat:
long samplesToNextBeat = samplesPerBeat - samplesIntoBeat;

// In MixerSampleProvider.Read():
//   - Read samples from A
//   - If B is "waiting to start", count down samplesToNextBeat
//   - When countdown hits 0, start reading from B
//   - Both are now perfectly aligned!
```

### Benefits
1. **Perfect sync**: Sample-accurate alignment, no drift
2. **No glitches**: No seeking during playback
3. **No latency issues**: Single output = single latency
4. **Smooth crossfading**: Happens in mixer, not via volume
5. **Deterministic**: Same behavior every time

### Implementation Steps

1. **Create `DeckSampleProvider`**
   - Extract audio chain from current `AudioDeck`
   - Add sample position tracking
   - Add play/pause flags (no separate WaveOut)

2. **Create `MixerSampleProvider`**
   - Implement `ISampleProvider`
   - Add both decks
   - Implement quantized start logic
   - Add crossfader mixing

3. **Refactor `AudioDeck`**
   - Becomes a wrapper around `DeckSampleProvider`
   - No more `WaveOutEvent` per deck
   - Position is calculated from samples

4. **Update `MainViewModel`**
   - Create single `WaveOutEvent`
   - Create `MixerSampleProvider` with both decks
   - Initialize output with mixer

### Code Estimate
- **DeckSampleProvider**: ~200 lines
- **MixerSampleProvider**: ~150 lines
- **AudioDeck refactor**: ~100 lines changed
- **MainViewModel changes**: ~50 lines

**Total**: ~500 lines of code, 2-3 hours of work

### This is the ONLY way to achieve perfect sync in software
Professional DJ software (Traktor, Serato, Rekordbox) ALL use this architecture.
