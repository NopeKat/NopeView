package app.android.nopeview

import android.graphics.Bitmap
import android.net.Uri
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.size
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp
import androidx.core.net.toUri
import app.android.nopeview.ui.RegionViewer

/**
 * Debug-only host for instrumentation tests. The device blocks
 * Instrumentation.startActivitySync, so the test launches this activity through
 * UiAutomation and the empty Compose rule observes its composition.
 */
object RegionTestState {
    var preview by mutableStateOf<Bitmap?>(null)
    var uri by mutableStateOf<Uri?>(null)
    var sourceWidth by mutableIntStateOf(2000)
    var sourceHeight by mutableIntStateOf(1000)
    var tileCount by mutableIntStateOf(0)
    var exited by mutableStateOf(false)
    var landscape by mutableStateOf(false)

    fun reset(
        previewBitmap: Bitmap,
        imageUri: Uri? = null,
        width: Int = 2000,
        height: Int = 1000,
    ) {
        preview = previewBitmap
        uri = imageUri
        sourceWidth = width
        sourceHeight = height
        tileCount = 0
        exited = false
        landscape = false
    }
}

class RegionTestActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            MaterialTheme {
                Box(
                    modifier = Modifier.fillMaxSize(),
                    contentAlignment = Alignment.Center,
                ) {
                    Box(
                        modifier = Modifier.size(
                            width = if (RegionTestState.landscape) 640.dp else 320.dp,
                            height = if (RegionTestState.landscape) 320.dp else 640.dp,
                        ),
                    ) {
                        val preview = RegionTestState.preview
                        if (RegionTestState.exited) {
                            Text("preview", modifier = Modifier.testTag("preview"))
                        } else if (preview != null) {
                            RegionViewer(
                                uri = RegionTestState.uri
                                    ?: "content://app.android.nopeview.test/missing".toUri(),
                                preview = preview,
                                sourceWidth = RegionTestState.sourceWidth,
                                sourceHeight = RegionTestState.sourceHeight,
                                onExit = { RegionTestState.exited = true },
                                modifier = Modifier.fillMaxSize(),
                                onTilesChanged = { RegionTestState.tileCount = it },
                            )
                        }
                    }
                }
            }
        }
    }
}
