#include "dj_audio_internal.h"
#include <cmath>

namespace dj {

SyncManager::SyncManager() {
    sync_state_.enabled = false;
    sync_state_.master_deck_id = -1;
    sync_state_.slave_deck_id = -1;
}

void SyncManager::enable(int slave_deck_id, int master_deck_id) {
    std::lock_guard<std::mutex> lock(sync_mutex_);
    sync_state_.enabled = true;
    sync_state_.master_deck_id = master_deck_id;
    sync_state_.slave_deck_id = slave_deck_id;
}

void SyncManager::disable(int deck_id) {
    std::lock_guard<std::mutex> lock(sync_mutex_);
    if (sync_state_.slave_deck_id == deck_id) {
        sync_state_.enabled = false;
        sync_state_.master_deck_id = -1;
        sync_state_.slave_deck_id = -1;
    }
}

void SyncManager::alignNow(Deck* slave, Deck* master) {
    if (!slave || !master) return;
    
    double master_bpm = master->getBPM();
    double slave_bpm = slave->getBPM();
    
    if (master_bpm <= 0.0 || slave_bpm <= 0.0) return;
    
    // Match tempo first
    double tempo_ratio = master_bpm / slave_bpm;
    slave->setTempo(tempo_ratio);
    
    // Get current positions accounting for beat offsets
    int sample_rate = 44100;  // TODO: Get from deck
    
    // Master's adjusted position and phase
    int64_t master_offset_samples = static_cast<int64_t>(master->getBeatOffset() * sample_rate);
    int64_t master_adjusted_pos = master->getSamplePosition() - master_offset_samples;
    double master_seconds_per_beat = 60.0 / master_bpm;
    int64_t master_samples_per_beat = static_cast<int64_t>(master_seconds_per_beat * sample_rate);
    double master_phase = (master_adjusted_pos % master_samples_per_beat) / (double)master_samples_per_beat;
    if (master_phase < 0) master_phase += 1.0;
    
    // Slave's target position with tempo adjustment
    double slave_seconds_per_beat = 60.0 / (slave_bpm * tempo_ratio);
    int64_t slave_samples_per_beat = static_cast<int64_t>(slave_seconds_per_beat * sample_rate);
    int64_t slave_offset_samples = static_cast<int64_t>(slave->getBeatOffset() * sample_rate);
    
    // Calculate which beat the slave is currently on
    int64_t slave_current_pos = slave->getSamplePosition();
    int64_t slave_adjusted_pos = slave_current_pos - slave_offset_samples;
    int64_t current_beat = slave_adjusted_pos / slave_samples_per_beat;
    
    // Set slave to same phase within its current beat
    int64_t target_pos = slave_offset_samples + (current_beat * slave_samples_per_beat) + 
                         static_cast<int64_t>(master_phase * slave_samples_per_beat);
    
    // Apply the alignment
    if (target_pos >= 0 && target_pos < slave_current_pos + slave_samples_per_beat) {
        slave->setSamplePosition(target_pos);
    }
}

void SyncManager::update(Deck* decks[2]) {
    std::lock_guard<std::mutex> lock(sync_mutex_);
    
    if (!sync_state_.enabled) return;
    
    int master_id = sync_state_.master_deck_id;
    int slave_id = sync_state_.slave_deck_id;
    
    if (master_id < 0 || master_id > 1 || slave_id < 0 || slave_id > 1) return;
    
    Deck* master = decks[master_id];
    Deck* slave = decks[slave_id];
    
    if (!master || !slave) return;
    
    // Get BPMs
    double master_bpm = master->getBPM();
    double slave_bpm = slave->getBPM();
    
    if (master_bpm <= 0.0 || slave_bpm <= 0.0) return;
    
    // Match tempo (this is lightweight, do every time)
    double tempo_ratio = master_bpm / slave_bpm;
    slave->setTempo(tempo_ratio);
    
    // Phase sync is more expensive and can cause clicks - do it less frequently
    static int frame_counter = 0;
    frame_counter++;
    
    // Check phase every 3 callbacks (~30ms at 512 samples) - balance between responsiveness and smoothness
    if (frame_counter < 3) return;
    frame_counter = 0;
    
    // Get phases (0.0 to 1.0)
    double master_phase = master->getPhase();
    double slave_phase = slave->getPhase();
    
    // Calculate phase difference
    double phase_diff = master_phase - slave_phase;
    
    // Normalize to -0.5 to 0.5 (shortest path)
    if (phase_diff > 0.5) phase_diff -= 1.0;
    if (phase_diff < -0.5) phase_diff += 1.0;
    
    // Only adjust if phase difference is significant (> 2% of a beat)
    // Balance between accuracy and avoiding clicks
    if (std::abs(phase_diff) > 0.02) {
        // Calculate correction in samples
        double slave_seconds_per_beat = 60.0 / (slave_bpm * tempo_ratio);
        int sample_rate = 44100;  // TODO: Get from deck
        
        // Convert phase difference to samples
        int64_t correction_samples = static_cast<int64_t>(
            phase_diff * slave_seconds_per_beat * sample_rate
        );
        
        // Limit correction size to prevent large jumps (max 50ms for smoother adjustments)
        int64_t max_correction = sample_rate / 20;  // 50ms
        correction_samples = std::max<int64_t>(-max_correction, 
                                               std::min<int64_t>(max_correction, correction_samples));
        
        // Apply correction
        int64_t new_position = slave->getSamplePosition() + correction_samples;
        if (new_position >= 0) {
            slave->setSamplePosition(new_position);
        }
    }
}

} // namespace dj
