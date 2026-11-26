// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

package app.android.nopeview.ui

import android.app.ActivityManager
import android.content.ClipData
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.Configuration
import android.graphics.Bitmap
import android.net.Uri
import android.os.Process
import android.provider.OpenableColumns
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.horizontalScroll
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
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.core.content.edit
import androidx.core.graphics.createBitmap
import androidx.core.net.toUri
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import app.android.nopeview.CodecSession
import app.android.nopeview.DecodedImage
import app.android.nopeview.OpenRequest
import app.android.nopeview.R
import app.android.nopeview.RecentEntry
import app.android.nopeview.RecentFiles
import java.util.Locale
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
private const val MAX_FULL_PIXELS_CEILING = 24_000_000L
private const val MIN_FULL_PIXELS = 4_000_000L
private const val FULL_HEAP_CAP_BYTES = 96L * 1024L * 1024L
private const val LOW_RAM_FULL_PIXELS = 8_000_000L
private const val PREFS_NAME = "viewer"
private const val KEY_LAST_URI = "last_uri"
private const val FRAME_BUFFER_COUNT = 2

@Composable
fun ViewerShell(incomingRequest: OpenRequest? = null) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    val prefs = remember(context) {
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
    }
    val landscape = LocalConfiguration.current.orientation ==
        Configuration.ORIENTATION_LANDSCAPE
    var selectedUri by remember { mutableStateOf<Uri?>(null) }
    var selectedName by remember { mutableStateOf<String?>(null) }
    var image by remember { mutableStateOf<DecodedImage?>(null) }
    var loading by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var animationJob by remember { mutableStateOf<Job?>(null) }
    var imageEpoch by remember { mutableIntStateOf(0) }
    var isPlaying by remember { mutableStateOf(true) }
    var startAtActual by remember { mutableStateOf(false) }
    var regionMode by remember { mutableStateOf(false) }
    var activeUriString by rememberSaveable { mutableStateOf<String?>(null) }
    var recentEntries by remember { mutableStateOf(RecentFiles.load(context)) }
    var recentVisible by remember { mutableStateOf(false) }
    var infoVisible by remember { mutableStateOf(false) }
    var viewResetSignal by remember { mutableIntStateOf(0) }
    var quarterTurns by remember { mutableIntStateOf(0) }
    var saveSourceUri by remember { mutableStateOf<Uri?>(null) }
    var lifecycleStarted by remember {
        mutableStateOf(lifecycleOwner.lifecycle.currentState.isAtLeast(Lifecycle.State.STARTED))
    }
    val fullPixelLimit = remember(context) { fullPixelBudget(context) }
    val decodeFailed = stringResource(R.string.decode_failed)
    val fullTooLarge = stringResource(R.string.full_too_large)
    val shareTitle = stringResource(R.string.share_image)

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

    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            when (event) {
                Lifecycle.Event.ON_START -> lifecycleStarted = true
                Lifecycle.Event.ON_STOP -> lifecycleStarted = false
                else -> Unit
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }

    fun forgetSaved(uri: Uri) {
        if (prefs.getString(KEY_LAST_URI, null) == uri.toString()) {
            prefs.edit { remove(KEY_LAST_URI) }
        }
    }

    fun openImage(uri: Uri, restored: Boolean) {
        val previousImage = image
        image = null
        animationJob?.cancel()
        animationJob = null
        // The region viewer still draws the preview until it is disposed, so
        // recycling here would race with the next frame. Let that path drop the
        // bitmap through normal garbage collection instead.
        if (!regionMode) {
            previousImage?.bitmap?.recycle()
        }
        selectedUri = uri
        selectedName = displayName(context, uri)
        activeUriString = uri.toString()
        imageEpoch += 1
        val generation = imageEpoch
        loading = true
        error = null
        regionMode = false
        startAtActual = false
        isPlaying = true
        quarterTurns = 0

        if (!restored) {
            val persisted = runCatching {
                context.contentResolver.takePersistableUriPermission(
                    uri,
                    Intent.FLAG_GRANT_READ_URI_PERMISSION,
                )
            }.isSuccess
            prefs.edit {
                if (persisted) {
                    putString(KEY_LAST_URI, uri.toString())
                } else {
                    remove(KEY_LAST_URI)
                }
            }
        }

        val descriptor = runCatching {
            context.contentResolver.openFileDescriptor(uri, "r")
        }.getOrNull()
        if (descriptor == null) {
            forgetSaved(uri)
            activeUriString = null
            if (restored) {
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
                    if (generation != imageEpoch) {
                        return@launch
                    }
                    forgetSaved(uri)
                    activeUriString = null
                    loading = false
                    error = decodeFailed
                    return@launch
                }
                if (generation != imageEpoch) {
                    first.bitmap.recycle()
                    return@launch
                }

                buffers[0] = first.bitmap
                currentBuffer = 0
                loading = false
                image = first
                recentEntries = RecentFiles.add(
                    context,
                    RecentEntry(
                        uri = uri.toString(),
                        name = selectedName ?: displayName(context, uri),
                        mimeType = context.contentResolver.getType(uri),
                        openedAt = System.currentTimeMillis(),
                    ),
                )

                if (first.frameCount > 1 && first.bitmap.width > 0 && first.bitmap.height > 0) {
                    buffers[1] = runCatching {
                        createBitmap(
                            first.bitmap.width,
                            first.bitmap.height,
                            Bitmap.Config.ARGB_8888,
                        )
                    }.getOrNull()

                    var frameIndex = 0
                    while (isActive && generation == imageEpoch) {
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
                        if (generation != imageEpoch) {
                            break
                        }
                        image = next
                    }
                }
            } catch (cancel: CancellationException) {
                throw cancel
            } catch (_: Exception) {
                if (generation == imageEpoch) {
                    loading = false
                    if (image == null) {
                        error = decodeFailed
                    }
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
        if (pixels > fullPixelLimit) {
            Toast.makeText(context, fullTooLarge, Toast.LENGTH_SHORT).show()
            return
        }

        val generation = imageEpoch
        loading = true
        regionMode = false
        scope.launch {
            val descriptor = runCatching {
                context.contentResolver.openFileDescriptor(uri, "r")
            }.getOrNull()
            if (descriptor == null) {
                if (generation == imageEpoch) {
                    loading = false
                    Toast.makeText(context, decodeFailed, Toast.LENGTH_SHORT).show()
                }
                return@launch
            }
            try {
                val full = withContext(Dispatchers.IO) {
                    decodeMutex.withLock {
                        codec.decode(descriptor, 0, 0)
                    }
                }
                if (full == null) {
                    if (generation == imageEpoch) {
                        loading = false
                        Toast.makeText(context, decodeFailed, Toast.LENGTH_SHORT).show()
                    }
                    return@launch
                }
                if (generation != imageEpoch) {
                    full.bitmap.recycle()
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
                if (generation == imageEpoch) {
                    loading = false
                    Toast.makeText(context, decodeFailed, Toast.LENGTH_SHORT).show()
                }
            } finally {
                runCatching { descriptor.close() }
            }
        }
    }

    fun requestRegionView() {
        val current = image ?: return
        if (selectedUri == null || !current.isPreview || current.frameCount != 1) {
            return
        }
        regionMode = true
        isPlaying = false
    }

    val openDocument = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.OpenDocument(),
    ) { uri: Uri? ->
        if (uri != null) {
            openImage(uri, restored = false)
        }
    }

    val saveDocument = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.CreateDocument("image/*"),
    ) { destination: Uri? ->
        val source = saveSourceUri
        saveSourceUri = null
        if (destination != null && source != null) {
            scope.launch {
                val success = withContext(Dispatchers.IO) {
                    runCatching {
                        val input = context.contentResolver.openInputStream(source)
                            ?: error("source is not readable")
                        input.use { inputStream ->
                            val output = context.contentResolver.openOutputStream(destination)
                                ?: error("destination is not writable")
                            output.use { outputStream ->
                                inputStream.copyTo(outputStream)
                            }
                        }
                    }.isSuccess
                }
                Toast.makeText(
                    context,
                    if (success) R.string.save_done else R.string.save_failed,
                    Toast.LENGTH_SHORT,
                ).show()
            }
        }
    }

    fun rotateView() {
        quarterTurns = (quarterTurns + 1) % 4
    }

    fun resetView() {
        quarterTurns = 0
        viewResetSignal += 1
    }

    fun shareCurrentImage() {
        val uri = selectedUri ?: return
        val mimeType = context.contentResolver.getType(uri) ?: "image/*"
        val send = Intent(Intent.ACTION_SEND).apply {
            type = mimeType
            putExtra(Intent.EXTRA_STREAM, uri)
            clipData = ClipData.newRawUri(null, uri)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
        runCatching {
            context.startActivity(
                Intent.createChooser(send, shareTitle),
            )
        }.onFailure {
            Toast.makeText(context, R.string.no_share_app, Toast.LENGTH_SHORT).show()
        }
    }

    fun saveCurrentImage() {
        val uri = selectedUri ?: return
        saveSourceUri = uri
        saveDocument.launch(selectedName ?: "image")
    }

    fun openRecent(entry: RecentEntry) {
        val uri = runCatching { entry.uri.toUri() }.getOrNull() ?: return
        recentVisible = false
        if (!canRead(context, uri)) {
            Toast.makeText(context, R.string.permission_lost, Toast.LENGTH_SHORT).show()
            return
        }
        openImage(uri, restored = false)
    }

    fun removeRecent(entry: RecentEntry) {
        recentEntries = RecentFiles.remove(context, entry.uri)
    }

    fun clearRecent() {
        RecentFiles.clear(context)
        recentEntries = emptyList()
    }

    LaunchedEffect(incomingRequest?.id) {
        val request = incomingRequest ?: return@LaunchedEffect
        openImage(request.uri, restored = false)
    }

    LaunchedEffect(Unit) {
        if (incomingRequest != null) {
            return@LaunchedEffect
        }

        val active = activeUriString?.let { runCatching { it.toUri() }.getOrNull() }
        if (active != null && canRead(context, active)) {
            openImage(active, restored = true)
            return@LaunchedEffect
        }

        val saved = prefs.getString(KEY_LAST_URI, null)
        if (saved == null) {
            activeUriString = null
            return@LaunchedEffect
        }
        val uri = runCatching { saved.toUri() }.getOrNull()
        if (uri != null && canRead(context, uri)) {
            openImage(uri, restored = true)
        } else {
            activeUriString = null
            prefs.edit { remove(KEY_LAST_URI) }
        }
    }

    val imagePixels = image?.let {
        it.sourceWidth.toLong() * it.sourceHeight.toLong()
    } ?: 0L
    val canLoadFull = image?.let {
        it.isPreview && it.frameCount == 1 && imagePixels <= fullPixelLimit
    } == true
    val canViewRegion = image?.let {
        it.isPreview && it.frameCount == 1 && imagePixels > fullPixelLimit
    } == true
    val infoFormat = remember(selectedName, selectedUri) {
        formatLabel(
            selectedName,
            selectedUri?.let { context.contentResolver.getType(it) },
        )
    }
    val infoSize = remember(selectedUri) {
        selectedUri?.let { queryFileSize(context, it) }
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .testTag("viewer_shell")
            .background(ViewerCanvas)
            .windowInsetsPadding(WindowInsets.safeDrawing)
            .padding(horizontal = 24.dp, vertical = 16.dp),
    ) {
        Column(modifier = Modifier.fillMaxSize()) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    text = stringResource(R.string.app_name),
                    style = MaterialTheme.typography.headlineMedium,
                    color = Color.White,
                )
                if (landscape) {
                    Text(
                        text = stringResource(R.string.version_label),
                        style = MaterialTheme.typography.labelSmall,
                        color = Color.White.copy(alpha = 0.4f),
                    )
                }
            }

            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f)
                    .padding(vertical = 12.dp),
                contentAlignment = Alignment.Center,
            ) {
                when {
                    loading -> CircularProgressIndicator()
                    image != null && regionMode && selectedUri != null -> RegionViewer(
                        uri = selectedUri!!,
                        preview = image!!.bitmap,
                        sourceWidth = image!!.sourceWidth,
                        sourceHeight = image!!.sourceHeight,
                        onExit = { regionMode = false },
                        modifier = Modifier.fillMaxSize(),
                    )
                    image != null -> ZoomableImage(
                        bitmap = image!!.bitmap,
                        isPreview = image!!.isPreview,
                        resetKey = imageEpoch,
                        resetSignal = viewResetSignal,
                        quarterTurns = quarterTurns,
                        startAtActual = startAtActual,
                        onRequestFull = if (canLoadFull) {
                            { requestFullImage() }
                        } else {
                            null
                        },
                        onRequestRegion = if (canViewRegion) {
                            { requestRegionView() }
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
                if (landscape && image != null && !regionMode) {
                    Column(
                        modifier = Modifier
                            .align(Alignment.BottomCenter)
                            .fillMaxWidth()
                            .background(Color.Black.copy(alpha = 0.55f))
                            .padding(8.dp),
                    ) {
                        ViewerControls(
                            landscape = true,
                            loading = loading,
                            frameCount = image!!.frameCount,
                            isPlaying = isPlaying,
                            canShare = selectedUri != null,
                            onRotate = { rotateView() },
                            onReset = { resetView() },
                            onInfo = { infoVisible = true },
                            onShare = { shareCurrentImage() },
                            onSave = { saveCurrentImage() },
                            onTogglePlay = { isPlaying = !isPlaying },
                            onOpen = { openDocument.launch(arrayOf("image/*")) },
                            onRecent = { recentVisible = true },
                        )
                    }
                }
            }

            if (!landscape && image != null && !regionMode) {
                ViewerControls(
                    landscape = false,
                    loading = loading,
                    frameCount = image!!.frameCount,
                    isPlaying = isPlaying,
                    canShare = selectedUri != null,
                    onRotate = { rotateView() },
                    onReset = { resetView() },
                    onInfo = { infoVisible = true },
                    onShare = { shareCurrentImage() },
                    onSave = { saveCurrentImage() },
                    onTogglePlay = { isPlaying = !isPlaying },
                    onOpen = { openDocument.launch(arrayOf("image/*")) },
                    onRecent = { recentVisible = true },
                    modifier = Modifier.fillMaxWidth(),
                )
                Spacer(modifier = Modifier.height(8.dp))
            }

            if (!landscape) {
                Spacer(modifier = Modifier.height(12.dp))
                Text(
                    text = stringResource(R.string.version_label),
                    style = MaterialTheme.typography.labelSmall,
                    color = Color.White.copy(alpha = 0.4f),
                )
            }
        }
    }

    if (infoVisible) {
        image?.let { current ->
            AlertDialog(
                onDismissRequest = { infoVisible = false },
                modifier = Modifier.testTag("info_dialog"),
                title = { Text(stringResource(R.string.image_info)) },
                text = {
                    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                        Text(
                            "${stringResource(R.string.image_info_name)}: " +
                                (selectedName ?: stringResource(R.string.unknown)),
                        )
                        Text(
                            "${stringResource(R.string.image_info_dimensions)}: " +
                                "${current.sourceWidth} x ${current.sourceHeight}",
                        )
                        Text(
                            "${stringResource(R.string.image_info_format)}: " +
                                (infoFormat ?: stringResource(R.string.unknown)),
                        )
                        Text(
                            "${stringResource(R.string.image_info_size)}: " +
                                (infoSize?.let { formatBytes(it) }
                                    ?: stringResource(R.string.unknown)),
                        )
                        if (current.frameCount > 1) {
                            Text(
                                "${stringResource(R.string.image_info_frames)}: " +
                                    current.frameCount,
                            )
                        }
                    }
                },
                confirmButton = {
                    TextButton(onClick = { infoVisible = false }) {
                        Text(stringResource(R.string.close))
                    }
                },
            )
        }
    }

    if (recentVisible) {
        RecentDialog(
            entries = recentEntries,
            canRead = { uri -> canRead(context, uri) },
            onOpen = { entry -> openRecent(entry) },
            onRemove = { entry -> removeRecent(entry) },
            onClear = { clearRecent() },
            onDismiss = { recentVisible = false },
        )
    }
}

