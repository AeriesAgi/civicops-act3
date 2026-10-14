# CivicOps crew applications

Round 12 provides an Android crew client and an installable web fallback at `/crew/`. Both use the municipal server's assigned jobs, explicit shifts and revocable location consent. **SOS records a simulated municipal control-room alert after deliberate confirmation; it does not contact emergency services, dispatch a crew, send a message or make a call.** Location is requested only after SOS and only during its bounded follow-up window. Municipal emergency procedures remain essential. The synthetic demo does not establish production readiness.

The current verification results and any native toolchain limitations are recorded in [VERIFICATION.md](VERIFICATION.md). [Android build evidence](ANDROID_BUILD.md) records the exact pinned toolchain, licence acceptance, build commands and APK location. APKs, local SDKs, Gradle caches and deployment credentials are never committed.

## Start and install

```bash
CIVICOPS_DEMO_MODE=true dotnet run --project products/civicops --urls http://localhost:5080
```

Open `http://localhost:5080/crew/` on the same machine. The default path is explicitly labelled synthetic. A production PWA installation needs a trusted HTTPS origin; loopback is permitted for local verification. A phone's `localhost` is the phone itself: use an approved municipal HTTPS endpoint, or an explicitly configured Android development connection. Neither app embeds a deployment URL or credential.

The PWA manifest and service worker cache the application shell. API responses remain network-only. IndexedDB holds the signed-in worker's bounded job cache and pending job edits, including sanitized photos. Jobs expire after 24 hours and unsent drafts after seven days; the PWA purges expired local records when the app next opens. Sign-out and authorization loss clear private local state. An offline job update remains visibly pending until the server accepts it. An expired/revoked credential requires sign-in; a conflicting version requires review. Retrying the same edit uses its original operation ID rather than duplicating it. Location pings are never stored in the PWA outbox.

The PWA uses the existing staff browser session for OIDC, and the device-pairing path for crew credentials. Its paired token is kept in tab-scoped `sessionStorage` until expiry, never in IndexedDB or service-worker caches. Closing the tab requires another sign-in. Reload may show cached jobs offline, but server identity verification is required before syncing; it never restores active location collection. Android uses AppAuth authorization code + PKCE through the system browser, or device pairing, with Keystore-backed encrypted credential storage. Real municipal IdP registration, mobile redirect approval, MFA, device management and live login/logout acceptance remain owner-pending. A supervisor chooses a worker and a reason when issuing a short-lived pairing code; the code is exchanged once for an expiring crew token and the grant/revocation is audited. Pairing does not authorize dispatch or supervision.

## Jobs and safety

The job list and filter show assigned work. Detail includes the volunteered case location when present, hazard class and descriptions, PPE, required certification codes and catalogue procedure steps. A location outline is context, not route guidance or proof of safe access. Workers choose en route, on site, paused, complete or cannot complete; pauses and incomplete work require a reason. Completion is a worker report: the case remains in progress until a dispatcher reviews the evidence and uses the existing resolve/communication-review workflow. Notes and photos attach to the job with the named authenticated actor and expected version. A worker cannot assign a crew or approve an unreviewed incident.

Photo collection has a visible purpose notice. Clients strip location metadata on device by default; the server sanitizes uploads again. Android exposes a per-photo location choice, but this server release always strips metadata before storage and reports that fact in its receipt, even when the client opts in. Browser re-encoding also discards metadata, including GPS. No photo-based location bypass is available. CivicOps performs no recognition on job evidence photos. Camera/video detector frames remain outside this feature and are never stored by CivicOps.

English is the primary crew interface. Afrikaans and isiZulu resources are starter translations; native-speaker, municipal-notice and manual TalkBack/screen-reader review remain acceptance tasks. Controls target at least 48 dp/pixels and support enlarged text. Automated browser accessibility evidence does not replace testing with workers and assistive technology.

## Location privacy model

Location sharing requires all of the following:

1. An authenticated worker identity and current tenant membership.
2. A server-issued active shift that has not ended or exceeded its maximum length.
3. A server-issued, unwithdrawn consent record for the current notice.
4. An explicit client action to share, OS/browser permission, and a visible sharing indicator.

