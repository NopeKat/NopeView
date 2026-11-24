package app.android.nopeview

import android.content.ContentProvider
import android.content.ContentValues
import android.database.Cursor
import android.database.MatrixCursor
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.provider.OpenableColumns
import java.io.File
import java.io.FileNotFoundException

/**
 * Debug-only provider that serves files written by instrumentation tests.
 * Keeping the images behind a content URI exercises the same resolver path as
 * the system file picker.
 */
class TestImageProvider : ContentProvider() {
    override fun onCreate(): Boolean = true

    override fun getType(uri: Uri): String? = when (fileFor(uri).extension.lowercase()) {
        "png" -> "image/png"
        "jpg", "jpeg" -> "image/jpeg"
        "gif" -> "image/gif"
        "bmp" -> "image/bmp"
        "ico" -> "image/x-icon"
        "tga" -> "image/x-tga"
        "hdr" -> "image/vnd.radiance"
        "dng" -> "image/x-adobe-dng"
        "tif", "tiff" -> "image/tiff"
        "psd", "psb" -> "image/vnd.adobe.photoshop"
        "webp" -> "image/webp"
        "dds" -> "image/vnd-ms.dds"
        else -> "application/octet-stream"
    }

    override fun query(
        uri: Uri,
        projection: Array<out String>?,
        selection: String?,
        selectionArgs: Array<out String>?,
        sortOrder: String?,
    ): Cursor {
        val file = fileFor(uri)
        val columns = projection ?: arrayOf(
            OpenableColumns.DISPLAY_NAME,
            OpenableColumns.SIZE,
        )
        val cursor = MatrixCursor(columns, 1)
        val row = cursor.newRow()
        for (column in columns) {
            when (column) {
                OpenableColumns.DISPLAY_NAME -> row.add(file.name)
                OpenableColumns.SIZE -> row.add(file.length())
            }
        }
        return cursor
    }

    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor {
        if (mode != "r") {
            throw FileNotFoundException("Only read access is supported")
        }
        return ParcelFileDescriptor.open(fileFor(uri), ParcelFileDescriptor.MODE_READ_ONLY)
    }

    override fun insert(uri: Uri, values: ContentValues?): Uri? =
        throw UnsupportedOperationException("Test images are read-only")

    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?): Int =
        throw UnsupportedOperationException("Test images are read-only")

    override fun update(
        uri: Uri,
        values: ContentValues?,
        selection: String?,
        selectionArgs: Array<out String>?,
    ): Int = throw UnsupportedOperationException("Test images are read-only")

    private fun fileFor(uri: Uri): File {
        val name = uri.lastPathSegment ?: throw FileNotFoundException("Missing file name")
        require(name.matches(Regex("[A-Za-z0-9._-]+"))) { "Invalid test image name" }
        val directory = File(requireNotNull(context).cacheDir, "test-images").canonicalFile
        val file = File(directory, name).canonicalFile
        require(file.parentFile == directory) { "Invalid test image path" }
        if (!file.isFile) {
            throw FileNotFoundException("No test image: $name")
        }
        return file
    }
}
