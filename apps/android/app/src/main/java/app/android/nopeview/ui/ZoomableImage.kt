package app.android.nopeview.ui

import android.graphics.Bitmap
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
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.FilterQuality
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.withTransform
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import app.android.nopeview.R
import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.min
import kotlin.math.roundToInt
import kotlin.math.sin

private const val MIN_ZOOM = 0.02f
private const val MAX_ZOOM = 8f

/**
 * Draws the bitmap in screen pixels so 100% means one bitmap pixel per
 * physical screen pixel. A double tap or the corner button switches between
 * fit and actual size.
 */
@Composable
fun ZoomableImage(
    bitmap: Bitmap,
    modifier: Modifier = Modifier,
    isPreview: Boolean = false,
    resetKey: Any? = null,
    startAtActual: Boolean = false,
    onRequestFull: (() -> Unit)? = null,
    onRequestRegion: (() -> Unit)? = null,
    frameIndex: Int = 0,
    frameCount: Int = 1,
) {
    val image = remember(bitmap) { bitmap.asImageBitmap() }
    val imageWidth = bitmap.width
    val imageHeight = bitmap.height

    var viewport by remember { mutableStateOf(IntSize.Zero) }
    var zoom by remember { mutableFloatStateOf(1f) }
    var offset by remember { mutableStateOf(Offset.Zero) }
    var rotation by remember { mutableFloatStateOf(0f) }
    var initialActual by remember(resetKey) { mutableStateOf(startAtActual) }

    LaunchedEffect(resetKey, viewport) {
        if (viewport.width > 0 && viewport.height > 0) {
            zoom = if (initialActual) {
                initialActual = false
                1f
            } else {
                fitZoom(viewport, imageWidth, imageHeight)
            }
            offset = Offset.Zero
            rotation = 0f
        }
    }

    Box(
        modifier = modifier
            .onSizeChanged { viewport = it },
    ) {
        Canvas(
            modifier = Modifier
                .fillMaxSize()
                .clipToBounds()
                .pointerInput(imageWidth, imageHeight, viewport) {
                    detectTransformGestures { centroid, pan, gestureZoom, gestureRotation ->
                        val oldZoom = zoom
                        val minimumZoom = minZoom(viewport, imageWidth, imageHeight)
                        val newZoom = (oldZoom * gestureZoom).coerceIn(minimumZoom, MAX_ZOOM)
                        val center = Offset(size.width / 2f, size.height / 2f)

                        if (oldZoom > 0f) {
                            val focal = centroid - center
                            offset = focal - (focal - offset) * (newZoom / oldZoom)
                        }
                        offset += pan
                        rotation = (rotation + gestureRotation) % 360f
                        zoom = newZoom
                        offset = clampOffset(
                            offset, newZoom, rotation, size, imageWidth, imageHeight,
                        )
                    }
                }
                .pointerInput(imageWidth, imageHeight, viewport) {
                    detectTapGestures(
                        onDoubleTap = { point ->
                            val goToActual = abs(zoom - 1f) >= 0.01f
                            val target = if (goToActual) {
                                1f
                            } else {
                                fitZoom(viewport, imageWidth, imageHeight)
                            }
                            val center = Offset(size.width / 2f, size.height / 2f)
                            val focal = point - center
                            val newZoom = target.coerceIn(
                                minZoom(viewport, imageWidth, imageHeight),
                                MAX_ZOOM,
                            )

                            if (zoom > 0f) {
                                offset = focal - (focal - offset) * (newZoom / zoom)
                            }
                            if (!goToActual) {
                                rotation = 0f
                            }
                            zoom = newZoom
                            offset = clampOffset(
                                offset, newZoom, rotation, size, imageWidth, imageHeight,
                            )
                        },
                    )
                },
        ) {
            val centerX = size.width / 2f + offset.x
            val centerY = size.height / 2f + offset.y
            withTransform({
                translate(centerX, centerY)
                rotate(rotation)
                scale(zoom, zoom, pivot = Offset.Zero)
                translate(-imageWidth / 2f, -imageHeight / 2f)
            }) {
                drawImage(
                    image = image,
                    dstOffset = IntOffset.Zero,
                    dstSize = IntSize(imageWidth, imageHeight),
                    filterQuality = FilterQuality.Medium,
                )
            }
        }

        FilledTonalButton(
            onClick = {
                if (isPreview && onRequestFull != null) {
                    onRequestFull.invoke()
                    return@FilledTonalButton
                }
                if (isPreview && onRequestRegion != null) {
                    onRequestRegion.invoke()
                    return@FilledTonalButton
                }
                val goToActual = abs(zoom - 1f) >= 0.01f
                val target = if (goToActual) {
                    1f
                } else {
                    fitZoom(viewport, imageWidth, imageHeight)
                }
                zoom = target.coerceIn(
                    minZoom(viewport, imageWidth, imageHeight),
                    MAX_ZOOM,
                )
                offset = Offset.Zero
                if (!goToActual) {
                    rotation = 0f
                }
            },
            modifier = Modifier
                .align(Alignment.TopEnd)
                .padding(8.dp),
        ) {
            Text(
                text = when {
                    isPreview && onRequestFull != null ->
                        stringResource(R.string.load_full)
                    isPreview && onRequestRegion != null ->
                        stringResource(R.string.load_region)
                    abs(zoom - 1f) < 0.01f -> stringResource(R.string.view_fit)
                    isPreview -> stringResource(R.string.view_preview_actual)
                    else -> stringResource(R.string.view_actual)
                },
            )
        }

        if (isPreview) {
            Text(
                text = "${stringResource(R.string.view_preview)} " +
                    "${bitmap.width}×${bitmap.height}",
                modifier = Modifier
                    .align(Alignment.TopStart)
                    .padding(8.dp),
                style = MaterialTheme.typography.labelMedium,
                color = Color.White.copy(alpha = 0.55f),
            )
        }

        Text(
            text = "${(zoom * 100f).roundToInt()}%",
            modifier = Modifier
                .align(Alignment.BottomStart)
                .padding(8.dp),
            style = MaterialTheme.typography.labelMedium,
            color = Color.White.copy(alpha = 0.55f),
        )

        if (frameCount > 1) {
            Text(
                text = "${frameIndex + 1} / $frameCount",
                modifier = Modifier
                    .align(Alignment.BottomEnd)
                    .padding(8.dp),
                style = MaterialTheme.typography.labelMedium,
                color = Color.White.copy(alpha = 0.55f),
            )
        }
    }
}