private fun fullPixelBudget(context: Context): Long {
    val activityManager = context.getSystemService(ActivityManager::class.java)
    val memoryInfo = ActivityManager.MemoryInfo()
    activityManager?.getMemoryInfo(memoryInfo)
    val totalMb = memoryInfo.totalMem / (1024L * 1024L)
    val totalBudgetBytes = when {
        totalMb >= 6_000L -> 96L * 1024L * 1024L
        totalMb >= 4_000L -> 80L * 1024L * 1024L
        totalMb >= 3_000L -> 64L * 1024L * 1024L
        else -> 48L * 1024L * 1024L
    }
    val heapBudgetBytes = Runtime.getRuntime().maxMemory() / 2L
    val byteBudget = minOf(heapBudgetBytes, totalBudgetBytes, FULL_HEAP_CAP_BYTES)
    val pixels = (byteBudget / 4L)
        .coerceIn(MIN_FULL_PIXELS, MAX_FULL_PIXELS_CEILING)
    return if (activityManager?.isLowRamDevice == true) {
        pixels.coerceAtMost(LOW_RAM_FULL_PIXELS)
    } else {
        pixels
    }
}

private fun displayName(context: Context, uri: Uri): String {
    val name = runCatching {
        context.contentResolver.query(
            uri,
            arrayOf(OpenableColumns.DISPLAY_NAME),
            null,
            null,
            null,
        )?.use { cursor ->
            if (cursor.moveToFirst()) {
                val index = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                if (index >= 0) cursor.getString(index) else null
            } else {
                null
            }
        }
    }.getOrNull()
    return name ?: uri.lastPathSegment ?: uri.toString()
}

