// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

package app.android.nopeview

import android.content.Context
import android.content.Intent
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.compose.ui.test.onAllNodesWithTag
import androidx.compose.ui.test.onNodeWithTag
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertEquals
import org.junit.Before
import org.junit.Rule
import org.junit.Test

/**
 * Opens an image, then removes its backing file before the process restart.
 * The persisted URI is intentionally left behind.
 */
class RestoreDeletedFileSeedTest {
    @get:Rule
    val rule = createEmptyComposeRule()

    private lateinit var context: Context

    @Before
    fun setUp() {
        context = InstrumentationRegistry.getInstrumentation().targetContext
        TestImages.reset(context)
    }

    @Test
    fun openImageAndDeleteIt() {
        val uri = TestImages.writePng(context, "deleted.png", 70, 35)
        TestShell.startImage(Intent.ACTION_VIEW, uri, "image/png")
        awaitTag("control_open")

        TestImages.delete(context, "deleted.png")
        val prefs = context.getSharedPreferences("viewer", Context.MODE_PRIVATE)
        assertEquals(uri.toString(), prefs.getString("last_uri", null))
    }

    private fun awaitTag(tag: String) {
        rule.waitUntil(timeoutMillis = 15_000) {
            rule.onAllNodesWithTag(tag).fetchSemanticsNodes().isNotEmpty()
        }
    }
}