private fun fitZoom(viewport: IntSize, imageWidth: Int, imageHeight: Int): Float {
    if (viewport.width <= 0 || viewport.height <= 0 || imageWidth <= 0 || imageHeight <= 0) {
        return 1f
    }
    val ratio = min(
        viewport.width.toFloat() / imageWidth,
        viewport.height.toFloat() / imageHeight,
    )
    return min(1f, ratio)
}

private fun minZoom(viewport: IntSize, imageWidth: Int, imageHeight: Int): Float {
    return (fitZoom(viewport, imageWidth, imageHeight) * 0.5f).coerceAtLeast(MIN_ZOOM)
}

private fun clampOffset(
    candidate: Offset,
    zoom: Float,
    rotation: Float,
    viewport: IntSize,
    imageWidth: Int,
    imageHeight: Int,
): Offset {
    val radians = Math.toRadians(rotation.toDouble())
    val cosine = abs(cos(radians)).toFloat()
    val sine = abs(sin(radians)).toFloat()
    val extentX = (imageWidth * cosine + imageHeight * sine) * zoom
    val extentY = (imageWidth * sine + imageHeight * cosine) * zoom
    val maxX = ((extentX - viewport.width) / 2f).coerceAtLeast(0f)
    val maxY = ((extentY - viewport.height) / 2f).coerceAtLeast(0f)
    return Offset(
        candidate.x.coerceIn(-maxX, maxX),
        candidate.y.coerceIn(-maxY, maxY),
    )
}
