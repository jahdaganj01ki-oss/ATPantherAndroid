package com.alditalk.panther.data

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.Query
import kotlinx.coroutines.flow.Flow

@Dao
interface LogDao {
    @Insert
    suspend fun insert(entry: LogEntry)

    @Query("SELECT * FROM log_entries ORDER BY timestamp DESC")
    fun getAll(): Flow<List<LogEntry>>

    /**
     * Freeze-Fix: UI-laesst nur die letzten [limit] Eintraege laden.
     * Ein unbeschraenktes SELECT * waechst bei 60s-Intervall um ~1440 Zeilen/Tag
     * und verlangsamt jedes Flow-Emit + RecyclerView-Bind spuerbar.
     */
    @Query("SELECT * FROM log_entries ORDER BY timestamp DESC LIMIT :limit")
    fun getRecent(limit: Int): Flow<List<LogEntry>>

    @Query("DELETE FROM log_entries WHERE timestamp < :maxAge")
    suspend fun deleteOlderThan(maxAge: Long)

    /**
     * Freeze-Fix: DB hart auf die letzten [keep] Eintraege begrenzen,
     * damit die Tabelle auch bei Minutengenaue Polling nicht unbegrenzt waechst.
     */
    @Query("DELETE FROM log_entries WHERE id NOT IN (SELECT id FROM log_entries ORDER BY timestamp DESC LIMIT :keep)")
    suspend fun deleteBeyondLimit(keep: Int)

    @Query("SELECT COUNT(*) FROM log_entries")
    suspend fun count(): Int
}
