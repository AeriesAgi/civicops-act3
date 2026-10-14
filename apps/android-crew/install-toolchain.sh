#!/usr/bin/env bash
set -euo pipefail
tool_root="${1:-/workspace/civicops/tools}"
mkdir -p "$tool_root/downloads" "$tool_root/jdk21" "$tool_root/android-sdk/cmdline-tools" "$tool_root/android-user" "$tool_root/gradle-cache"
fetch() {
  local url="$1" name="$2" checksum="$3"
  if [[ ! -f "$tool_root/downloads/$name" ]]; then
    curl -fL --retry 2 --connect-timeout 15 --max-time 300 "$url" -o "$tool_root/downloads/$name"
  fi
  printf '%s  %s\n' "$checksum" "$tool_root/downloads/$name" | sha256sum --check --status
}
fetch 'https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.12.1%2B1/OpenJDK21U-jdk_x64_linux_hotspot_21.0.12.1_1.tar.gz' 'temurin21.0.12.1.tar.gz' 'ce79869e1307ed8ee1e2baa86a412b1eb5b75d10a01006d788a6f968bcfaee94'
fetch 'https://dl.google.com/android/repository/commandlinetools-linux-15859902_latest.zip' 'android-commandlinetools-15859902.zip' '4e4c464f145a7512b57d088ac6c278c03c9eea610886b35a5e0804e74eedf583'
if [[ ! -x "$tool_root/jdk21/jdk-21.0.12.1+1/bin/java" ]]; then
  tar -xzf "$tool_root/downloads/temurin21.0.12.1.tar.gz" -C "$tool_root/jdk21"
fi
if [[ ! -x "$tool_root/android-sdk/cmdline-tools/15859902/bin/sdkmanager" ]]; then
  unzip -q "$tool_root/downloads/android-commandlinetools-15859902.zip" -d "$tool_root/android-sdk/cmdline-tools"
  mv "$tool_root/android-sdk/cmdline-tools/cmdline-tools" "$tool_root/android-sdk/cmdline-tools/15859902"
fi
export JAVA_HOME="$tool_root/jdk21/jdk-21.0.12.1+1"
export ANDROID_HOME="$tool_root/android-sdk"
# Gradle rejects conflicting Android SDK variables. Override a runner's
# preconfigured SDK root so installation and the subsequent build use one
# deterministic, pinned SDK directory.
export ANDROID_SDK_ROOT="$ANDROID_HOME"
export ANDROID_USER_HOME="$tool_root/android-user"
export GRADLE_USER_HOME="$tool_root/gradle-cache"
export PATH="$JAVA_HOME/bin:$PATH"
sdk_manager="$ANDROID_HOME/cmdline-tools/15859902/bin/sdkmanager"
# Owner round-10 instruction explicitly authorises standard SDK licence acceptance.
set +e
yes | "$sdk_manager" --sdk_root="$ANDROID_HOME" --licenses
license_status="${PIPESTATUS[1]}"
set -e
[[ "$license_status" -eq 0 ]]
"$sdk_manager" --sdk_root="$ANDROID_HOME" 'platform-tools' 'platforms;android-36' 'build-tools;36.0.0'
python3 - "$ANDROID_HOME" <<'PY'
import pathlib, sys
root = pathlib.Path(sys.argv[1])
for path, revision in [('platform-tools', '37.0.1'), ('platforms/android-36', '2'), ('build-tools/36.0.0', '36.0.0')]:
    properties = dict(line.split('=', 1) for line in (root / path / 'source.properties').read_text().splitlines() if '=' in line and not line.startswith('#'))
    if properties.get('Pkg.Revision') != revision:
        raise SystemExit(f'{path}: review SDK revision {properties.get("Pkg.Revision")} before updating the pinned toolchain (expected {revision})')
    print(f'{path}: verified revision {revision}')
PY
