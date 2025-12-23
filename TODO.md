# DJ App - TODO List

## Current Status
- BPM detection using MiniBPM is inaccurate (detects 112/121 instead of 140/128)
- Beat sync implementation is in place but depends on accurate BPM
- Crossfade and automix logic works

## Next Session: Integrate Queen Mary (QM DSP) for BPM Detection

### Step 1: Download QM DSP Library
- [ ] Clone or download from: https://github.com/c4dm/qm-dsp
- [ ] Add to `DJAudioEngine/libs/qm-dsp/`

### Step 2: Update CMake Build
- [ ] Add QM DSP source files to CMakeLists.txt
- [ ] Key files needed:
  - `dsp/tempotracking/TempoTrackV2.cpp`
  - `dsp/onsets/DetectionFunction.cpp`
  - `dsp/signalconditioning/...`
  - FFT and supporting utilities

### Step 3: Implement New BPM Analyzer
- [ ] Create wrapper function using QM DSP's TempoTrackV2
- [ ] Replace MiniBPM calls with QM DSP calls
- [ ] Test with same songs that showed wrong BPM

### Step 4: Test Beat Sync
- [ ] Verify BPM detection accuracy (should match Mixxx: 140 and 128)
- [ ] Test crossfade with accurate BPM values
- [ ] Confirm beats align properly

## Known Issues
- MiniBPM detects wrong BPM for some tracks
- Phase correction may need further tuning after accurate BPM is working

## Reference
- Mixxx uses `AnalyzerQueenMaryBeats` from QM DSP
- QM DSP repo: https://github.com/c4dm/qm-dsp
