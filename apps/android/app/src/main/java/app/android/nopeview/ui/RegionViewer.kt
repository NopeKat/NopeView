package app.android.nopeview.ui

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Rect
import android.net.Uri
import android.os.Build
import androidx.annotation.RequiresApi
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.FilterQuality
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.withTransform
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import app.android.nopeview.R
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.isActive
import kotlinx.coroutines.withContext
import kotlin.math.ceil
import kotlin.math.min

private const val TILE_SIZE = 512
private const val TILE_CACHE_LIMIT = 24
private const val TILE_MARGIN = 1

private data class RegionTile(
    val bitmap: Bitmap,
    val image: ImageBitmap,
)

/**
 * Draws a full-resolution region of a still image without decoding the whole
 * source. The preview bitmap stays underneath as a placeholder while the
 * visible tiles are decoded on demand. Tiles are only requested at 1:1, so
 * each screen pixel maps to exactly one source pixel.
 */
@Composable
fun RegionViewer(
    uri: Uri,
    preview: Bitmap,
    sourceWidth: Int,
    sourceHeight: Int,
    onExit: () -> Unit,
    modifier: Modifier = Modifier,
) {
    if (Build.VERSION.SDK_INT < Build.VERSION_CODES.P ||
        sourceWidth <= 0 ||
        sourceHeight <= 0
    ) {
        Box(modifier = modifier, contentAlignment = Alignment.Center) {
            Text(
                text = stringResource(R.string.region_unsupported),
                color = Color.White.copy(alpha = 0.7f),
            )
            FilledTonalButton(
                onClick = onExit,
                modifier = Modifier
                    .align(Alignment.TopEnd)
                    .padding(8.dp),
            ) {
                Text(stringResource(R.string.back_to_preview))
            }
        }
        return
    }

    val context = LocalContext.current
    val loader = remember(uri, sourceWidth, sourceHeight) {
        RegionTileLoader(context, uri)
    }
    val previewImage = remember(preview) { preview.asImageBitmap() }
    val tiles = remember { mutableStateMapOf<Int, RegionTile>() }
    val failed = remember { mutableSetOf<Int>() }
    var viewport by remember { mutableStateOf(IntSize.Zero) }
    var offset by remember { mutableStateOf(Offset.Zero) }

    val tileCols = ((sourceWidth + TILE_SIZE - 1) / TILE_SIZE).coerceAtLeast(1)
    val tileRows = ((sourceHeight + TILE_SIZE - 1) / TILE_SIZE).coerceAtLeast(1)
    val visible = remember(viewport, offset, sourceWidth, sourceHeight, tileCols, tileRows) {
        visibleTileKeys(viewport, offset, sourceWidth, sourceHeight, tileCols, tileRows)
    }

    DisposableEffect(loader) {
        onDispose {
            for (tile in tiles.values) {
                tile.bitmap.recycle()
            }
            tiles.clear()
        }
    }

    LaunchedEffect(visible, loader) {
        for (key in visible) {
            if (!isActive) {
                break
            }
            if (tiles.containsKey(key) || failed.contains(key)) {
                continue
            }
            val tile = loader.load(tileRect(key, tileCols, sourceWidth, sourceHeight))
            if (!isActive) {
                tile?.recycle()
                break
            }
            if (tile == null) {
                failed.add(key)
                continue
            }
            while (tiles.size >= TILE_CACHE_LIMIT) {
                val victim = tiles.keys.firstOrNull { it !in visible } ?: break
                tiles.remove(victim)?.bitmap?.recycle()
            }
            tiles[key] = RegionTile(tile, tile.asImageBitmap())
        }
    }

    Box(
        modifier = modifier.onSizeChanged { viewport = it },
    ) {
        Canvas(
            modifier = Modifier
                .fillMaxSize()
                .clipToBounds()
                .pointerInput(viewport, sourceWidth, sourceHeight) {
                    detectDragGestures { change, dragAmount ->
                        change.consume()
                        offset = clampRegionOffset(
                            offset + dragAmount,
                            viewport,
                            sourceWidth,
                            sourceHeight,
                        )
                    }
                },
        ) {
            withTransform({
                translate(size.width / 2f + offset.x, size.height / 2f + offset.y)
                translate(-sourceWidth / 2f, -sourceHeight / 2f)
            }) {
                drawImage(
                    image = previewImage,
                    dstOffset = IntOffset.Zero,
                    dstSize = IntSize(sourceWidth, sourceHeight),
                    filterQuality = FilterQuality.Low,
                )
                for ((key, tile) in tiles) {
                    val column = key and 0xFFFF
                    val row = key shr 16
                    drawImage(
                        image = tile.image,
                        dstOffset = IntOffset(column * TILE_SIZE, row * TILE_SIZE),
                        dstSize = IntSize(
                            min(TILE_SIZE, sourceWidth - column * TILE_SIZE),
                            min(TILE_SIZE, sourceHeight - row * TILE_SIZE),
                        ),
                        filterQuality = FilterQuality.None,
                    )
                }
            }
        }

        FilledTonalButton(
            onClick = onExit,
            modifier = Modifier
                .align(Alignment.TopEnd)
                .padding(8.dp),
        ) {
            Text(stringResource(R.string.back_to_preview))
        }

        Text(
            text = stringResource(R.string.region_label),
            modifier = Modifier
                .align(Alignment.TopStart)
                .padding(8.dp),
            style = MaterialTheme.typography.labelMedium,
            color = Color.White.copy(alpha = 0.6f),
        )

        Text(
            text = "100%",
            modifier = Modifier
                .align(Alignment.BottomStart)
                .padding(8.dp),
            style = MaterialTheme.typography.labelMedium,
            color = Color.White.copy(alpha = 0.55f),
        )
    }
}

