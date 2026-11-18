package app.android.nopeview.ui

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Rect
import android.net.Uri
import android.os.Build
import androidx.activity.compose.BackHandler
import androidx.annotation.RequiresApi
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.detectTransformGestures
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
import androidx.compose.runtime.mutableFloatStateOf
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
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import app.android.nopeview.R
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.isActive
import kotlinx.coroutines.withContext
import kotlin.math.abs
import kotlin.math.min
import kotlin.math.roundToInt

private const val TILE_SIZE = 512
private const val TILE_CACHE_LIMIT = 24
private const val TILE_MARGIN = 1
private const val MAX_REGION_ZOOM = 4f

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
    onTilesChanged: ((Int) -> Unit)? = null,
    onTileEvicted: (() -> Unit)? = null,
) {
    BackHandler { onExit() }

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
                    .padding(8.dp)
                    .testTag("region_back"),
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
    var zoom by remember { mutableFloatStateOf(1f) }

    val tileCols = ((sourceWidth + TILE_SIZE - 1) / TILE_SIZE).coerceAtLeast(1)
    val tileRows = ((sourceHeight + TILE_SIZE - 1) / TILE_SIZE).coerceAtLeast(1)
    val visible = remember(viewport, offset, zoom, sourceWidth, sourceHeight, tileCols, tileRows) {
        if (zoom < 1f) {
            emptyList()
        } else {
            visibleTileKeys(viewport, offset, zoom, sourceWidth, sourceHeight, tileCols, tileRows)
        }
    }

    DisposableEffect(loader) {
        onDispose {
            for (tile in tiles.values) {
                tile.bitmap.recycle()
            }
            tiles.clear()
            onTilesChanged?.invoke(0)
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
                tiles.remove(victim)?.let { tile ->
                    tile.bitmap.recycle()
                    onTileEvicted?.invoke()
                }
            }
            tiles[key] = RegionTile(tile, tile.asImageBitmap())
            onTilesChanged?.invoke(tiles.size)
        }
    }

    Box(
        modifier = modifier
            .testTag("region_viewer")
            .onSizeChanged { viewport = it },
    ) {
        Canvas(
            modifier = Modifier
                .fillMaxSize()
                .testTag("region_canvas")
                .clipToBounds()
                .pointerInput(viewport, sourceWidth, sourceHeight) {
                    detectTransformGestures { centroid, pan, gestureZoom, _ ->
                        val oldZoom = zoom
                        val minimum = fitRegionZoom(viewport, sourceWidth, sourceHeight)
                        val newZoom = (oldZoom * gestureZoom)
                            .coerceIn(minimum, MAX_REGION_ZOOM)
                        val center = Offset(size.width / 2f, size.height / 2f)

                        if (oldZoom > 0f) {
                            val focal = centroid - center
                            offset = focal - (focal - offset) * (newZoom / oldZoom)
                        }
                        offset += pan
                        zoom = newZoom
                        offset = clampRegionOffset(
                            offset,
                            viewport,
                            sourceWidth,
                            sourceHeight,
                            newZoom,
                        )
                    }
                }
                .pointerInput(sourceWidth, sourceHeight, viewport) {
                    detectTapGestures(
                        onDoubleTap = { point ->
                            val fit = fitRegionZoom(viewport, sourceWidth, sourceHeight)
                            val target = if (abs(zoom - 1f) < 0.01f) fit else 1f
                            val center = Offset(size.width / 2f, size.height / 2f)
                            val focal = point - center

                            if (abs(target - fit) < 0.001f) {
                                offset = Offset.Zero
                            } else if (zoom > 0f) {
                                offset = focal - (focal - offset) * (target / zoom)
                            }
                            zoom = target
                            offset = clampRegionOffset(
                                offset,
                                viewport,
                                sourceWidth,
                                sourceHeight,
                                target,
                            )
                        },
                    )
                },
        ) {
            val canvas = drawContext.canvas
            canvas.save()
            canvas.translate(size.width / 2f + offset.x, size.height / 2f + offset.y)
            canvas.scale(zoom, zoom)
            canvas.translate(-sourceWidth / 2f, -sourceHeight / 2f)
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
            canvas.restore()
        }

        FilledTonalButton(
            onClick = onExit,
            modifier = Modifier
                .align(Alignment.TopEnd)
                .padding(8.dp)
                .testTag("region_back"),
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
            text = if (zoom < 1f) {
                stringResource(R.string.view_fit)
            } else {
                "${(zoom * 100f).roundToInt()}%"
            },
            modifier = Modifier
                .align(Alignment.BottomStart)
                .padding(8.dp)
                .testTag("region_zoom"),
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
    zoom: Float,
    sourceWidth: Int,
    sourceHeight: Int,
    tileCols: Int,
    tileRows: Int,
): List<Int> {
    if (viewport.width <= 0 || viewport.height <= 0 || zoom <= 0f) {
        return emptyList()
    }
    val halfWidth = viewport.width / (2f * zoom)
    val halfHeight = viewport.height / (2f * zoom)
    val left = sourceWidth / 2f - halfWidth - offset.x / zoom
    val right = sourceWidth / 2f + halfWidth - offset.x / zoom
    val top = sourceHeight / 2f - halfHeight - offset.y / zoom
    val bottom = sourceHeight / 2f + halfHeight - offset.y / zoom
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
    zoom: Float,
): Offset {
    val maxX = ((sourceWidth * zoom - viewport.width) / 2f).coerceAtLeast(0f)
    val maxY = ((sourceHeight * zoom - viewport.height) / 2f).coerceAtLeast(0f)
    return Offset(
        candidate.x.coerceIn(-maxX, maxX),
        candidate.y.coerceIn(-maxY, maxY),
    )
}

private fun fitRegionZoom(
    viewport: IntSize,
    sourceWidth: Int,
    sourceHeight: Int,
): Float {
    if (viewport.width <= 0 || viewport.height <= 0 ||
        sourceWidth <= 0 || sourceHeight <= 0
    ) {
        return 1f
    }
    return min(
        1f,
        min(
            viewport.width.toFloat() / sourceWidth,
            viewport.height.toFloat() / sourceHeight,
        ),
    )
}
