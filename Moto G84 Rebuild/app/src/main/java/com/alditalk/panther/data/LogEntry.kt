package com.alditalk.panther.data

import androidx.room.Entity
import androidx.room.Index
import androidx.room.PrimaryKey

/**
 * X11Pro v1.2: Index auf [timestamp] – die UI sortiert bei jedem Poll
 * (ORDER BY timestamp DESC LIMIT 200) und der Trim läuft über einen
 * Subselect auf timestamp. Ohne Index = Vollsortierung über bis zu
 * 5000 Zeilen bei jedem 60-s-Durchlauf (spürbarer Hänger auf dem
 * Helio G25 / eMMC des X11Pro).
 */
@Entity(tableName = "log_entries", indices = [Index(value = ["timestamp"])])
data class LogEntry(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    val timestamp: Long = System.currentTimeMillis(),
    val type: String,       // "CHECK" or "BOOKING"
    val remainingMb: Float = 0f,
    val message: String,
)
