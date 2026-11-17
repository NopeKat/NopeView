package app.android.nopeview

import android.graphics.Bitmap
import android.graphics.Matrix
import android.os.ParcelFileDescriptor
import androidx.exifinterface.media.ExifInterface
import java.io.Closeable
import java.io.IOException

/**
 * Result of one decode. The bitmap may already be oriented from EXIF;
 * sourceWidth and sourceHeight describe that oriented source.
 */
data class DecodedImage(
    val bitmap: Bitmap,
    val sourceWidth: Int,
    val sourceHeight: Int,
    val frameCount: Int,
    val frameDelayMs: Int,
) {
    val isPreview: Boolean
        get() = bitmap.width < sourceWidth || bitmap.height < sourceHeight
}

/**
 * Owns the router context for the process and turns decoded RGBA pixels into a
 * Bitmap. The JNI bridge locks the Bitmap pixels and copies the native rows
 * directly, so no intermediate Java byte array is allocated.
 */
class CodecSession(context: android.content.Context) : Closeable {
    private val handle: Long = NativeCodec.create(context.applicationInfo.nativeLibraryDir)

    val isReady: Boolean
        get() = handle != 0L

    fun decode(
        descriptor: ParcelFileDescriptor,
        frameIndex: Int,
        maxDimension: Int,
    ): DecodedImage? {
        if (handle == 0L || frameIndex < 0) {
            return null
        }

        val orientation = if (frameIndex == 0) {
            readOrientation(descriptor)
        } else {
            ExifInterface.ORIENTATION_NORMAL
        }
        val info = IntArray(8)
        val bitmap = NativeCodec.decodeFdBitmap(
            handle,
            descriptor.fd,
            frameIndex,
            maxDimension,
            info,
        ) ?: return null

        val width = info[0]
        val height = info[1]
        if (width <= 0 || height <= 0 || bitmap.width != width || bitmap.height != height) {
            bitmap.recycle()
            return null
        }

        val oriented = applyOrientation(bitmap, orientation)
        val swapsAxes = orientation in 5..8
        val rawSourceWidth = info[5].takeIf { it > 0 } ?: width
        val rawSourceHeight = info[6].takeIf { it > 0 } ?: height
        val sourceWidth = if (swapsAxes) rawSourceHeight else rawSourceWidth
        val sourceHeight = if (swapsAxes) rawSourceWidth else rawSourceHeight

        return DecodedImage(
            bitmap = oriented,
            sourceWidth = sourceWidth,
            sourceHeight = sourceHeight,
            frameCount = info[3],
            frameDelayMs = info[4],
        )
    }

    override fun close() {
        if (handle != 0L) {
            NativeCodec.destroy(handle)
        }
    }
}

private fun readOrientation(descriptor: ParcelFileDescriptor): Int {
    return try {
        val exif = ExifInterface(descriptor.fileDescriptor)
        val orientation = exif.getAttributeInt(
            ExifInterface.TAG_ORIENTATION,
            ExifInterface.ORIENTATION_NORMAL,
        )
        if (orientation == ExifInterface.ORIENTATION_UNDEFINED) {
            ExifInterface.ORIENTATION_NORMAL
        } else {
            orientation
        }
    } catch (_: IOException) {
        ExifInterface.ORIENTATION_NORMAL
    } catch (_: RuntimeException) {
        ExifInterface.ORIENTATION_NORMAL
    }
}

private fun applyOrientation(bitmap: Bitmap, orientation: Int): Bitmap {
    if (orientation == ExifInterface.ORIENTATION_NORMAL) {
        return bitmap
    }

    val matrix = Matrix()
    when (orientation) {
        ExifInterface.ORIENTATION_FLIP_HORIZONTAL -> matrix.setScale(-1f, 1f)
        ExifInterface.ORIENTATION_ROTATE_180 -> matrix.setRotate(180f)
        ExifInterface.ORIENTATION_FLIP_VERTICAL -> matrix.setScale(1f, -1f)
        ExifInterface.ORIENTATION_TRANSPOSE -> {
            matrix.setRotate(90f)
            matrix.postScale(-1f, 1f)
        }
        ExifInterface.ORIENTATION_ROTATE_90 -> matrix.setRotate(90f)
        ExifInterface.ORIENTATION_TRANSVERSE -> {
            matrix.setRotate(90f)
            matrix.postScale(1f, -1f)
        }
        ExifInterface.ORIENTATION_ROTATE_270 -> matrix.setRotate(270f)
        else -> return bitmap
    }

    return try {
        val transformed = Bitmap.createBitmap(
            bitmap,
            0,
            0,
            bitmap.width,
            bitmap.height,
            matrix,
            false,
        )
        if (transformed !== bitmap) {
            bitmap.recycle()
        }
        transformed
    } catch (_: IllegalArgumentException) {
        bitmap
    }
}
