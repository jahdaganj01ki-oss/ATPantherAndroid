// Top-level build file
// AGP 8.7 ist die erste Version, die compileSdk/targetSdk 35 (Android 15)
// sauber unterstuetzt – mit 8.5 bricht der Build ab.
plugins {
    id("com.android.application") version "8.7.3" apply false
    id("org.jetbrains.kotlin.android") version "2.0.0" apply false
    id("com.google.devtools.ksp") version "2.0.0-1.0.21" apply false
}
