import java.util.Properties
import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}

// The release key: keystore.properties next to settings.gradle.kts, kept out of git
// with the keystore folder. Without it the release build comes out unsigned.
val releaseKey = rootProject.file("keystore.properties").takeIf { it.exists() }?.let { file ->
    Properties().apply { file.inputStream().use(::load) }
}

android {
    namespace = "app.litetunnel"
    compileSdk = 36

    defaultConfig {
        applicationId = "app.litetunnel"
        minSdk = 26
        targetSdk = 36
        versionCode = 5
        versionName = "0.1.4"
        // libbox.aar is built for these two only (scripts/build-libbox-android.sh).
        ndk { abiFilters += listOf("arm64-v8a", "armeabi-v7a") }
        // Shown in Settings; reading Libbox.version() would start the Go runtime in the UI.
        buildConfigField("String", "CORE_VERSION", "\"1.14.4\"")
    }

    signingConfigs {
        if (releaseKey != null) {
            create("release") {
                storeFile = rootProject.file(releaseKey.getProperty("storeFile"))
                storePassword = releaseKey.getProperty("storePassword")
                keyAlias = releaseKey.getProperty("keyAlias")
                keyPassword = releaseKey.getProperty("keyPassword")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.findByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    // libbox.so is ~57 MB per ABI. Stored uncompressed the APK is ~120 MB; compressed it
    // is about a third of that, and the installer unpacks only the phone's own ABI.
    packaging {
        jniLibs { useLegacyPackaging = true }
    }
}

kotlin {
    compilerOptions { jvmTarget.set(JvmTarget.JVM_17) }
}

dependencies {
    implementation(project(":core"))
    implementation(files("libs/libbox.aar"))

    val composeBom = platform("androidx.compose:compose-bom:2025.10.00")
    implementation(composeBom)
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.foundation:foundation")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.activity:activity-compose:1.11.0")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.9.4")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.9.4")
    implementation("androidx.core:core-ktx:1.17.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")
}
