plugins {
    id("com.android.application") version "8.11.1" apply false
    id("org.jetbrains.kotlin.android") version "2.1.20" apply false
    id("org.jetbrains.kotlin.plugin.compose") version "2.1.20" apply false
    id("com.google.devtools.ksp") version "2.1.20-1.0.32" apply false
}

// Keep APKs, generated compiler metadata and reports beside the external Gradle cache.
// Generated metadata can resemble credential strings; it should not enter the checkout.
val externalBuildRoot = gradle.gradleUserHomeDir.resolve("project-build/android-crew").canonicalFile
val repositoryRoot = rootDir.parentFile.parentFile.canonicalFile
require(!externalBuildRoot.toPath().startsWith(repositoryRoot.toPath())) {
    "GRADLE_USER_HOME must be outside the repository so generated Android outputs remain external."
}
allprojects {
    layout.buildDirectory.set(externalBuildRoot.resolve(if (this == rootProject) "root" else name))
}
