package app.android.nopeview

import android.content.Context
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Before
import org.junit.Test

/**
 * Leaves a URI that the app cannot read. This stands in for a persisted grant
 * that has expired or been revoked.
 */
class RestoreLostPermissionSeedTest {
    private lateinit var context: Context

    @Before
    fun setUp() {
        context = InstrumentationRegistry.getInstrumentation().targetContext
        TestImages.reset(context)
    }

    @Test
    fun leaveUnreadableUri() {
        context.getSharedPreferences("viewer", Context.MODE_PRIVATE)
            .edit()
            .putString(
                "last_uri",
                "content://app.android.nopeview.missingprovider/unreadable.png",
            )
            .commit()
    }
}
