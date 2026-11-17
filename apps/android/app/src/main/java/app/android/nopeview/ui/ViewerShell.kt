package app.android.nopeview.ui

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import app.android.nopeview.CodecSession
import app.android.nopeview.DecodedImage
import app.android.nopeview.R
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext

private val ViewerCanvas = Color(0xFF0B0C0E)

@Composable
fun ViewerShell() {
    val context = LocalContext.current
    var selectedName by remember { mutableStateOf<String?>(null) }
    var image by remember { mutableStateOf<DecodedImage?>(null) }
    var loading by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var animationJob by remember { mutableStateOf<Job?>(null) }
    var imageEpoch by remember { mutableStateOf(0) }
    val decodeFailed = stringResource(R.string.decode_failed)

    val codec = remember(context) { CodecSession(context) }
    val decodeMutex = remember { Mutex() }
    val scope = rememberCoroutineScope()
    val currentImage by rememberUpdatedState(image)
    val currentJob by rememberUpdatedState(animationJob)
    DisposableEffect(codec) {
        onDispose {
            currentJob?.cancel()
            currentImage?.bitmap?.recycle()
            codec.close()
        }
    }

    val openDocument = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.OpenDocument(),
    ) { uri: Uri? ->
        if (uri != null) {
            animationJob?.cancel()
            animationJob = null
            selectedName = displayName(context, uri)
            imageEpoch += 1
            loading = true
            error = null
            image = null

            val descriptor = runCatching {
                context.contentResolver.openFileDescriptor(uri, "r")
            }.getOrNull()
            if (descriptor == null) {
                loading = false
                error = decodeFailed
            } else {
                animationJob = scope.launch {
                    try {
                        val first = withContext(Dispatchers.IO) {
                            decodeMutex.withLock {
                                codec.decode(descriptor, 0, PREVIEW_MAX_DIMENSION)
                            }
                        }
                        if (first == null) {
                            loading = false
                            error = decodeFailed
                            return@launch
                        }

                        loading = false
                        image = first
                        var frameIndex = 0
                        while (isActive) {
                            val current = image ?: break
                            if (current.frameCount <= 1) {
                                break
                            }
                            delay(current.frameDelayMs.coerceIn(20, 5000).toLong())
                            val nextIndex = (frameIndex + 1) % current.frameCount
                            val next = withContext(Dispatchers.IO) {
                                decodeMutex.withLock {
                                    codec.decode(descriptor, nextIndex, PREVIEW_MAX_DIMENSION)
                                }
                            } ?: break
                            frameIndex = nextIndex
                            image = next
                        }
                    } catch (cancel: CancellationException) {
                        throw cancel
                    } catch (_: Exception) {
                        loading = false
                        if (image == null) {
                            error = decodeFailed
                        }
                    } finally {
                        runCatching { descriptor.close() }
                    }
                }
            }
        }
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(ViewerCanvas)
            .windowInsetsPadding(WindowInsets.safeDrawing)
            .padding(horizontal = 24.dp, vertical = 16.dp),
    ) {
        Column(modifier = Modifier.fillMaxSize()) {
            Text(
                text = stringResource(R.string.app_name),
                style = MaterialTheme.typography.headlineMedium,
                color = Color.White,
            )

            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f)
                    .padding(vertical = 12.dp),
                contentAlignment = Alignment.Center,
            ) {
                when {
                    loading -> CircularProgressIndicator()
                    image != null -> ZoomableImage(
                        bitmap = image!!.bitmap,
                        isPreview = image!!.isPreview,
                        resetKey = imageEpoch,
                        modifier = Modifier.fillMaxSize(),
                    )
                    else -> Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        Text(
                            text = selectedName ?: stringResource(R.string.empty_title),
                            style = MaterialTheme.typography.titleLarge,
                            color = Color.White,
                            textAlign = TextAlign.Center,
                        )
                        Spacer(modifier = Modifier.height(8.dp))
                        Text(
                            text = error ?: if (selectedName == null) {
                                stringResource(R.string.empty_body)
                            } else {
                                stringResource(R.string.selected_hint)
                            },
                            style = MaterialTheme.typography.bodyMedium,
                            color = if (error != null) {
                                MaterialTheme.colorScheme.error
                            } else {
                                Color.White.copy(alpha = 0.65f)
                            },
                            textAlign = TextAlign.Center,
                        )
                    }
                }
            }

            Column(
                modifier = Modifier.fillMaxWidth(),
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                FilledTonalButton(
                    onClick = { openDocument.launch(arrayOf("image/*")) },
                    enabled = !loading,
                ) {
                    Text(stringResource(R.string.open_image))
                }
            }

            Spacer(modifier = Modifier.height(12.dp))
            Text(
                text = stringResource(R.string.version_label),
                style = MaterialTheme.typography.labelSmall,
                color = Color.White.copy(alpha = 0.4f),
            )
        }
    }
}

private const val PREVIEW_MAX_DIMENSION = 4096

private fun displayName(context: Context, uri: Uri): String {
    context.contentResolver.query(
        uri,
        arrayOf(OpenableColumns.DISPLAY_NAME),
        null,
        null,
        null,
    )?.use { cursor ->
        if (cursor.moveToFirst()) {
            val index = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
            if (index >= 0) {
                return cursor.getString(index)
            }
        }
    }
    return uri.lastPathSegment ?: uri.toString()
}
