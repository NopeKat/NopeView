package app.android.nopeview.ui

import android.graphics.Bitmap
import android.graphics.Color
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.test.TouchInjectionScope
import androidx.compose.ui.test.assertTextEquals
import androidx.compose.ui.test.doubleClick
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.compose.ui.test.onAllNodesWithTag
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.pinch
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTouchInput
import androidx.test.espresso.Espresso
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.runner.lifecycle.ActivityLifecycleMonitorRegistry
import androidx.test.runner.lifecycle.Stage
import app.android.nopeview.RegionTestState
import app.android.nopeview.R
import app.android.nopeview.TestShell
import org.junit.After
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test

/**
 * Exercises the real pointer pipeline in the region viewer. The activity is
 * launched through UiAutomation because the device blocks
 * Instrumentation.startActivitySync, and the empty Compose rule attaches to the
 * debug host afterwards.
 */
class RegionViewerGestureTest {
    @get:Rule
    val rule = createEmptyComposeRule()

    private lateinit var preview: Bitmap

    @Before
    fun setUp() {
        finishResumedActivities()
        preview = Bitmap.createBitmap(200, 100, Bitmap.Config.ARGB_8888).apply {
            eraseColor(Color.GRAY)
        }
        RegionTestState.reset(preview)
        launchHost()
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
    fun pinchCapsAtFourTimesAndDoubleTapSwitches() {
        rule.onNodeWithTag("region_zoom").assertTextEquals("100%")

        rule.onNodeWithTag("region_canvas").performTouchInput { pinchOut() }
        rule.waitForIdle()
        rule.onNodeWithTag("region_zoom").assertTextEquals("400%")

        rule.onNodeWithTag("region_canvas").performTouchInput { pinchOut() }
        rule.waitForIdle()
        rule.onNodeWithTag("region_zoom").assertTextEquals("400%")

        rule.onNodeWithTag("region_canvas").performTouchInput { doubleClick(center) }
        rule.waitForIdle()
        rule.onNodeWithTag("region_zoom").assertTextEquals("100%")

        rule.onNodeWithTag("region_canvas").performTouchInput { doubleClick(center) }
        rule.waitForIdle()
        val fit = InstrumentationRegistry.getInstrumentation()
            .targetContext
            .getString(R.string.view_fit)
        rule.onNodeWithTag("region_zoom").assertTextEquals(fit)
    }

    @Test
    fun backButtonReturnsToPreview() {
        rule.onNodeWithTag("region_back").performClick()
        rule.waitForIdle()

        assertTrue(RegionTestState.exited)
        rule.onNodeWithText("preview").assertExists()
    }

    @Test
    fun systemBackReturnsToPreview() {
        Espresso.pressBack()
        rule.waitForIdle()

        assertTrue(RegionTestState.exited)
        rule.onNodeWithText("preview").assertExists()
    }

    @Test
    fun viewportRotationKeepsZoomAndViewer() {
        rule.onNodeWithTag("region_canvas").performTouchInput { pinchOut() }
        rule.waitForIdle()
        rule.onNodeWithTag("region_zoom").assertTextEquals("400%")

        RegionTestState.landscape = true
        rule.waitForIdle()

        rule.onNodeWithTag("region_canvas").assertExists()
        rule.onNodeWithTag("region_zoom").assertTextEquals("400%")
    }

    private fun launchHost() {
        TestShell.startActivity("app.android.nopeview/.RegionTestActivity")
        rule.waitUntil(timeoutMillis = 10_000) {
            rule.onAllNodesWithTag("region_viewer")
                .fetchSemanticsNodes()
                .isNotEmpty()
        }
    }
}

private fun TouchInjectionScope.pinchOut() {
    pinch(
        start0 = center - Offset(80f, 0f),
        end0 = center - Offset(400f, 0f),
        start1 = center + Offset(80f, 0f),
        end1 = center + Offset(400f, 0f),
        durationMillis = 400,
    )
}
