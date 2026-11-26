// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

package app.android.nopeview

import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.compose.ui.test.onAllNodesWithTag
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Rule
import org.junit.Test

/**
 * Runs after RestoreSeedTest and a force-stop. It must not clear preferences:
 * the saved URI and its persisted read grant are the state under test.
 */
class RestoreRestartTest {
    @get:Rule
    val rule = createEmptyComposeRule()

    @Test
    fun restoresLastImageAfterProcessRestart() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        TestShell.startMain()
        awaitTag("control_open")

        rule.onNodeWithTag("control_info").performClick()
        awaitTag("info_dialog")
        val name = context.getString(R.string.image_info_name) + ": restore.png"
        val dimensions = context.getString(R.string.image_info_dimensions) + ": 80 x 40"
        rule.onNodeWithText(name).assertExists()
        rule.onNodeWithText(dimensions).assertExists()
    }

    private fun awaitTag(tag: String) {
        rule.waitUntil(timeoutMillis = 15_000) {
            rule.onAllNodesWithTag(tag).fetchSemanticsNodes().isNotEmpty()
        }
    }
}
