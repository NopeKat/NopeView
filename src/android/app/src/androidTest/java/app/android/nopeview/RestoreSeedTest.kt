// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

package app.android.nopeview

import android.content.Context
import android.content.Intent
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.compose.ui.test.onAllNodesWithTag
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Before
import org.junit.Rule
import org.junit.Test

/**
 * Leaves one image in the persisted viewer state. The test runner is stopped
 * after this class, then RestoreAfterRestartTest starts a fresh process.
 */
class RestoreSeedTest {
    @get:Rule
    val rule = createEmptyComposeRule()

    private lateinit var context: Context

    @Before
    fun setUp() {
        context = InstrumentationRegistry.getInstrumentation().targetContext
        TestImages.reset(context)
    }

    @Test
    fun openImageForRestore() {
        val older = TestImages.writePng(context, "restore-old.png", 60, 30)
        TestShell.startImage(Intent.ACTION_VIEW, older, "image/png")
        awaitTag("control_open")

        val latest = TestImages.writePng(context, "restore.png", 80, 40)
        TestShell.startImage(Intent.ACTION_VIEW, latest, "image/png")
        awaitTag("control_open")

        rule.onNodeWithTag("control_info").performClick()
        awaitTag("info_dialog")
        val name = context.getString(R.string.image_info_name) + ": restore.png"
        rule.onNodeWithText(name).assertExists()
        rule.onNodeWithText(context.getString(R.string.close)).performClick()
    }

    private fun awaitTag(tag: String) {
        rule.waitUntil(timeoutMillis = 15_000) {
            rule.onAllNodesWithTag(tag).fetchSemanticsNodes().isNotEmpty()
        }
    }
}