Android uses a location foreground service with the persistent notification **“CivicOps is sharing your location while on shift”**. It requests foreground location permission; it does not request background location permission or start tracking at boot. The PWA collects only while its page is open and visible. It stops when hidden, offline, signed out, permission is revoked, consent is withdrawn, the shift ends or its expiry is reached. Restarting the client never silently starts location collection.

The server independently rejects a location batch with HTTP 403 and an audit event when the shift or consent is invalid. Each batch identifies its shift and consent and uses a stable operation ID. Ending a shift or withdrawing consent removes the visible live position. Offline withdrawal stops local collection immediately; the server receives that privacy change when connectivity returns. No client may treat elapsed offline time as renewed consent. A supervisor can revoke the device token, and an expired device cannot send further pings.

Tenant policy defaults are **60-second sampling, 100-metre accepted accuracy, 30-day raw retention and a 12-hour maximum shift**. Bounds are 15–600 seconds, 5–1,000 metres, 1–30 retention days and 1–12 shift hours; supervisors provide a reason for policy changes. A shift also ends no later than its device credential expiry, normally eight hours or the earlier native OIDC expiry. The server enforces the sampling interval with one second of timer tolerance. Batches contain 1–100 capture-ordered points from one UTC day, within the South African geographic envelope and the current shift/consent time window. Clients split batches at UTC midnight. Raw pings are tenant/worker scoped. A worker can inspect and export their own retained history. Supervisors see current sharing positions and last-seen age; ordinary crew credentials cannot retrieve other workers' positions or histories. The map is operational context, never a person-risk or neighbourhood-crime score.

Automatic purge removes expired raw points and retains only tenant-day sample counts without worker association. Job-photo retention is also bounded. Local purge does not erase retained backups, old storage media, previous exports or provider-held records. Municipal storage encryption, backup lifecycle, access review and the complete approved retention schedule remain owner responsibilities. [Responsible AI enforcement](RESPONSIBLE_AI.md) and [POPIA controls](privacy/POPIA.md) describe the shared boundaries.

## Server contract

All paths below are relative to `/api/v1/crew`. The [OpenAPI document](api/openapi.json) publishes the exact closed request/response contracts, errors and security schemes. Writes carry a stable `operationId`; job writes also require `expectedVersion`. Unknown fields, duplicate keys, invalid geography, unsupported image formats and out-of-bound policies fail closed. Device credentials have `crew:self` authority plus `sos-trigger` for their own alert. They cannot dispatch, read the control-room queue, or use supervisory routes.

| Routes | Access and behavior |
|---|---|
| `GET /me`, `GET /me/jobs`, `GET /me/jobs/{id}` | Current worker/crew binding; assigned work only |
| `POST /me/jobs/{id}/status`, `/notes`, `/photos` | Assigned worker, human choice, expected version and idempotency; notes/photos remain work evidence |
| `GET /me/jobs/{id}/photos/{photoId}` | Assigned worker, retained sanitized photo and audited access |
| `GET /jobs/{id}`, `GET /jobs/{id}/photos/{photoId}` | Tenant-local supervisor evidence review, audited; no raw location-history access |
| `POST /shifts/start`, `POST /shifts/end` | Explicit server shift; end targets its device-owned shift ID |
| `POST /consent`, `DELETE /consent` | Explicit current-notice acceptance or immediate revocation |
| `POST /locations` | Current device-owned shift, consent ID, visible indicator and bounded idempotent point batch |
| `GET /me/locations`, `GET /me/locations/export` | Own retained history only; browsing is paged, export contains all retained points |
| `POST /pairing`, `GET /devices`, `POST /devices/{id}/revoke` | Supervisor creates a ten-minute one-use code or audits/revokes an expiring device grant |
| `POST /pairing/exchange` | Rate-limited code exchange; municipality ID is checked before binding the grant |
| `GET /policy`, `PUT /policy`, `POST /retention/purge` | Supervisor bounded policy with reason; manual purge supplements automatic hourly purge |
| `GET /auth/config`, `POST /auth/challenge`, `POST /auth/oidc` | Configured public native client; one-use five-minute nonce challenge, validated issuer/signature, exactly one configured native audience, matching optional `azp`, expiry and trusted workforce claims |
| `POST /sos` | Records the shared control-room alert. No coordinates are accepted on this route and no emergency call or dispatch is made |

