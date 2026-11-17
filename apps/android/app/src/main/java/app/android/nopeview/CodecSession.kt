package app.android.nopeview

import android.content.Context
import android.graphics.Bitmap
import java.io.Closeable
import java.nio.ByteBuffer

/**
 * Owns the router context for the process and turns decoded RGBA bytes into a
 * Bitmap. The router stays on the native heap; only the pixel copy reaches
 * the Java heap for the first working decode path.
 */
class CodecSession(context: Context) : Closeable {
    private val handle: Long = NativeCodec.create(context.applicationInfo.nativeLibraryDir)

    val isReady: Boolean
        get() = handle != 0L

    fun decode(fd: Int, maxDimension: Int): Bitmap? {
        if (handle == 0L) {
            return null
        }

        val info = IntArray(8)
        val bytes = NativeCodec.decodeFd(handle, fd, maxDimension, info) ?: return null
        val width = info[0]
        val height = info[1]
        val stride = info[2]
        if (width <= 0 || height <= 0 || stride != width * 4) {
            return null
        }

        val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
        bitmap.copyPixelsFromBuffer(ByteBuffer.wrap(bytes))
        return bitmap
    }

    override fun close() {
        if (handle != 0L) {
            NativeCodec.destroy(handle)
        }
    }
}
