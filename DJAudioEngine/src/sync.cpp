#include "dj_audio_internal.h"
#include <cmath>
#include <cstdio>
#ifdef _WIN32
#include <Windows.h>
#endif

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
    FILE* logFile = fopen("c:\\Apps\\DJApp\\cpp_debug.log", "a");
    
    if (!slave || !master) {
        if (logFile) { fprintf(logFile, "alignNow: null deck\n"); fclose(logFile); }
        return;
    }
    
    double master_bpm = master->getBPM();
    double slave_bpm = slave->getBPM();
    
    if (master_bpm <= 0.0 || slave_bpm <= 0.0) {
        if (logFile) { fprintf(logFile, "alignNow: Invalid BPM m=%.1f s=%.1f\n", master_bpm, slave_bpm); fclose(logFile); }
        return;
    }
    
    // Match tempo
    double tempo_ratio = master_bpm / slave_bpm;
    slave->setTempo(tempo_ratio);
    
    // Simple: set slave to same position as master (for same song testing)
    int64_t master_pos = master->getSamplePosition();
    
    if (logFile) {
        fprintf(logFile, "alignNow: master_pos=%lld, setting slave with forceSync=true\n", (long long)master_pos);
        fflush(logFile);
    }
    
    // Use forceSync=true to clear SoundTouch buffer!
    slave->setSamplePosition(master_pos, true);
    
    if (logFile) {
        fprintf(logFile, "alignNow: Done, slave now at %lld\n", (long long)slave->getSamplePosition());
        fclose(logFile);
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
    
    double master_bpm = master->getBPM();
    double slave_bpm = slave->getBPM();
    
    if (master_bpm <= 0.0 || slave_bpm <= 0.0) return;
    
    // ONLY match tempo - phase alignment happens once via alignNow()
    double tempo_ratio = master_bpm / slave_bpm;
    slave->setTempo(tempo_ratio);
}

} // namespace dj
