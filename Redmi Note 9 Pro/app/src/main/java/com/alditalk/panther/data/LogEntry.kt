package com.alditalk.panther.data

import androidx.room.Entity
import androidx.room.Index
import androidx.room.PrimaryKey

/**
 * Redmi Note 9 Pro v1.2: Index auf [timestamp] – die UI sortiert bei jedem Poll
 * (ORDER BY timestamp DESC LIMIT 200) und der Trim läuft über einen
 * Subselect auf timestamp. Ohne Index = Vollsortierung über bis zu
 * 5000 Zeilen bei jedem 60-s-Durchlauf (spürbarer Hänger).
 */
@Entity(tableName = "log_entries", indices = [Index(value = ["timestamp"])])
data class LogEntry(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    val timestamp: Long = System.currentTimeMillis(),
    val type: String,       // "CHECK" or "BOOKING"
    val remainingMb: Float = 0f,
    val message: String,
)
