package com.alditalk.panther.data

import androidx.room.Entity
import androidx.room.PrimaryKey

@Entity(tableName = "log_entries")
data class LogEntry(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    val timestamp: Long = System.currentTimeMillis(),
    val type: String,       // "CHECK" or "BOOKING"
    val remainingMb: Float = 0f,
    val message: String,
)
