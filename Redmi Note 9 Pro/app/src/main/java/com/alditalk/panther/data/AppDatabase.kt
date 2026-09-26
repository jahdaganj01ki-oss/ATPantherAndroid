package com.alditalk.panther.data

import android.content.Context
import androidx.room.Database
import androidx.room.Room
import androidx.room.RoomDatabase

@Database(entities = [LogEntry::class], version = 2, exportSchema = false)
abstract class AppDatabase : RoomDatabase() {
    abstract fun logDao(): LogDao

    companion object {
        @Volatile
        private var INSTANCE: AppDatabase? = null

        /**
         * Redmi Note 9 Pro v1.2: Version 2 (Index auf LogEntry.timestamp).
         * Reine Log-DB ohne Nutzerdaten → destruktive Migration ist
         * akzeptabel und vermeidet Migrations-Crashs beim Update.
         */
        fun getDatabase(context: Context): AppDatabase {
            return INSTANCE ?: synchronized(this) {
                Room.databaseBuilder(
                    context.applicationContext,
                    AppDatabase::class.java,
                    "at_panther_db"
                )
                    .fallbackToDestructiveMigration()
                    .setJournalMode(RoomDatabase.JournalMode.TRUNCATE)
                    .build().also { INSTANCE = it }
            }
        }
    }
}
