package app.android.nopeview

import android.content.Context
import android.os.Debug
import android.util.Log
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/**
 * Measures process PSS around the decode paths that can grow without bound.
 * The thresholds are deliberately generous for CI devices; the printed values
 * are the useful baseline.
 */
class MemoryBaselineTest {
    private lateinit var context: Context

    @Before
    fun setUp() {
        context = InstrumentationRegistry.getInstrumentation().targetContext
        TestImages.reset(context)
    }

    @Test
    fun largePreviewDecodeStaysWithinBudget() {
        val uri = TestImages.writePng(context, "memory-large.png", 6000, 5000)
        val session = CodecSession(context)
        context.contentResolver.openFileDescriptor(uri, "r").use { descriptor ->
            assertNotNull(descriptor)
            settle()
            val before = pssKb()
            val image = session.decode(requireNotNull(descriptor), 0, 4096)
            assertNotNull(image)
            settle()
            val after = pssKb()
            val delta = after - before
            Log.i(TAG, "6000x5000 preview delta=${delta}KB pss=${after}KB")
            assertTrue("preview PSS delta ${delta}KB", delta < 220_000)
            assertTrue(image!!.bitmap.width <= 4096)
            assertTrue(image.bitmap.height <= 4096)
            image.bitmap.recycle()
        }
        session.close()
    }

    @Test
    fun twentyMegapixelFullDecodeStaysWithinBudget() {
        val uri = TestImages.writePng(context, "memory-full.png", 5000, 4000)
        val session = CodecSession(context)
        context.contentResolver.openFileDescriptor(uri, "r").use { descriptor ->
            assertNotNull(descriptor)
            settle()
            val before = pssKb()
            val image = session.decode(requireNotNull(descriptor), 0, 0)
            assertNotNull(image)
            settle()
            val after = pssKb()
            val delta = after - before
            Log.i(TAG, "5000x4000 full delta=${delta}KB pss=${after}KB")
            assertTrue("full PSS delta ${delta}KB", delta < 260_000)
            assertTrue(image!!.bitmap.width == 5000)
            assertTrue(image.bitmap.height == 4000)
            image.bitmap.recycle()
        }
        session.close()
    }

    @Test
    fun animatedGifCacheStaysWithinBudget() {
        val uri = TestImages.copyGif(context, "memory-anim.gif", "rotating-earth.gif")
        val session = CodecSession(context)
        context.contentResolver.openFileDescriptor(uri, "r").use { descriptor ->
            assertNotNull(descriptor)
            settle()
            val before = pssKb()
            val image = session.decode(requireNotNull(descriptor), 0, 4096)
            assertNotNull(image)
            settle()
            val after = pssKb()
            val delta = after - before
            Log.i(TAG, "44-frame GIF delta=${delta}KB pss=${after}KB")
            assertTrue("GIF PSS delta ${delta}KB", delta < 140_000)
            assertTrue(image!!.frameCount > 1)
            image.bitmap.recycle()
        }
        session.close()
    }

    private fun settle() {
        System.gc()
        Thread.sleep(250)
    }

    private fun pssKb(): Int {
        val info = Debug.MemoryInfo()
        Debug.getMemoryInfo(info)
        return info.totalPss
    }

    private companion object {
        const val TAG = "NopeViewMemory"
    }
}
