// O server não é instalado como app: o PC envia o .apk para /data/local/tmp e o executa com
// `app_process`, igual ao scrcpy. Por isso ele não tem tela (Activity) nem dependências.
plugins {
    alias(libs.plugins.android.application)
}

android {
    namespace = "com.seamline.server"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.seamline.server"
        minSdk = 30
        targetSdk = 36
        versionCode = 1
        versionName = "0.1.0"
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        buildConfig = true
    }
}
