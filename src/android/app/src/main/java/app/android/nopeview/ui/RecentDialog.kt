package app.android.nopeview.ui

import android.net.Uri
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.net.toUri
import app.android.nopeview.R
import app.android.nopeview.RecentEntry
import java.text.DateFormat
import java.util.Date

@Composable
fun RecentDialog(
    entries: List<RecentEntry>,
    canRead: (Uri) -> Boolean,
    onOpen: (RecentEntry) -> Unit,
    onRemove: (RecentEntry) -> Unit,
    onClear: () -> Unit,
    onDismiss: () -> Unit,
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        modifier = Modifier.testTag("recent_dialog"),
        title = { Text(stringResource(R.string.recent_files)) },
        text = {
            if (entries.isEmpty()) {
                Text(stringResource(R.string.recent_empty))
            } else {
                LazyColumn(modifier = Modifier.heightIn(max = 360.dp)) {
                    items(entries, key = { it.uri }) { entry ->
                        val uri = runCatching { entry.uri.toUri() }.getOrNull()
                        val available = uri != null && canRead(uri)
                        Row(
                            modifier = Modifier.fillMaxWidth(),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Column(
                                modifier = Modifier
                                    .weight(1f)
                                    .clickable(enabled = available) { onOpen(entry) }
                                    .padding(vertical = 10.dp, horizontal = 4.dp),
                            ) {
                                Text(
                                    text = entry.name,
                                    style = MaterialTheme.typography.bodyMedium,
                                    color = if (available) {
                                        Color.Unspecified
                                    } else {
                                        MaterialTheme.colorScheme.onSurface.copy(alpha = 0.45f)
                                    },
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                )
                                Text(
                                    text = if (available) {
                                        recentSubtitle(entry)
                                    } else {
                                        stringResource(R.string.permission_lost)
                                    },
                                    style = MaterialTheme.typography.labelSmall,
                                    color = if (available) {
                                        MaterialTheme.colorScheme.onSurfaceVariant
                                    } else {
                                        MaterialTheme.colorScheme.error
                                    },
                                )
                            }
                            TextButton(onClick = { onRemove(entry) }) {
                                Text(stringResource(R.string.remove))
                            }
                        }
                    }
                }
            }
        },
        confirmButton = {
            TextButton(onClick = onDismiss) {
                Text(stringResource(R.string.close))
            }
        },
        dismissButton = {
            TextButton(onClick = onClear, enabled = entries.isNotEmpty()) {
                Text(stringResource(R.string.clear))
            }
        },
    )
}

private fun recentSubtitle(entry: RecentEntry): String {
    val extension = entry.name
        .substringAfterLast('.', "")
        .trim()
        .uppercase()
        .takeIf { it.isNotEmpty() && it.length <= 8 }
    val type = extension
        ?: entry.mimeType
            ?.substringAfter('/')
            ?.uppercase()
            ?.takeIf { it.isNotEmpty() }
        ?: "Image"
    val time = if (entry.openedAt > 0L) {
        DateFormat.getDateTimeInstance(DateFormat.SHORT, DateFormat.SHORT)
            .format(Date(entry.openedAt))
    } else {
        ""
    }
    return if (time.isEmpty()) type else "$type  $time"
}
