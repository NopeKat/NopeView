// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

package app.android.nopeview

import android.content.Context
import android.content.Intent
import androidx.compose.ui.test.assertIsEnabled
import androidx.compose.ui.test.assertTextEquals
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.compose.ui.test.onAllNodesWithTag
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.runner.lifecycle.ActivityLifecycleMonitorRegistry
import androidx.test.runner.lifecycle.Stage
import org.junit.After
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test

/**
 * Exercises the real MainActivity through shell intents. The image provider
 * keeps the decode path identical to a document picked through SAF.
 */
class ViewerShellTest {
    @get:Rule
    val rule = createEmptyComposeRule()

    private lateinit var context: Context

    @Before
    fun setUp() {
        context = InstrumentationRegistry.getInstrumentation().targetContext
        SystemUi.wake()
        finishResumedActivities()
        TestImages.reset(context)
    }

    @After
    fun tearDown() {
        finishResumedActivities()
    }

    private fun finishResumedActivities() {
        InstrumentationRegistry.getInstrumentation().runOnMainSync {
            ActivityLifecycleMonitorRegistry.getInstance()
                .getActivitiesInStage(Stage.RESUMED)
                .forEach { it.finish() }
        }
    }

    @Test
    fun viewIntentOpensImageAndInfo() {
        val uri = TestImages.writePng(context, "view.png", 40, 20)
        TestShell.startImage(Intent.ACTION_VIEW, uri, "image/png")
        awaitTag("control_open")

        rule.onNodeWithTag("viewer_shell").assertExists()
        rule.onNodeWithTag("control_info").performClick()
        awaitTag("info_dialog")
        val dimensions = context.getString(R.string.image_info_dimensions) + ": 40 x 20"
        rule.onNodeWithText(dimensions).assertExists()
        closeDialog()
    }

    @Test
    fun sendIntentOpensImage() {
        val uri = TestImages.writePng(context, "send.png", 60, 30)
        TestShell.startImage(Intent.ACTION_SEND, uri, "image/png")
        awaitTag("control_open")

        rule.onNodeWithTag("control_info").performClick()
        awaitTag("info_dialog")
        val dimensions = context.getString(R.string.image_info_dimensions) + ": 60 x 30"
        rule.onNodeWithText(dimensions).assertExists()
        closeDialog()
    }

    @Test
    fun toolbarAndRecentListWork() {
        val uri = TestImages.writePng(context, "recent.png", 40, 20)
        TestShell.startImage(Intent.ACTION_VIEW, uri, "image/png")
        awaitTag("control_open")

        rule.onNodeWithTag("control_rotate").performClick()
        rule.onNodeWithTag("control_reset").performClick()
        rule.onNodeWithTag("control_info").performClick()
        awaitTag("info_dialog")
        closeDialog()

        rule.onNodeWithTag("control_share").assertIsEnabled()
        rule.onNodeWithTag("control_save").assertIsEnabled()

        rule.onNodeWithTag("control_recent").performClick()
        awaitTag("recent_dialog")
        rule.onNodeWithText("recent.png").assertExists()
        rule.onNodeWithText(context.getString(R.string.remove)).performClick()
        awaitText(context.getString(R.string.recent_empty))
        rule.onNodeWithText(context.getString(R.string.close)).performClick()
    }

    @Test
    fun shareOpensSystemChooser() {
        val uri = TestImages.writePng(context, "share.png", 40, 20)
        TestShell.startImage(Intent.ACTION_VIEW, uri, "image/png")
        awaitTag("control_open")
        SystemUi.wake()

        rule.onNodeWithTag("control_share").performClick()
        val resumed = SystemUi.waitForResumed(
            listOf("intentresolver", "ResolverActivity", "ChooserActivity"),
        )
        assertTrue("share chooser was not resumed: $resumed", resumed.isNotEmpty())

        SystemUi.back()
        SystemUi.wake()
        awaitTag("control_open")
    }

    @Test
    fun saveOpensSystemPicker() {
        val uri = TestImages.writePng(context, "save.png", 40, 20)
        TestShell.startImage(Intent.ACTION_VIEW, uri, "image/png")
        awaitTag("control_open")
        SystemUi.wake()

        rule.onNodeWithTag("control_save").performClick()
        val resumed = SystemUi.waitForResumed(
            listOf("documentsui", "CreateDocument", "SaveActivity", "picker"),
        )
        assertTrue("save picker was not resumed: $resumed", resumed.isNotEmpty())

        SystemUi.back()
        SystemUi.wake()
        awaitTag("control_open")
    }

    @Test
    fun gifPauseAndResumeToggle() {
        val uri = TestImages.copyGif(context, "animated.gif", "rotating-earth.gif")
        TestShell.startImage(Intent.ACTION_VIEW, uri, "image/gif")
        awaitTag("control_play")
        rule.onNodeWithTag("frame_label").assertExists()

        rule.onNodeWithTag("control_play")
            .assertTextEquals(context.getString(R.string.pause_animation))
        rule.onNodeWithTag("control_play").performClick()
        rule.onNodeWithTag("control_play")
            .assertTextEquals(context.getString(R.string.resume_animation))
        rule.onNodeWithTag("control_play").performClick()
        rule.onNodeWithTag("control_play")
            .assertTextEquals(context.getString(R.string.pause_animation))
    }

    private fun awaitTag(tag: String) {
        rule.waitUntil(timeoutMillis = 15_000) {
            rule.onAllNodesWithTag(tag).fetchSemanticsNodes().isNotEmpty()
        }
        if (tag == "control_open") {
            SystemUi.keepOn()
        }
    }

    private fun awaitText(text: String) {
        rule.waitUntil(timeoutMillis = 15_000) {
            rule.onAllNodesWithTag("recent_dialog").fetchSemanticsNodes().isNotEmpty() &&
                runCatching {
                    rule.onNodeWithText(text).fetchSemanticsNode()
                }.isSuccess
        }
    }

    private fun closeDialog() {
        rule.onNodeWithText(context.getString(R.string.close)).performClick()
    }
}
