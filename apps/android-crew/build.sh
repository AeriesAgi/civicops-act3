#!/usr/bin/env bash
set -euo pipefail
tool_root="${1:-/workspace/civicops/tools}"
app_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
export JAVA_HOME="$tool_root/jdk21/jdk-21.0.12.1+1"
export ANDROID_HOME="$tool_root/android-sdk"
# GitHub-hosted runners export ANDROID_SDK_ROOT to the preinstalled SDK. Keep
# both Android variables aligned with the pinned toolchain or Gradle aborts
# before evaluating the project.
export ANDROID_SDK_ROOT="$ANDROID_HOME"
export ANDROID_USER_HOME="$tool_root/android-user"
export GRADLE_USER_HOME="$tool_root/gradle-cache"
export PATH="$JAVA_HOME/bin:$PATH"
[[ -x "$JAVA_HOME/bin/java" && -d "$ANDROID_HOME/platforms/android-36" ]] || { printf '%s\n' 'Run install-toolchain.sh with the same external tool directory first.' >&2; exit 1; }
cd "$app_root"
./gradlew --no-daemon -Dorg.gradle.jvmargs=-Xmx2g -Dorg.gradle.workers.max=2 assembleDebug lint testDebugUnitTest
printf '%s\n' "Debug APK: $GRADLE_USER_HOME/project-build/android-crew/app/outputs/apk/debug/app-debug.apk"
