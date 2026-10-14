# Android crew build and acceptance

The native source is under [apps/android-crew](../apps/android-crew/README.md). The portable crew features, API contract and POPIA notice are in [CREW_APP.md](CREW_APP.md). Native screen/device behavior is separate from the phone-size PWA screenshots; a successful build does not claim a field-device test, live municipal SSO or operational deployment.

## Pinned toolchain

Tool downloads, SDK/JDK files, Gradle caches and the debug signing key live outside the repository, under `/workspace/civicops/tools` in this verification environment. No sudo, emulator, Docker daemon or live provider API is used.

| Component | Pinned/observed version | Official source and integrity |
|---|---|---|
| Eclipse Temurin JDK | 21.0.12.1+1, Linux x64 | [Adoptium archive](https://github.com/adoptium/temurin21-binaries/releases/download/jdk-21.0.12.1%2B1/OpenJDK21U-jdk_x64_linux_hotspot_21.0.12.1_1.tar.gz); SHA-256 `ce79869e1307ed8ee1e2baa86a412b1eb5b75d10a01006d788a6f968bcfaee94` |
| Android command-line tools | Archive 15859902, revision 22.0 | [Google archive](https://dl.google.com/android/repository/commandlinetools-linux-15859902_latest.zip); SHA-256 `4e4c464f145a7512b57d088ac6c278c03c9eea610886b35a5e0804e74eedf583` from the [official download table](https://developer.android.com/studio) |
| Android SDK platform | Android 16, API 36, revision 2 | Installed from Google by `sdkmanager platforms;android-36`; [stable platform notes](https://developer.android.com/tools/releases/platforms) |
| SDK build tools | 36.0.0 | Installed from Google by `sdkmanager build-tools;36.0.0` |
| SDK platform tools | 37.0.1 | Installed from Google by `sdkmanager platform-tools`; installer rejects an unreviewed revision change |
| Gradle wrapper | 8.13 | [Official distribution](https://services.gradle.org/distributions/gradle-8.13-bin.zip); SHA-256 `20f1b1176237254a6fc204d8434196fa11a4cfb387567519c61556e8710aed78`, enforced in wrapper properties |
| Android Gradle Plugin | 8.11.1 | [Official compatibility notes](https://developer.android.com/build/releases/agp-8-11-0-release-notes): API 36, Gradle 8.13 and JDK 17+ |
| Kotlin + Compose compiler plugin | 2.1.20 | Pinned together in the build; [Compose compiler guidance](https://developer.android.com/develop/ui/compose/compiler) |
| Kotlin Symbol Processing | 2.1.20-1.0.32 | Google KSP via Maven Central; Room uses KSP instead of kapt |

The standard Android SDK package licences were accepted non-interactively with `yes | sdkmanager --licenses`, as explicitly authorised by the round-10 instruction. The local licence transcript ends “All SDK package licenses accepted.” `sdkmanager` revision 22 prints its official deprecation notice in favour of `android sdk`; it installed the requested packages successfully. AGP 8.11.1 prints an SDK metadata version-3/version-4 compatibility warning with the newer SDK manager; the build result and lint result are reported separately below.

An initial Adoptium version-query URL for 21.0.8+9 returned HTTP 404 before download. The official latest-assets API then supplied the pinned 21.0.12.1+1 archive and checksum. No unofficial mirror or unverified download was used. The first two native build attempts exposed resource/source changes during compilation and were rerun against a stable source snapshot; they are not counted as passes. The first lint run reported 15 errors because warnings are treated as errors. The icon, explicit debug-domain attributes, quantity wording, URI helpers and Room KSP migration resolved them. Narrow documented suppressions retain checked synchronous encrypted-token commits, intentionally pinned build versions and partial af/zu fallback; security and accessibility lint checks remain enabled.

## Build commands

The installer verifies both downloaded archive hashes before extraction and checks the observed platform/package revisions. It refuses to silently accept a later package revision. The wrapper verifies the Gradle distribution checksum.

```bash
./apps/android-crew/install-toolchain.sh /workspace/civicops/tools
./apps/android-crew/build.sh /workspace/civicops/tools
```

Equivalent explicit command, from `apps/android-crew`:

```bash
export JAVA_HOME=/workspace/civicops/tools/jdk21/jdk-21.0.12.1+1
export ANDROID_HOME=/workspace/civicops/tools/android-sdk
export ANDROID_USER_HOME=/workspace/civicops/tools/android-user
export GRADLE_USER_HOME=/workspace/civicops/tools/gradle-cache
export PATH="$JAVA_HOME/bin:$PATH"
./gradlew --no-daemon -Dorg.gradle.jvmargs=-Xmx2g -Dorg.gradle.workers.max=2 assembleDebug lint testDebugUnitTest
```

Gradle is limited to two workers and a 2 GiB JVM heap. The POSIX wrapper explicitly redirects Gradle’s project cache and Kotlin’s persistent project cache into `GRADLE_USER_HOME/project-cache/android-crew`, in addition to the external download/user cache. All project build directories are set to `GRADLE_USER_HOME/project-build/android-crew` (`app` for Android outputs, `root` for root-project outputs); configuration rejects a Gradle user home inside the checkout. With the commands above, the exact APK path is `/workspace/civicops/tools/gradle-cache/project-build/android-crew/app/outputs/apk/debug/app-debug.apk`. Unit-test XML is under the same `app/test-results/testDebugUnitTest`, and lint reports are under `app/reports`; CI uploads these external evidence paths. The build prints its exact APK location. Room schema contracts remain deliberately committed under `app/schemas` and are covered by the privacy guards.

Earlier ignored `.gradle`/`.kotlin` caches and the initial `app/build` tree were moved intact outside the repository before final verification; no build/cache tree remains in the checkout. The earlier APK, reports and compiler output were preserved at `/workspace/civicops/tmp/round10-android-prior-build-evidence/app-build`. Its generated kapt Java stub contained Kotlin metadata that a directory secret scan mistook for an API key; no secret was involved. Moving generated output outside the checkout resolves that finding without a generated-file scanner exclusion. Do not commit or distribute a debug APK as a production release. A release signing key is owner-controlled and must remain outside the repository.

## Dependencies and licences

Runtime libraries are obtained from official Google Maven/Maven Central repositories and pinned in [app/build.gradle.kts](../apps/android-crew/app/build.gradle.kts); resolved dependencies are recorded in the Gradle lockfile. Gradle plugin resolution additionally uses the official Gradle Plugin Portal. No runtime CDN, map-tile SDK, face/biometric library or analytics SDK is included.

| Library | Pin | Licence/source |
|---|---|---|
| Compose Material 3/UI | BOM 2025.05.01 | Apache-2.0; [official BOM notes](https://developer.android.com/develop/ui/compose/bom) |
| Activity Compose | 1.10.1 | Apache-2.0; Google Maven |
| Lifecycle | 2.9.0 | Apache-2.0; Google Maven |
| Room runtime/compiler/ktx | 2.7.1 | Apache-2.0; [Room release notes](https://developer.android.com/jetpack/androidx/releases/room) |
| WorkManager | 2.10.1 | Apache-2.0; [WorkManager release notes](https://developer.android.com/jetpack/androidx/releases/work) |
| ExifInterface | 1.4.1 | Apache-2.0; [ExifInterface release notes](https://developer.android.com/jetpack/androidx/releases/exifinterface) |
| AppAuth Android | 0.11.1 | Apache-2.0; [OpenID project](https://github.com/openid/AppAuth-Android), system-browser OAuth code flow and S256 PKCE |
| Kotlin coroutines Android/test | 1.10.2 | Apache-2.0; Maven Central |
| JUnit, test only | 4.13.2 | EPL-1.0; Maven Central; not bundled into the app |

## Native privacy and deployment limits

The [location state machine](../apps/android-crew/app/src/main/java/za/civicops/crew/CrewModels.kt) denies sensors unless an active server-verified shift, valid consent receipt, unexpired scoped token, explicit Share action and visible foreground notification are all present. The [foreground service](../apps/android-crew/app/src/main/java/za/civicops/crew/ShiftLocationService.kt) checks permissions/notification visibility and deadlines continuously, uses a monotonic deadline in addition to server expiry, rejects stale pre-service provider fixes and never requests background permission. Revocation/end removes queued location drafts; a new consent/shift cannot reuse old receipt IDs. An HTTP 403 on location ingestion stops sharing and discards locations. An expired/revoked crew session clears private cached jobs and unsent drafts instead of exporting or retrying them under another identity.

The token/challenge/session state uses [Android Keystore AES-GCM storage](../apps/android-crew/app/src/main/java/za/civicops/crew/CredentialVault.kt). Room data is app-private, not separately SQLCipher-encrypted; production requires Android device encryption, MDM access policy and remote-wipe acceptance. Cloud/device-transfer backup is disabled. The outbox is capped at 100 items / 20 MiB, with seven-day local draft expiry on startup/sync. Force-stopped/offline devices cannot guarantee wall-clock deletion until the app or worker runs; MDM lost-device response remains owner work. Sign-out discards unsent drafts and clears captured temporary photos.

[PhotoPrivacy](../apps/android-crew/app/src/main/java/za/civicops/crew/PhotoPrivacy.kt) re-encodes pixels with correct rotation and strips EXIF by default. The optional per-photo choice is available only during a verified active shift with a valid consent receipt and unexpired token. The gate is rechecked after camera capture and before queueing; without it the photo is stripped or the opt-in draft is rejected. The choice resets after each capture. GPS tags may enter the prepared on-device upload only under those gates; the server always strips metadata again, and the UI says so. System-camera originals outside CivicOps storage are governed by the camera application/device policy. There is no continuous camera capture, video/frame store or recognition code.

English screen resources are complete; af/zu resources reuse existing notice language and add a small machine-drafted navigation subset, falling back to English. Native-speaker municipal notice review, TalkBack on physical devices, font scaling, rotation, camera behavior, notification revocation, OEM battery policy and offline reconnect still require owner device QA. The offline coordinate schematic is explicitly labelled and makes no route or ETA claim. No live identity-provider exchange, notification, call, SMS or email was executed for this round.

For municipal MDM, deploy an owner-signed release through the approved managed app channel, configure the municipality HTTPS endpoint through the documented settings, register the public native OIDC client with `za.civicops.crew:/oauth2redirect` and the server’s separate native audience, require device encryption/screen lock, grant location/notification permissions only after worker consent, restrict backup/sharing and rehearse revocation/wipe. These are deployment instructions, not tested MDM integration. The [PWA](../layers/crew-pwa/README.md) is shipped independently for non-Android devices.

## Verification

On 9 October 2026, the installer command passed both archive checksums and all three SDK package revision guards. After relocating generated outputs, `./apps/android-crew/build.sh /workspace/civicops/tools` completed `assembleDebug lint testDebugUnitTest` in 2 minutes 14 seconds, with all 55 Gradle tasks executed: **51 tests passed, 0 failed/errors/skipped; lint reported “No issues found.”** A final configuration-guard rerun passed in 31 seconds (one task executed, 54 up-to-date), retaining those results. The suite contains 20 consent/shift/photo gates, 14 outbox/configuration cases, seven ViewModel flows and ten native privacy/schema guards. The transcripts are `/workspace/civicops/tmp/round10-android-external-output.log` and `/workspace/civicops/tmp/round10-android-external-output-final.log`; test counts were read from the four external JUnit XML reports. The 12,177,432-byte debug APK was produced at the external path above. The Gradle dependency lockfile was generated with `--write-locks`, then verified by the normal build command. The earlier universal-guard transcript remains preserved at `/workspace/civicops/tmp/round10-android-universal-guard.log`.

`aapt2 dump badging` independently verified the built package’s min SDK 26 / target SDK 36 and absence of `ACCESS_BACKGROUND_LOCATION`. The merged APK includes WorkManager’s normal `RECEIVE_BOOT_COMPLETED` rescheduling receiver and wake-lock permission for the job outbox; neither can start the location service. Dedicated guards keep sensor code and foreground-service starts out of the worker and keep location `START_NOT_STICKY`. Restoring a process resets sharing/server verification to off and drops queued location points. This differs from a location-tracking boot receiver, which is absent by design.

`gitleaks dir . --redact` passed after external-output relocation, scanning 8.18 MB with no leaks; its transcript is `/workspace/civicops/tmp/round10-android-external-output-gitleaks.log`. The checkout contains no Android build directory or Gradle/Kotlin cache, and the CI workflow YAML and `build.sh` syntax checks passed. Production source, unit-test semantics and permission declarations were unchanged by output relocation.

The [native contract guard](../apps/android-crew/app/src/test/java/za/civicops/crew/NativePrivacyGuardTest.kt) discovers every release Kotlin/Java source file and every committed Room schema version without a DTO type whitelist. It checks source identifiers and serialized keys, including escaped aliases, and all Room `fieldPath`/`columnName` values for prohibited biometric/identification and person-risk names. Two injection checks prove newly introduced DTO fields, nested schema aliases and encoded names are caught while legitimate `Interface`, `Surface` and incident-risk names remain allowed. These release guards complement the runtime consent state machine; they do not infer the meaning of arbitrary evidence content or establish municipal privacy acceptance.

The SDK metadata warning described above and the debug packaging message for the prebuilt Compose `libandroidx.graphics.path.so` remain build-tool notices; the library is packaged without stripping and no custom native library is supplied. Native runtime, physical-device/emulator behavior and live municipal SSO remain owner-pending. PWA browser captures and server integration checks are recorded separately in [CREW_APP.md](CREW_APP.md) and [VERIFICATION.md](VERIFICATION.md).
