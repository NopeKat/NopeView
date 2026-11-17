package app.android.nopeview

import android.graphics.Bitmap

object NativeCodec {
    init {
        System.loadLibrary("nopeview")
    }

    external fun create(codecDir: String): Long

    external fun destroy(handle: Long)

    external fun decodeFdBitmap(
        handle: Long,
        fd: Int,
        maxDimension: Int,
        outInfo: IntArray,
    ): Bitmap?
}
