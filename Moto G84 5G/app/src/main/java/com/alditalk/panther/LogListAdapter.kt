package com.alditalk.panther

import android.view.LayoutInflater
import android.view.ViewGroup
import androidx.recyclerview.widget.DiffUtil
import androidx.recyclerview.widget.ListAdapter
import androidx.recyclerview.widget.RecyclerView
import com.alditalk.panther.data.LogEntry
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * DiffUtil-Callback fuer den Log-Verlauf (MotoG84-Freeze-Fix).
 * LogEntry ist eine data class mit stabilem Primary-Key [LogEntry.id] –
 * strukturelle Gleichheit reicht damit fuer effizientes Partial-Binding.
 */
private val LOG_DIFF = object : DiffUtil.ItemCallback<LogEntry>() {
    override fun areItemsTheSame(oldItem: LogEntry, newItem: LogEntry): Boolean =
        oldItem.id == newItem.id

    override fun areContentsTheSame(oldItem: LogEntry, newItem: LogEntry): Boolean =
        oldItem == newItem
}

/**
 * MotoG84 v1.6: RecyclerView-Adapter fuer den Verlauf.
 *
 * Bewusst aus [LogFragment] herausgezogen: seit v1.6 gibt es keine zweite
 * Seite mehr, der Verlauf steht mit fester Hoehe in [MainFragment] auf der
 * einen Seite. ListAdapter + DiffUtil bleiben erhalten, damit bei jedem
 * 60-s-Poll nur die tatsaechlich neuen Zeilen gebunden werden.
 */
class LogListAdapter :
    ListAdapter<LogEntry, LogListAdapter.ViewHolder>(LOG_DIFF) {

    private val sdf = SimpleDateFormat("dd.MM HH:mm:ss", Locale.GERMAN)

    class ViewHolder(val view: android.widget.TextView) :
        RecyclerView.ViewHolder(view)

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): ViewHolder {
        val view = LayoutInflater.from(parent.context)
            .inflate(R.layout.item_log, parent, false) as android.widget.TextView
        return ViewHolder(view)
    }

    override fun onBindViewHolder(holder: ViewHolder, position: Int) {
        val entry = getItem(position)
        val time = sdf.format(Date(entry.timestamp))
        val typeIcon = if (entry.type == "BOOKING") "📦" else "📡"
        holder.view.text = "$time  $typeIcon  ${entry.message}"
    }
}
