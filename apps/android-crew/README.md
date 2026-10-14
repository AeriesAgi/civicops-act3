# CivicOps Android crew client

Kotlin, Jetpack Compose and Material 3; Android 8+ (`minSdk 26`), Android 16 target (`targetSdk 36`). No municipality URL, identity issuer, client secret or crew token is built into the application. Configure the municipality server in Settings/sign-in, then use municipal OIDC in the system browser with PKCE or a supervisor-issued, short-lived pairing code.

Assigned jobs, hazards, PPE, certifications, steps, status reports, notes and photo capture use the scoped [crew API](../../docs/CREW_APP.md). Room caches assigned work and retains an idempotent WorkManager outbox for offline edits. A conflict stays visible until the worker loads the latest job, reviews it and explicitly retries or discards the draft. Reporting crew completion leaves dispatcher review/resolution outstanding.

Location is opt-in, on-shift only, and uses a visible foreground service; the application requests no background location permission. A verified server shift and consent receipt are required before enabling sensors. Ending a shift, withdrawing consent, token expiry/revocation, removing notification/location permission or reaching the shift deadline stops sharing. WorkManager can reschedule job-outbox work after a reboot; it cannot restart location sharing, and restored sessions reset sharing to off. Starting a verified shift or granting consent needs a connection; cached jobs and permitted edits remain available offline during a valid session.

SOS records a deliberately confirmed, simulated municipal control-room alert through `/api/v1/sos`. The app requests location only after the alert and only for the server-bounded follow-up window. **No responders are notified and no dispatch or emergency call is performed.** Use 10111, 10177 or 112 for a life-threatening emergency.

Build with the pinned, external toolchain:

```bash
./apps/android-crew/install-toolchain.sh /workspace/civicops/tools
./apps/android-crew/build.sh /workspace/civicops/tools
```

Generated outputs stay outside the checkout under `GRADLE_USER_HOME/project-build/android-crew`. With the command above, the debug APK is `/workspace/civicops/tools/gradle-cache/project-build/android-crew/app/outputs/apk/debug/app-debug.apk`; it must not be committed or distributed as a municipal release. The build prints its exact APK path. The [build and deployment evidence](../../docs/ANDROID_BUILD.md) records checksums, licences, dependencies, verification and owner device/MDM acceptance. The independently shipped [crew PWA](../../layers/crew-pwa/README.md) is the browser fallback.
