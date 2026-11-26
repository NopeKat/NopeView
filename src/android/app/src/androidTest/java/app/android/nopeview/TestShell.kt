// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

package app.android.nopeview

import android.net.Uri
import android.os.ParcelFileDescriptor
import androidx.test.platform.app.InstrumentationRegistry

/**
 * Starts activities through the shell. The device rejects
 * Instrumentation.startActivitySync, but a shell launch reaches the real
 * activity and the empty Compose rule can attach to its composition.
 */
object TestShell {
    fun startImage(action: String, uri: Uri, mimeType: String): String {
        val command = "am start -W -a $action -d $uri -t $mimeType " +
            "-f 0x41 -n app.android.nopeview/.MainActivity"
        val output = execute(command)
        checkLaunch(output)
        return output
    }

    fun startMain(): String {
        val output = execute("am start -W -n app.android.nopeview/.MainActivity")
        checkLaunch(output)
        return output
    }

    fun startActivity(component: String): String {
        val output = execute("am start -W -n $component")
        checkLaunch(output)
        return output
    }

    private fun checkLaunch(output: String) {
        check(!output.contains("Error:") && !output.contains("Exception occurred")) {
            output
        }
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
