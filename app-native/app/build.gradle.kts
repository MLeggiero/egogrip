plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "org.egogrip.capture"
    compileSdk = 34

    defaultConfig {
        // NOTE: this applicationId is also your future PICO enterprise "authorized package
        // name" — lock it now so the camera entitlement you request later matches.
        applicationId = "org.egogrip.capture"
        minSdk = 29          // PICO OS is Android 10+; 29 is safe
        targetSdk = 34
        versionCode = 1
        versionName = "0.1.0"
    }

    buildTypes {
        release { isMinifyEnabled = false }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
}

dependencies {
    // Shared capture core (CaptureClock, Camera2Client, EgogripCamera) — also built as the AAR.
    implementation(project(":capture"))

    // The ONLY hard dependency: USB-serial (CDC/FTDI) for the RP2040. Rock solid.
    implementation("com.github.mik3y:usb-serial-for-android:3.8.1")

    // --- OPTIONAL camera (UVC) ---  Leave this commented for tomorrow's first build so a
    // flaky camera lib can never block the serial path. To enable: uncomment, copy
    // ../optional/UvcClient.kt into app/src/main/java/org/egogrip/capture/, and follow
    // README "Enable the camera".
    // implementation("com.herohan:UVCAndroid:1.0.4")
}
