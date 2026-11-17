package app.android.nopeview.ui

import android.content.Context
import android.net.Uri
import android.os.Handler
import android.os.Looper
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
import java.util.concurrent.Executors

private val ViewerCanvas = Color(0xFF0B0C0E)

@Composable
fun ViewerShell() {
    val context = LocalContext.current
    var selectedName by remember { mutableStateOf<String?>(null) }
    var image by remember { mutableStateOf<DecodedImage?>(null) }
    var loading by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    val decodeFailed = stringResource(R.string.decode_failed)

    val codec = remember(context) { CodecSession(context) }
    val decoder = remember { Executors.newSingleThreadExecutor() }
    val currentImage by rememberUpdatedState(image)
    DisposableEffect(codec, decoder) {
        onDispose {
            decoder.shutdownNow()
            currentImage?.bitmap?.recycle()
            codec.close()
        }
    }

    val openDocument = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.OpenDocument(),
    ) { uri: Uri? ->
        if (uri != null) {
            selectedName = displayName(context, uri)
            loading = true
            error = null
            decoder.execute {
                val decoded = runCatching {
                    context.contentResolver.openFileDescriptor(uri, "r")?.use { descriptor ->
                        codec.decode(descriptor, PREVIEW_MAX_DIMENSION)
                    }
                }.getOrNull()
                Handler(Looper.getMainLooper()).post {
                    loading = false
                    image = decoded
                    error = if (decoded == null) {
                        decodeFailed
                    } else {
                        null
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
