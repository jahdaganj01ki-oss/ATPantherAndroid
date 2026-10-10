plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("com.google.devtools.ksp")
}

android {
    namespace = "com.alditalk.panther"
    compileSdk = 34

    defaultConfig {
        // Eigene applicationId, damit die Redmi-Variante parallel zur
        // Original-App und zur X11Pro-Variante installierbar bleibt
        // (kein Update-Konflikt).
        applicationId = "com.alditalk.panther.redminote9pro"
        minSdk = 24
        targetSdk = 34
        versionCode = 1
        versionName = "1.0-redmi9pro"

        // Redmi Note 9 Pro: Snapdragon 720G ist 64-Bit-ARM – arm64-v8a +
        // armeabi-v7a reichen; schlanke APK ohne x86-Emulatoren-ABIs.
        ndk {
            abiFilters += listOf("arm64-v8a", "armeabi-v7a")
        }

        // ResConfigs: nur deutsch + englisch, spart Resource-Matching zur Laufzeit
        resConfigs("de", "en")
    }

    // Redmi Note 9 Pro hat Android 11 (API 30) – minSdk 24 bleibt kompatibel
    signingConfigs {
        create("release") {
            // Nur wirksam, wenn die CI-Secrets gesetzt sind
            // (siehe .github/workflows/redmi-note-9-pro-version-1.yml -> Decode signing keystore).
            val ksPath = System.getenv("ATP_KEYSTORE_PATH")
            if (ksPath != null) {
                storeFile = file(ksPath)
                storePassword = System.getenv("ANDROID_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("ANDROID_KEY_ALIAS")
                keyPassword = System.getenv("ANDROID_KEY_PASSWORD")
            }
        }
    }
    buildTypes {
        debug {
            applicationIdSuffix = ".debug"
        }
        release {
            isMinifyEnabled = true
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
            val ksPath = System.getenv("ATP_KEYSTORE_PATH")
            if (ksPath != null) {
                signingConfig = signingConfigs.getByName("release")
            }
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
    buildFeatures {
        viewBinding = true
    }
}

dependencies {
    // AndroidX
    implementation("androidx.core:core-ktx:1.13.1")
    implementation("androidx.appcompat:appcompat:1.7.0")
    implementation("com.google.android.material:material:1.12.0")
    implementation("androidx.constraintlayout:constraintlayout:2.1.4")
    implementation("androidx.lifecycle:lifecycle-service:2.8.3")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.3")
    implementation("androidx.recyclerview:recyclerview:1.3.2")

    // HTTP
    implementation("com.squareup.okhttp3:okhttp:4.12.0")

    // Room
    implementation("androidx.room:room-runtime:2.6.1")
    implementation("androidx.room:room-ktx:2.6.1")
    ksp("androidx.room:room-compiler:2.6.1")

    // Encrypted SharedPreferences
    implementation("androidx.security:security-crypto:1.1.0-alpha06")

    // Coroutines
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.8.1")
}
