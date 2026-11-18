package app.android.nopeview

import android.content.Context
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.compose.ui.test.onAllNodesWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertNull
import org.junit.Rule
import org.junit.Test

/**
 * Runs after RestoreDeletedFileSeedTest and a force-stop. The saved URI
 * still exists, but its backing file does not.
 */
class RestoreDeletedFileAfterRestartTest {
    @get:Rule
    val rule = createEmptyComposeRule()

    @Test
    fun clearsDeletedLastImage() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val prefs = context.getSharedPreferences("viewer", Context.MODE_PRIVATE)
        TestShell.startMain()
        awaitTag("viewer_shell")

        rule.waitUntil(timeoutMillis = 15_000) {
            prefs.getString("last_uri", null) == null
        }
        rule.onNodeWithText(context.getString(R.string.empty_title)).assertExists()
        assertNull(prefs.getString("last_uri", null))
    }

    private fun awaitTag(tag: String) {
        rule.waitUntil(timeoutMillis = 15_000) {
            rule.onAllNodesWithTag(tag).fetchSemanticsNodes().isNotEmpty()
        }
    }
}
