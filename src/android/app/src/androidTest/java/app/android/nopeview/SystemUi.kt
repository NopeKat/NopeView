package app.android.nopeview

import android.os.ParcelFileDescriptor
import android.view.WindowManager
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.runner.lifecycle.ActivityLifecycleMonitorRegistry
import androidx.test.runner.lifecycle.Stage

/**
 * Reads the current resumed activity from the shell. System share sheets and
 * document pickers are outside the app process, so Compose cannot observe them.
 */
object SystemUi {
    fun waitForResumed(markers: List<String>, timeoutMs: Long = 12_000): String {
        val deadline = System.currentTimeMillis() + timeoutMs
        var last = ""
        while (System.currentTimeMillis() < deadline) {
            last = resumedActivities()
            if (markers.any { marker -> last.contains(marker, ignoreCase = true) }) {
                return last
            }
            Thread.sleep(250)
        }
        error("No resumed activity matched $markers; last=$last")
    }

    fun back() {
        execute("input keyevent KEYCODE_BACK")
    }

    fun wake() {
        execute("input keyevent KEYCODE_WAKEUP")
        execute("wm dismiss-keyguard")
    }

    fun keepOn() {
        InstrumentationRegistry.getInstrumentation().runOnMainSync {
            ActivityLifecycleMonitorRegistry.getInstance()
                .getActivitiesInStage(Stage.RESUMED)
                .forEach { activity ->
                    activity.window.addFlags(
                        WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON,
                    )
                }
        }
    }

    private fun resumedActivities(): String {
        return execute(
            "dumpsys activity activities | grep -E " +
                "'mResumedActivity|ResumedActivity|topResumedActivity'",
        )
    }

    private fun execute(command: String): String {
        val descriptor = InstrumentationRegistry.getInstrumentation()
            .uiAutomation
            .executeShellCommand(command)
        return ParcelFileDescriptor.AutoCloseInputStream(descriptor).use { input ->
            input.bufferedReader().readText()
        }
    }
}
