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
import androidx.test.runner.lifecycle.ActivityLifecycleMonitorRegistry
import androidx.test.runner.lifecycle.Stage
import org.junit.After
import org.junit.Before
import org.junit.Rule
import org.junit.Test

/**
 * Covers the codecs added to the Android CMake target: PNG-in-ICO, uncompressed
 * TGA, flat Radiance HDR, a CFA DNG, uncompressed RGB TIFF, a raw RGB PSD,
 * a lossy WebP, a BC1 DDS, an SVG, an uncompressed EXR and a JP2. The WebP
 * fixture comes from the MIT-licensed sample-files collection; the JP2 fixture
 * is the openly licensed jpylyzer reference.jp2 derived from a public-domain
 * source. Each fixture is copied into the app cache and opened through the same
 * content URI path as a system file picker.
 */
class AdditionalFormatDecodeTest {
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

    @Test
    fun icoWithPngPayloadOpens() {
        openAndCheck("sample.ico", "image/x-icon", "sample.ico", 64, 200)
    }

    @Test
    fun uncompressedTgaOpens() {
        openAndCheck("sample.tga", "image/x-tga", "sample.tga", 8, 8)
    }

    @Test
    fun flatRadianceHdrOpens() {
        openAndCheck("sample.hdr", "image/vnd.radiance", "sample.hdr", 4, 4)
    }

    @Test
    fun cfaDngOpens() {
        openAndCheck("sample.dng", "image/x-adobe-dng", "sample.dng", 8, 6)
    }

    @Test
    fun uncompressedTiffOpens() {
        openAndCheck("sample.tiff", "image/tiff", "sample.tiff", 8, 6)
    }

    @Test
    fun rawRgbPsdOpens() {
        openAndCheck("sample.psd", "image/vnd.adobe.photoshop", "sample.psd", 8, 6)
    }

    @Test
    fun lossyWebpOpens() {
        openAndCheck("sample.webp", "image/webp", "sample.webp", 32, 88)
    }

    @Test
    fun bc1DdsOpens() {
        openAndCheck("sample.dds", "image/vnd-ms.dds", "sample.dds", 8, 8)
    }

    @Test
    fun svgOpens() {
        openAndCheck("sample.svg", "image/svg+xml", "sample.svg", 8, 6)
    }

    @Test
    fun uncompressedExrOpens() {
        openAndCheck("sample.exr", "image/x-exr", "sample.exr", 8, 6)
    }

    @Test
    fun jp2Opens() {
        openAndCheck("sample.jp2", "image/jp2", "sample.jp2", 7, 157)
    }

    private fun openAndCheck(
        assetName: String,
        mimeType: String,
        fileName: String,
        width: Int,
        height: Int,
    ) {
        val uri = TestImages.copyAsset(context, fileName, assetName)
        TestShell.startImage(Intent.ACTION_VIEW, uri, mimeType)
        awaitTag("control_open")

        rule.onNodeWithTag("control_info").performClick()
        awaitTag("info_dialog")
        val dimensions = context.getString(R.string.image_info_dimensions) +
            ": $width x $height"
        rule.onNodeWithText(dimensions).assertExists()
        rule.onNodeWithText(context.getString(R.string.close)).performClick()
    }

    private fun awaitTag(tag: String) {
        rule.waitUntil(timeoutMillis = 15_000) {
            rule.onAllNodesWithTag(tag).fetchSemanticsNodes().isNotEmpty()
        }
        if (tag == "control_open") {
            SystemUi.keepOn()
        }
    }

    private fun finishResumedActivities() {
        InstrumentationRegistry.getInstrumentation().runOnMainSync {
            ActivityLifecycleMonitorRegistry.getInstance()
                .getActivitiesInStage(Stage.RESUMED)
                .forEach { it.finish() }
        }
    }
}
