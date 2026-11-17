package app.android.nopeview

object NativeCodec {
    init {
        System.loadLibrary("nopeview")
    }

    external fun create(codecDir: String): Long

    external fun destroy(handle: Long)

    external fun decodeFd(
        handle: Long,
        fd: Int,
        maxDimension: Int,
        outInfo: IntArray,
    ): ByteArray?
}
