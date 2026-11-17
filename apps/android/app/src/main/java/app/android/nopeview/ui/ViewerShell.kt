package app.android.nopeview.ui

import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.net.Uri
import android.provider.OpenableColumns
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
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
import androidx.core.content.edit
import androidx.core.graphics.createBitmap
import androidx.core.net.toUri
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
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
private const val PREVIEW_MAX_DIMENSION = 4096
private const val MAX_FULL_PIXELS = 24_000_000L
private const val PREFS_NAME = "viewer"
private const val KEY_LAST_URI = "last_uri"
private const val FRAME_BUFFER_COUNT = 2

@Composable
fun ViewerShell() {
    val context = LocalContext.current
    val activity = context as? ComponentActivity
    val prefs = remember(context) {
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
    }
    var selectedUri by remember { mutableStateOf<Uri?>(null) }
    var selectedName by remember { mutableStateOf<String?>(null) }
    var image by remember { mutableStateOf<DecodedImage?>(null) }
    var loading by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var animationJob by remember { mutableStateOf<Job?>(null) }
    var imageEpoch by remember { mutableIntStateOf(0) }
    var isPlaying by remember { mutableStateOf(true) }
    var startAtActual by remember { mutableStateOf(false) }
    var lifecycleStarted by remember {
        mutableStateOf(
            activity?.lifecycle?.currentState?.isAtLeast(Lifecycle.State.STARTED) ?: true,
        )
    }
    val decodeFailed = stringResource(R.string.decode_failed)
    val fullTooLarge = stringResource(R.string.full_too_large)

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

    DisposableEffect(activity) {
        if (activity == null) {
            onDispose { }
        } else {
            val observer = LifecycleEventObserver { _, event ->
                when (event) {
                    Lifecycle.Event.ON_START -> lifecycleStarted = true
                    Lifecycle.Event.ON_STOP -> lifecycleStarted = false
                    else -> Unit
                }
            }
            activity.lifecycle.addObserver(observer)
            onDispose { activity.lifecycle.removeObserver(observer) }
        }
    }

    fun openImage(uri: Uri, restored: Boolean) {
        val previousImage = image
        val previousJobActive = animationJob?.isActive == true
        image = null
        animationJob?.cancel()
        animationJob = null
        if (previousImage != null && !previousJobActive) {
            previousImage.bitmap.recycle()
        }
        selectedUri = uri
        selectedName = displayName(context, uri)
        imageEpoch += 1
        loading = true
        error = null
        startAtActual = false
        isPlaying = true

        if (!restored) {
            prefs.edit { putString(KEY_LAST_URI, uri.toString()) }
            runCatching {
                context.contentResolver.takePersistableUriPermission(
                    uri,
                    Intent.FLAG_GRANT_READ_URI_PERMISSION,
                )
            }
        }

        val descriptor = runCatching {
            context.contentResolver.openFileDescriptor(uri, "r")
        }.getOrNull()
        if (descriptor == null) {
            if (restored) {
                prefs.edit { remove(KEY_LAST_URI) }
                selectedUri = null
                selectedName = null
            }
            loading = false
            error = if (restored) null else decodeFailed
            return
        }

        animationJob = scope.launch {
            val buffers = arrayOfNulls<Bitmap>(FRAME_BUFFER_COUNT)
            var currentBuffer = -1
            try {
                val first = withContext(Dispatchers.IO) {
                    decodeMutex.withLock {
                        codec.decode(descriptor, 0, PREVIEW_MAX_DIMENSION)
                    }
                }
                if (first == null) {
                    if (restored) {
                        prefs.edit { remove(KEY_LAST_URI) }
                    }
                    loading = false
                    error = decodeFailed
                    return@launch
                }

                buffers[0] = first.bitmap
                currentBuffer = 0
                loading = false
                image = first

                if (first.frameCount > 1 && first.bitmap.width > 0 && first.bitmap.height > 0) {
                    buffers[1] = runCatching {
                        createBitmap(
                            first.bitmap.width,
                            first.bitmap.height,
                            Bitmap.Config.ARGB_8888,
                        )
                    }.getOrNull()

                    var frameIndex = 0
                    while (isActive) {
                        val current = image ?: break
                        if (current.frameCount <= 1) {
                            break
                        }
                        if (!isPlaying || !lifecycleStarted) {
                            delay(50)
                            continue
                        }

                        delay(current.frameDelayMs.coerceIn(20, 5000).toLong())
                        val nextIndex = (frameIndex + 1) % current.frameCount
                        val targetIndex = 1 - currentBuffer
                        val target = buffers[targetIndex] ?: runCatching {
                            createBitmap(
                                current.bitmap.width,
                                current.bitmap.height,
                                Bitmap.Config.ARGB_8888,
                            )
                        }.getOrNull()

                        var next = if (target == null) {
                            null
                        } else {
                            withContext(Dispatchers.IO) {
                                decodeMutex.withLock {
                                    codec.decodeInto(
                                        descriptor,
                                        nextIndex,
                                        PREVIEW_MAX_DIMENSION,
                                        target,
                                    )
                                }
                            }
                        }

                        if (next == null) {
                            val fallback = withContext(Dispatchers.IO) {
                                decodeMutex.withLock {
                                    codec.decode(descriptor, nextIndex, PREVIEW_MAX_DIMENSION)
                                }
                            } ?: break
                            if (buffers[targetIndex] != null) {
                                buffers[targetIndex]?.recycle()
                            }
                            buffers[targetIndex] = fallback.bitmap
                            next = fallback
                        } else {
                            buffers[targetIndex] = target
                        }

                        currentBuffer = targetIndex
                        frameIndex = nextIndex
                        image = next
                    }
                }
            } catch (cancel: CancellationException) {
                throw cancel
            } catch (_: Exception) {
                loading = false
                if (image == null) {
                    error = decodeFailed
                }
            } finally {
                for (buffer in buffers) {
                    if (buffer != null && buffer !== image?.bitmap) {
                        buffer.recycle()
                    }
                }
                runCatching { descriptor.close() }
            }
        }
    }

    fun requestFullImage() {
        val uri = selectedUri ?: return
        val current = image ?: return
        if (!current.isPreview || current.frameCount != 1) {
            return
        }
        val pixels = current.sourceWidth.toLong() * current.sourceHeight.toLong()
        if (pixels > MAX_FULL_PIXELS) {
            Toast.makeText(context, fullTooLarge, Toast.LENGTH_SHORT).show()
            return
        }

        loading = true
        scope.launch {
            val descriptor = runCatching {
                context.contentResolver.openFileDescriptor(uri, "r")
            }.getOrNull()
            if (descriptor == null) {
                loading = false
                Toast.makeText(context, decodeFailed, Toast.LENGTH_SHORT).show()
                return@launch
            }
            try {
                val full = withContext(Dispatchers.IO) {
                    decodeMutex.withLock {
                        codec.decode(descriptor, 0, 0)
                    }
                }
                if (full == null) {
                    loading = false
                    Toast.makeText(context, decodeFailed, Toast.LENGTH_SHORT).show()
                    return@launch
                }
                val previousImage = image
                image = full
                if (previousImage != null) {
                    previousImage.bitmap.recycle()
                }
                imageEpoch += 1
                startAtActual = true
                error = null
                loading = false
            } catch (cancel: CancellationException) {
                throw cancel
            } catch (_: Exception) {
                loading = false
                Toast.makeText(context, decodeFailed, Toast.LENGTH_SHORT).show()
            } finally {
                runCatching { descriptor.close() }
            }
        }
    }

    val openDocument = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.OpenDocument(),
    ) { uri: Uri? ->
        if (uri != null) {
            openImage(uri, restored = false)
        }
    }

    LaunchedEffect(Unit) {
        val saved = prefs.getString(KEY_LAST_URI, null) ?: return@LaunchedEffect
        val uri = runCatching { saved.toUri() }.getOrNull() ?: return@LaunchedEffect
        val hasPermission = context.contentResolver.persistedUriPermissions.any {
            it.uri == uri && it.isReadPermission
        }
        if (hasPermission) {
            openImage(uri, restored = true)
        } else {
            prefs.edit { remove(KEY_LAST_URI) }
        }
    }

    val canLoadFull = image?.let {
        it.isPreview && it.frameCount == 1 &&
            it.sourceWidth.toLong() * it.sourceHeight.toLong() <= MAX_FULL_PIXELS
    } == true

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
                        startAtActual = startAtActual,
                        onRequestFull = if (canLoadFull) {
                            { requestFullImage() }
                        } else {
                            null
                        },
                        frameIndex = image!!.frameIndex,
                        frameCount = image!!.frameCount,
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

            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.Center,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                FilledTonalButton(
                    onClick = { openDocument.launch(arrayOf("image/*")) },
                    enabled = !loading,
                ) {
                    Text(stringResource(R.string.open_image))
                }
                if ((image?.frameCount ?: 1) > 1) {
                    Spacer(modifier = Modifier.width(8.dp))
                    FilledTonalButton(
                        onClick = { isPlaying = !isPlaying },
                        enabled = !loading,
                    ) {
                        Text(
                            stringResource(
                                if (isPlaying) {
                                    R.string.pause_animation
                                } else {
                                    R.string.resume_animation
                                },
                            ),
                        )
                    }
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

private fun displayName(context: Context, uri: Uri): String {
    var name: String? = null
    val cursor = context.contentResolver.query(
        uri,
        arrayOf(OpenableColumns.DISPLAY_NAME),
        null,
        null,
        null,
    )
    if (cursor != null) {
        try {
            if (cursor.moveToFirst()) {
                val index = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                if (index >= 0) {
                    name = cursor.getString(index)
                }
            }
        } finally {
            cursor.close()
        }
    }
    return name ?: uri.lastPathSegment ?: uri.toString()
}
