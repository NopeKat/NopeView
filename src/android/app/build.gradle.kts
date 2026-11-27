// Copyright (c) 2026 NopeKat. All rights reserved.
// Licensed under the GNU Affero General Public License v3.0; see LICENSE.

import java.io.File
import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

android {
    namespace = "app.android.nopeview"
    compileSdk = 36
    buildToolsVersion = "36.0.0"
    ndkVersion = "29.0.14206865"

    defaultConfig {
        applicationId = "app.android.nopeview"
        minSdk = 26
        targetSdk = 36
        versionCode = 2
        versionName = "0.0.1.0"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"

        ndk {
            abiFilters += "arm64-v8a"
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro",
            )
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
    }

    externalNativeBuild {
        cmake {
            path = file("src/main/cpp/CMakeLists.txt")
            version = "3.22.1"
        }
    }

    packaging {
        jniLibs {
            useLegacyPackaging = true
        }
    }

    lint {
        abortOnError = true
        warningsAsErrors = true
        // Versions are intentionally pinned; dependencies are updated on purpose.
        // Only arm64-v8a is shipped, so ChromeOS x86 support is out of scope.
        // Adaptive icons still need the -v26 qualifier for AAPT.
        disable += setOf("GradleDependency", "ChromeOsAbiSupport", "ObsoleteSdkInt")
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_17)
        allWarningsAsErrors.set(true)
    }
}

layout.buildDirectory.set(file("${rootDir}/../../build/android/app"))

// The APK carries its own copy of the licence files, because the package cannot
// read the repository root. A copy that falls behind the root would ship a
// stale notice, so any Gradle build, an IDE build included, fails before the
// packaging steps.
val licenceAssetNames = listOf("LICENSE", "THIRD-PARTY-NOTICES.md")
val licenceAssetDirectory = file("src/main/assets/licenses")
val licenceRepoRoot = rootDir.parentFile.parentFile

val verifyLicenceAssets by tasks.registering {
    group = "verification"
    description = "Checks that the Android licence assets match the repository root."
    inputs.files(licenceAssetNames.map { File(licenceRepoRoot, it) })
    inputs.files(licenceAssetNames.map { File(licenceAssetDirectory, it) })
    doLast {
        licenceAssetNames.forEach { name ->
            val root = File(licenceRepoRoot, name)
            val copy = File(licenceAssetDirectory, name)
            if (!root.isFile) {
                throw GradleException(
                    "The licence file is missing from the repository root: ${root.absolutePath}",
                )
            }
            if (!copy.isFile) {
                throw GradleException(
                    "The Android licence asset is missing: ${copy.absolutePath}. " +
                        "Copy it from the repository root before building.",
                )
            }
            if (!root.readBytes().contentEquals(copy.readBytes())) {
                throw GradleException(
                    "The Android licence asset is out of date: ${copy.absolutePath}. " +
                        "Copy ${root.absolutePath} over it before building.",
                )
            }
        }
    }
}

tasks.named("preBuild").configure { dependsOn(verifyLicenceAssets) }

dependencies {
    implementation(platform("androidx.compose:compose-bom:2025.12.00"))
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.activity:activity-compose:1.12.4")
    implementation("androidx.exifinterface:exifinterface:1.3.7")

    debugImplementation("androidx.compose.ui:ui-tooling")
    debugImplementation("androidx.compose.ui:ui-test-manifest")

    androidTestImplementation(platform("androidx.compose:compose-bom:2025.12.00"))
    androidTestImplementation("androidx.compose.ui:ui-test-junit4")
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
    androidTestImplementation("androidx.test:runner:1.6.2")
    androidTestImplementation("androidx.test.espresso:espresso-core:3.6.1")
}
