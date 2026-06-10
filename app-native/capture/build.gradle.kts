plugins {
    id("com.android.library")
    id("org.jetbrains.kotlin.android")
}

// egogrip-capture: the reusable on-device capture core, built as an AAR that the Unity app
// drops into Assets/Plugins/Android/. Framework-only (Camera2) — no external dependencies — so
// it imports into Unity cleanly. The standalone app-native app depends on this same module.
android {
    namespace = "org.egogrip.capture.lib"
    compileSdk = 34
    defaultConfig {
        minSdk = 29
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
}
