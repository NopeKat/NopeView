package app.android.nopeview

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.net.Uri
import androidx.core.net.toUri
import androidx.exifinterface.media.ExifInterface
import androidx.test.platform.app.InstrumentationRegistry
import java.io.File
import java.io.FileOutputStream

/**
 * Writes deterministic images into the target app cache and returns URIs
 * served by the debug TestImageProvider.
 */
object TestImages {
    const val AUTHORITY = "app.android.nopeview.testimages"

    fun reset(context: Context) {
        clearPreferences(context)
        testImageDir(context).deleteRecursively()
        testImageDir(context).mkdirs()
    }

    fun clearPreferences(context: Context) {
        context.getSharedPreferences("viewer", Context.MODE_PRIVATE)
            .edit()
            .clear()
            .commit()
    }

    fun uri(name: String): Uri = "content://$AUTHORITY/$name".toUri()

    fun writePng(
        context: Context,
        name: String,
        width: Int,
        height: Int,
        label: String = "",
    ): Uri {
        val file = file(context, name)
        val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
        try {
            bitmap.eraseColor(Color.rgb(70, 80, 90))
            if (label.isNotEmpty()) {
                val paint = Paint().apply {
                    color = Color.WHITE
                    textSize = minOf(width, height) / 8f
                    isAntiAlias = true
                }
                Canvas(bitmap).drawText(label, width / 10f, height / 2f, paint)
            }
            FileOutputStream(file).use { output ->
                check(bitmap.compress(Bitmap.CompressFormat.PNG, 100, output))
            }
        } finally {
            bitmap.recycle()
        }
        return uri(name)
    }

    fun writeJpegWithOrientation(
        context: Context,
        name: String,
        width: Int,
        height: Int,
        orientation: Int,
    ): Uri {
        val file = file(context, name)
        val bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888)
        try {
            bitmap.eraseColor(Color.rgb(120, 90, 60))
            FileOutputStream(file).use { output ->
                check(bitmap.compress(Bitmap.CompressFormat.JPEG, 92, output))
            }
        } finally {
            bitmap.recycle()
        }
        ExifInterface(file).apply {
            setAttribute(ExifInterface.TAG_ORIENTATION, orientation.toString())
            saveAttributes()
        }
        return uri(name)
    }

    fun copyAsset(context: Context, name: String, assetName: String): Uri {
        val file = file(context, name)
        InstrumentationRegistry.getInstrumentation().context.assets
            .open(assetName)
            .use { input ->
                FileOutputStream(file).use { output -> input.copyTo(output) }
            }
        return uri(name)
    }

    fun copyGif(context: Context, name: String, assetName: String): Uri =
        copyAsset(context, name, assetName)

    fun delete(context: Context, name: String) {
        file(context, name).delete()
    }

    private fun file(context: Context, name: String): File {
        val directory = testImageDir(context)
        directory.mkdirs()
        return File(directory, name)
    }

    private fun testImageDir(context: Context): File =
        File(context.cacheDir, "test-images")
}
