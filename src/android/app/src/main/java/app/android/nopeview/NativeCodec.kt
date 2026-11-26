// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

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
        frameIndex: Int,
        maxDimension: Int,
        outInfo: IntArray,
    ): Bitmap?

    external fun decodeFdBitmapInto(
        handle: Long,
        fd: Int,
        frameIndex: Int,
        maxDimension: Int,
        outInfo: IntArray,
        target: Bitmap,
    ): Boolean
}
