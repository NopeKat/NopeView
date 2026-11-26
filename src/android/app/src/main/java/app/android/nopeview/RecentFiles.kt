// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

package app.android.nopeview

import android.content.Context
import androidx.core.content.edit
import org.json.JSONArray
import org.json.JSONObject

data class RecentEntry(
    val uri: String,
    val name: String,
    val mimeType: String?,
    val openedAt: Long,
)

/**
 * Stores the most recently opened documents in the same preferences file as
 * the last-image fallback. Entries are kept newest first and deduplicated by
 * URI so a repeated open moves the existing row to the top.
 */
object RecentFiles {
    private const val PREFS_NAME = "viewer"
    private const val KEY_RECENT = "recent_files"
    const val LIMIT = 10

    fun load(context: Context): List<RecentEntry> {
        val raw = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            .getString(KEY_RECENT, null)
            ?: return emptyList()
        val array = runCatching { JSONArray(raw) }.getOrNull() ?: return emptyList()
        val entries = ArrayList<RecentEntry>(array.length())
        for (index in 0 until array.length()) {
            val item = array.optJSONObject(index) ?: continue
            val uri = item.optString("uri").takeIf { it.isNotEmpty() } ?: continue
            entries.add(
                RecentEntry(
                    uri = uri,
                    name = item.optString("name", uri),
                    mimeType = item.optString("mime").takeIf { it.isNotEmpty() },
                    openedAt = item.optLong("openedAt", 0L),
                ),
            )
        }
        return entries.take(LIMIT)
    }

    fun add(context: Context, entry: RecentEntry): List<RecentEntry> {
        val current = load(context).filterNot { it.uri == entry.uri }
        val updated = (listOf(entry) + current).take(LIMIT)
        save(context, updated)
        return updated
    }

    fun remove(context: Context, uri: String): List<RecentEntry> {
        val updated = load(context).filterNot { it.uri == uri }
        save(context, updated)
        return updated
    }

    fun clear(context: Context) {
        save(context, emptyList())
    }

    private fun save(context: Context, entries: List<RecentEntry>) {
        val array = JSONArray()
        for (entry in entries) {
            array.put(
                JSONObject()
                    .put("uri", entry.uri)
                    .put("name", entry.name)
                    .put("mime", entry.mimeType ?: "")
                    .put("openedAt", entry.openedAt),
            )
        }
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            .edit { putString(KEY_RECENT, array.toString()) }
    }
}
