package app.android.nopeview

import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.Color
import androidx.compose.ui.test.assertTextEquals
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.compose.ui.test.onAllNodesWithTag
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performTouchInput
import androidx.compose.ui.test.swipeLeft
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.runner.lifecycle.ActivityLifecycleMonitorRegistry
import androidx.test.runner.lifecycle.Stage
import org.junit.After
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test

/**
 * Uses a real 30 MP document for the region path, including the EXIF
 * orientation that changes the source dimensions.
 */
class LargeImageRegionTest {
    @get:Rule
    val rule = createEmptyComposeRule()

    private lateinit var context: Context
    private var preview: Bitmap? = null

    @Before
    fun setUp() {
        context = InstrumentationRegistry.getInstrumentation().targetContext
        SystemUi.wake()
        TestImages.reset(context)
    }

    @After
    fun tearDown() {
        InstrumentationRegistry.getInstrumentation().runOnMainSync {
            ActivityLifecycleMonitorRegistry.getInstance()
                .getActivitiesInStage(Stage.RESUMED)
                .forEach { it.finish() }
        }
        preview?.recycle()
        preview = null
    }

    @Test
    fun largePngLoadsRealTilesAndKeepsCacheBounded() {
        val uri = TestImages.writePng(context, "large.png", 6000, 5000)
        preview = Bitmap.createBitmap(600, 500, Bitmap.Config.ARGB_8888).apply {
            eraseColor(Color.rgb(60, 70, 80))
        }
        RegionTestState.reset(requireNotNull(preview), uri, 6000, 5000)
        TestShell.startActivity("app.android.nopeview/.RegionTestActivity")
        awaitTag("region_viewer")

        rule.waitUntil(timeoutMillis = 20_000) {
            RegionTestState.tileCount > 0
        }
        assertTrue(RegionTestState.tileCount <= 24)

        repeat(8) {
            rule.onNodeWithTag("region_canvas").performTouchInput { swipeLeft() }
            rule.waitForIdle()
            rule.waitUntil(timeoutMillis = 20_000) {
                RegionTestState.tileCount <= 24
            }
        }
        rule.waitUntil(timeoutMillis = 20_000) {
            RegionTestState.tileCount == 24
        }
        rule.waitUntil(timeoutMillis = 20_000) {
            RegionTestState.evictions > 0
        }
        assertTrue(RegionTestState.tileCount in 1..24)
    }

    @Test
    fun exifOrientedJpegKeepsRotatedDimensionsInRegionView() {
        val uri = TestImages.writeJpegWithOrientation(
            context,
            "rotated.jpg",
            6000,
            5000,
            6,
        )
        TestShell.startImage(Intent.ACTION_VIEW, uri, "image/jpeg")
        awaitTag("control_open")

        rule.onNodeWithTag("control_info").performClick()
        awaitTag("info_dialog")
        val dimensions = context.getString(R.string.image_info_dimensions) + ": 5000 x 6000"
        rule.onNodeWithText(dimensions).assertExists()
        rule.onNodeWithText(context.getString(R.string.close)).performClick()

        rule.onNodeWithTag("image_zoom_button").performClick()
        awaitTag("region_viewer")
        rule.onNodeWithTag("region_zoom").assertTextEquals("100%")
    }

    private fun awaitTag(tag: String) {
        rule.waitUntil(timeoutMillis = 20_000) {
            rule.onAllNodesWithTag(tag).fetchSemanticsNodes().isNotEmpty()
        }
    }
}