Pairing codes contain 40 random bits and expire after ten minutes. Issued tokens contain a separate 256-bit secret, are stored as hashes on the server, expire after eight hours (or the OIDC token expiry if earlier), and can be revoked by a supervisor. The native public-client registration is configured with **`CIVICOPS_CREW_OIDC_CLIENT_ID`**; blank disables native OIDC while retaining pairing. Existing web OIDC authority/role/tenant settings still apply. The issuer must provide trusted `crew_id` and `worker_id` claims for crew-app access, matching workforce membership. An explicit municipality-path mismatch fails before any device grant is persisted. Never supply a native client secret to the app.

File storage preserves the existing single-process, separate-file atomic-write limitation; a crash between history/photo-byte and audit/metadata writes may require reconciliation. PostgreSQL crew writes join the shared durable transaction. Neither mode is a substitute for independently operated backups, storage encryption or production capacity testing. [Storage boundaries](operations/STORAGE.md) remain applicable.

## POPIA collection notice and municipal approval

The consent screen must show the server's current notice before accepting consent. Its purpose is to share a worker's location during an explicitly started shift for field coordination and worker safety. Authorized municipal supervisors can see the latest position; the worker can inspect/export their own retained raw history. Sharing is optional and can be withdrawn in the app, and ending a shift stops collection. The screen states the tenant retention policy and directs workers to their municipal Information Officer. Municipal contact details and the final notice remain deployment acceptance work. No app location is used for biometric identification, person-risk scoring, predictive policing or continuous tracking.

This is an engineering notice template. Before production, the municipality must identify the responsible party, Information Officer, recipients/operators, lawful basis, full retention and backup schedule, rights/request process and complaint channel, and review voluntariness in the employment context. The Information Regulator's [collection-notice and retention explanation](https://inforegulator.org.za/privacy-notice/) and [conditions for lawful processing](https://inforegulator.org.za/knowledge-base/category/popia/chapter-3-conditions-for-lawful-processing/) were checked on 9 October 2026. Repository consent controls do not establish POPIA compliance or a lawful employment-monitoring programme.

The current server notice is version `civicops-crew-location-v1`:

> Your municipality uses your location only to coordinate your assigned field work and worker safety while you have an active shift and explicitly choose to share. Only authorised municipal supervisors see your latest sharing position; you can see and export your own history. Raw location records are automatically deleted after the municipality's retention window (30 days by default, at most 30 days). Anonymous municipality-day sample counts may remain. Sharing has a visible indicator and stops when you end your shift, withdraw consent, or reach the maximum shift length (12 hours by default, at most 12 hours). You can withdraw at any time. No off-shift monitoring, biometric identification, person scoring or automatic police/dispatch decision is permitted. Ask your municipal Information Officer about the lawful basis, rights and access review before live deployment. CivicOps is not an emergency service.

## Municipal deployment and MDM

Use a private HTTPS endpoint, scoped issuer claims and municipality-specific worker/crew records. Replace synthetic workforce and qualifications before field use. Approve and sign the APK outside the repository; distribute it through the municipality's managed channel or MDM with an approved release process. Test upgrade, rollback, token revocation, device loss, OS permission denial, battery restrictions, data clearing and shift expiry on representative Android hardware. Do not grant background location permission or configure a boot-time tracking workaround.

For PWA deployments, review shared-device access, browser storage eviction, installation policy, HTTPS certificate trust and managed browser updates. Pending edits need operational reconciliation if a lost device or cleared browser deletes its local outbox. A cached shell is not an authoritative job list; every accepted change comes from the authenticated server. Keep municipal emergency contacts and escalation procedures available outside the app. SOS alerts the municipal control room and never replaces 10111/10177/112.

Live Android device behavior, store publication/MDM distribution, live SSO, real municipal locations, lawful notices, native-language/manual accessibility review and operational acceptance remain owner-pending. The local browser and server tests use synthetic data and make no external provider, map-tile or identity write calls.