private fun canRead(context: Context, uri: Uri): Boolean {
    return runCatching {
        context.checkUriPermission(
            uri,
            Process.myPid(),
            Process.myUid(),
            Intent.FLAG_GRANT_READ_URI_PERMISSION,
        ) == PackageManager.PERMISSION_GRANTED
    }.getOrDefault(false)
}

private fun queryFileSize(context: Context, uri: Uri): Long? {
    val cursor = runCatching {
        context.contentResolver.query(
            uri,
            arrayOf(OpenableColumns.SIZE),
            null,
            null,
            null,
        )
    }.getOrNull() ?: return null
    return try {
        if (cursor.moveToFirst()) {
            val index = cursor.getColumnIndex(OpenableColumns.SIZE)
            if (index >= 0 && !cursor.isNull(index)) {
                cursor.getLong(index).takeIf { it > 0L }
            } else {
                null
            }
        } else {
            null
        }
    } finally {
        cursor.close()
    }
}

private fun formatLabel(name: String?, mimeType: String?): String? {
    val extension = name
        ?.substringAfterLast('.', "")
        ?.trim()
        ?.takeIf { it.isNotEmpty() && it.length <= 8 && it.all(Char::isLetterOrDigit) }
    if (extension != null) {
        return extension.uppercase(Locale.US)
    }
    val subtype = mimeType
        ?.substringAfter('/', "")
        ?.trim()
        ?.takeIf { it.isNotEmpty() }
    return subtype?.uppercase(Locale.US)
}

