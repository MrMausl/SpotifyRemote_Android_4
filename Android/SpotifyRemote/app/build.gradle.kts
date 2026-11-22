plugins {
    alias(libs.plugins.android.application)
}

android {
    namespace = "com.example.spotifyremote"
    compileSdk {
        version = release(37)
    }

    defaultConfig {
        applicationId = "com.example.spotifyremote"
        minSdk = 17
        targetSdk = 17
        versionCode = 1
        versionName = "1.0"

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    buildTypes {
        release {
            optimization {
                enable = true
                packageScope = setOf("androidx.**", "kotlin.**", "kotlinx.**")
            }
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_1_8
        targetCompatibility = JavaVersion.VERSION_1_8
    }
}

dependencies {
}