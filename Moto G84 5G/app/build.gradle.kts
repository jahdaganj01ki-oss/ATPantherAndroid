plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("com.google.devtools.ksp")
}

android {
    namespace = "com.alditalk.panther"
    compileSdk = 34

    defaultConfig {
        // Eigene applicationId, damit die Moto-G84-Variante parallel zur
        // Original-App und zu den anderen Geraetevarianten installierbar bleibt
        // (kein Update-Konflikt).
        applicationId = "com.alditalk.panther.motog845g"
        minSdk = 24
        targetSdk = 34
        versionCode = 1
        versionName = "1.0-motog845g"

        // Moto G84 5G: Snapdragon 695 (64-Bit ARM) – arm64-v8a + armeabi-v7a
        // reichen; schlanke APK ohne x86-Emulator-ABIs.
        // Gegenueber dem X11Pro (Helio G25) profitiert die App hier vom
        // 12-GB-RAM und der deutlich schnelleren CPU – gleiche Filter,
        // aber noch zuegigere Performance.
        ndk {
            abiFilters += listOf("arm64-v8a", "armeabi-v7a")
        }

        // ResConfigs: nur deutsch + englisch, spart Resource-Matching zur Laufzeit
        resConfigs("de", "en")
    }

    // Moto G84 5G: Android 13 (API 33) ab Werk, updatebar auf Android 14/15 –
    // targetSdk 34 deckt alle ab. Kein Downgrade fuer aeltere Geraete noetig.
    signingConfigs {
        create("release") {
            // Nur wirksam, wenn die CI-Secrets gesetzt sind
            // (siehe .github/workflows/motog84.yml -> Decode signing keystore).
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
    implementation("androidx.viewpager2:viewpager2:1.0.0")

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