private fun formatBytes(bytes: Long): String {
    if (bytes < 1024L) {
        return "$bytes B"
    }
    val kilobytes = bytes / 1024.0
    if (kilobytes < 1024.0) {
        return String.format(Locale.US, "%.1f KB", kilobytes)
    }
    val megabytes = kilobytes / 1024.0
    if (megabytes < 1024.0) {
        return String.format(Locale.US, "%.1f MB", megabytes)
    }
    return String.format(Locale.US, "%.1f GB", megabytes / 1024.0)
}

@Composable
private fun ViewerControls(
    landscape: Boolean,
    loading: Boolean,
    frameCount: Int,
    isPlaying: Boolean,
    canShare: Boolean,
    onRotate: () -> Unit,
    onReset: () -> Unit,
    onInfo: () -> Unit,
    onShare: () -> Unit,
    onSave: () -> Unit,
    onTogglePlay: () -> Unit,
    onOpen: () -> Unit,
    onRecent: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val pauseLabel = stringResource(
        if (isPlaying) R.string.pause_animation else R.string.resume_animation,
    )
    if (landscape) {
        Row(
            modifier = modifier.horizontalScroll(rememberScrollState()),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            ControlButton(stringResource(R.string.rotate_image), !loading, "control_rotate", onRotate)
            ControlButton(stringResource(R.string.reset_view), !loading, "control_reset", onReset)
            ControlButton(stringResource(R.string.image_info), !loading, "control_info", onInfo)
            ControlButton(
                stringResource(R.string.share_image),
                !loading && canShare,
                "control_share",
                onShare,
            )
            ControlButton(
                stringResource(R.string.save_image),
                !loading && canShare,
                "control_save",
                onSave,
            )
            if (frameCount > 1) {
                ControlButton(pauseLabel, !loading, "control_play", onTogglePlay)
            }
            ControlButton(stringResource(R.string.open_image), !loading, "control_open", onOpen)
            ControlButton(stringResource(R.string.recent_files), !loading, "control_recent", onRecent)
        }
        return
    }

    Column(modifier = modifier, verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            ControlButton(stringResource(R.string.rotate_image), !loading, "control_rotate", onRotate)
            ControlButton(stringResource(R.string.reset_view), !loading, "control_reset", onReset)
            ControlButton(stringResource(R.string.image_info), !loading, "control_info", onInfo)
        }
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            ControlButton(
                stringResource(R.string.share_image),
                !loading && canShare,
                "control_share",
                onShare,
            )
            ControlButton(
                stringResource(R.string.save_image),
                !loading && canShare,
                "control_save",
                onSave,
            )
            if (frameCount > 1) {
                ControlButton(pauseLabel, !loading, "control_play", onTogglePlay)
            }
        }
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.Center,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            ControlButton(stringResource(R.string.open_image), !loading, "control_open", onOpen)
            ControlButton(stringResource(R.string.recent_files), !loading, "control_recent", onRecent)
        }
    }
}

@Composable
private fun ControlButton(
    label: String,
    enabled: Boolean,
    tag: String,
    onClick: () -> Unit,
) {
    FilledTonalButton(
        onClick = onClick,
        enabled = enabled,
        modifier = Modifier.testTag(tag),
    ) {
        Text(label)
    }
}
