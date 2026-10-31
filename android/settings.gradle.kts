pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "Seamline"

// app: o aplicativo normal que o usuário instala (tela de status e permissões).
// server: o processo com permissão de "shell", iniciado pelo PC via adb + app_process.
include(":app", ":server")
