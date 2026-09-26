package com.alditalk.panther

import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import androidx.fragment.app.Fragment
import androidx.recyclerview.widget.DiffUtil
import androidx.recyclerview.widget.LinearLayoutManager
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
 * MotoG84 v1.3: Verlauf-Seite (ViewPager Seite 1).
 * Eigene Display-Seite per Wisch; Zurueck per Wisch oder Button.
 */
class LogFragment : Fragment() {

    companion object {
        fun newInstance() = LogFragment()
    }

    private lateinit var rvLog: RecyclerView
    private val adapter = LogAdapter()
    private var lastTopLogId: Long? = null

    private fun host(): MainActivity = requireActivity() as MainActivity

    override fun onCreateView(
        inflater: LayoutInflater,
        container: ViewGroup?,
        savedInstanceState: Bundle?
    ): View = inflater.inflate(R.layout.fragment_log, container, false)

    override fun onViewCreated(view: View, savedInstanceState: Bundle?) {
        super.onViewCreated(view, savedInstanceState)
        rvLog = view.findViewById(R.id.rvLog)
        val lm = LinearLayoutManager(requireContext())
        rvLog.layoutManager = lm
        rvLog.adapter = adapter
        rvLog.setHasFixedSize(true)
        rvLog.itemAnimator = null
        rvLog.setItemViewCacheSize(20)

        view.findViewById<View>(R.id.btnBack).setOnClickListener {
            host().showMainPage()
        }
        // MotoG84 v1.5: Wartung (von Haupt-Seite hierher umgezogen).
        view.findViewById<View>(R.id.btnClearCache).setOnClickListener {
            host().onClearCacheClicked()
        }
        view.findViewById<View>(R.id.btnExportLog).setOnClickListener {
            host().onExportLogClicked()
        }

        host().logState.entries.observe(viewLifecycleOwner) { entries ->
            val newTopId = entries.firstOrNull()?.id
            val shouldStickToNewest = lastTopLogId == null ||
                lm.findFirstVisibleItemPosition() <= 1
            lastTopLogId = newTopId
            adapter.submitList(entries) {
                if (shouldStickToNewest && entries.isNotEmpty()) {
                    rvLog.scrollToPosition(0)
                }
            }
        }
    }

    class LogAdapter :
        ListAdapter<LogEntry, LogAdapter.ViewHolder>(LOG_DIFF) {

        private val sdf = SimpleDateFormat("dd.MM HH:mm:ss", Locale.GERMAN)

        class ViewHolder(val view: android.widget.TextView) :
            RecyclerView.ViewHolder(view)

        override fun onCreateViewHolder(
            parent: ViewGroup,
            viewType: Int
        ): ViewHolder {
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
}
