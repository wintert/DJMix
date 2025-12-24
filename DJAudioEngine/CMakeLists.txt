cmake_minimum_required(VERSION 3.15)
project(DJAudioEngine)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Add preprocessor definition for DLL exports
add_definitions(-DDJ_AUDIO_ENGINE_EXPORTS)
add_definitions(-DUSE_KISS_FFT)

# MSVC needs _USE_MATH_DEFINES for M_PI
add_definitions(-D_USE_MATH_DEFINES)

# QM DSP's kiss_fft needs double precision
add_definitions(-Dkiss_fft_scalar=double)

# Find packages from vcpkg
find_package(portaudio CONFIG REQUIRED)
find_package(SampleRate CONFIG REQUIRED)

# Build SoundTouch from source
add_subdirectory(libs/soundtouch EXCLUDE_FROM_ALL)

# Include directories
include_directories(
    ${CMAKE_CURRENT_SOURCE_DIR}/include
    ${CMAKE_CURRENT_SOURCE_DIR}/libs
    ${CMAKE_CURRENT_SOURCE_DIR}/libs/soundtouch/include
    ${CMAKE_CURRENT_SOURCE_DIR}/libs/asiosdk/common
    ${CMAKE_CURRENT_SOURCE_DIR}/libs/minibpm/src
    ${CMAKE_CURRENT_SOURCE_DIR}/libs/btrack/src
    ${CMAKE_CURRENT_SOURCE_DIR}/libs/btrack/libs/kiss_fft130
    # QM DSP includes
    ${CMAKE_CURRENT_SOURCE_DIR}/libs/qm-dsp
    ${CMAKE_CURRENT_SOURCE_DIR}/libs/qm-dsp/ext/kissfft
    ${CMAKE_CURRENT_SOURCE_DIR}/libs/qm-dsp/ext/kissfft/tools
)

# Source files
set(SOURCES
    src/audio_engine.cpp
    src/audio_file.cpp
    src/deck.cpp
    src/mixer.cpp
    src/sync.cpp
    src/soundtouch_wrap.cpp
    src/bpm_analyzer.cpp
    libs/minibpm/src/MiniBpm.cpp
    libs/btrack/src/BTrack.cpp
    libs/btrack/src/OnsetDetectionFunction.cpp
    # Note: Not including BTrack's kiss_fft - using QM DSP's version instead
    # QM DSP source files  
    libs/qm-dsp/dsp/tempotracking/TempoTrackV2.cpp
    libs/qm-dsp/dsp/onsets/DetectionFunction.cpp
    libs/qm-dsp/dsp/onsets/PeakPicking.cpp
    libs/qm-dsp/dsp/signalconditioning/DFProcess.cpp
    libs/qm-dsp/dsp/signalconditioning/Filter.cpp
    libs/qm-dsp/dsp/signalconditioning/FiltFilt.cpp
    libs/qm-dsp/dsp/signalconditioning/Framer.cpp
    libs/qm-dsp/dsp/phasevocoder/PhaseVocoder.cpp
    libs/qm-dsp/dsp/transforms/FFT.cpp
    libs/qm-dsp/maths/MathUtilities.cpp
    libs/qm-dsp/maths/Correlation.cpp
    libs/qm-dsp/base/KaiserWindow.cpp
    libs/qm-dsp/base/SincWindow.cpp
    libs/qm-dsp/ext/kissfft/kiss_fft.c
    libs/qm-dsp/ext/kissfft/tools/kiss_fftr.c
)

# Create the DLL
add_library(DJAudioEngine SHARED ${SOURCES})

# Link libraries
target_link_libraries(DJAudioEngine 
    PRIVATE 
    portaudio
    SoundTouch
    SampleRate::samplerate
)

# Set output directory
set_target_properties(DJAudioEngine PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin
)