@RequiresApi(Build.VERSION_CODES.P)
private class RegionTileLoader(
    context: Context,
    private val uri: Uri,
) {
    private val resolver = context.contentResolver

    suspend fun load(rect: Rect): Bitmap? = withContext(Dispatchers.IO) {
        runCatching {
            val source = android.graphics.ImageDecoder.createSource(resolver, uri)
            android.graphics.ImageDecoder.decodeBitmap(source) { decoder, _, _ ->
                decoder.allocator = android.graphics.ImageDecoder.ALLOCATOR_SOFTWARE
                decoder.setCrop(rect)
            }
        }.getOrNull()
    }
}

private fun visibleTileKeys(
    viewport: IntSize,
    offset: Offset,
    sourceWidth: Int,
    sourceHeight: Int,
    tileCols: Int,
    tileRows: Int,
): List<Int> {
    if (viewport.width <= 0 || viewport.height <= 0) {
        return emptyList()
    }
    val left = sourceWidth / 2f - viewport.width / 2f - offset.x
    val right = sourceWidth / 2f + viewport.width / 2f - offset.x
    val top = sourceHeight / 2f - viewport.height / 2f - offset.y
    val bottom = sourceHeight / 2f + viewport.height / 2f - offset.y
    val firstColumn = ((left / TILE_SIZE).toInt() - TILE_MARGIN).coerceAtLeast(0)
    val lastColumn = ((right / TILE_SIZE).toInt() + TILE_MARGIN).coerceAtMost(tileCols - 1)
    val firstRow = ((top / TILE_SIZE).toInt() - TILE_MARGIN).coerceAtLeast(0)
    val lastRow = ((bottom / TILE_SIZE).toInt() + TILE_MARGIN).coerceAtMost(tileRows - 1)

    val keys = ArrayList<Int>((lastColumn - firstColumn + 1) * (lastRow - firstRow + 1))
    for (row in firstRow..lastRow) {
        for (column in firstColumn..lastColumn) {
            keys.add(tileKey(column, row))
        }
    }
    return keys
}

private fun tileRect(key: Int, tileCols: Int, sourceWidth: Int, sourceHeight: Int): Rect {
    val column = key and 0xFFFF
    val row = key shr 16
    val left = column * TILE_SIZE
    val top = row * TILE_SIZE
    return Rect(
        left,
        top,
        min(left + TILE_SIZE, sourceWidth),
        min(top + TILE_SIZE, sourceHeight),
    ).also {
        require(column in 0 until tileCols) { "column out of range" }
    }
}

private fun tileKey(column: Int, row: Int): Int = (row shl 16) or column

private fun clampRegionOffset(
    candidate: Offset,
    viewport: IntSize,
    sourceWidth: Int,
    sourceHeight: Int,
): Offset {
    val maxX = ((sourceWidth - viewport.width) / 2f).coerceAtLeast(0f)
    val maxY = ((sourceHeight - viewport.height) / 2f).coerceAtLeast(0f)
    return Offset(
        candidate.x.coerceIn(-maxX, maxX),
        candidate.y.coerceIn(-maxY, maxY),
    )
}
