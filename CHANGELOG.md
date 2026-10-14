# Changelog

All rounds below were completed locally before the 12–18 October 2026 ACT III online build window. They use synthetic verification data; live AMD/provider/municipal identity and deployed infrastructure evidence remains owner-pending. No round implies a production approval, achieved SLA or published release.

## Unreleased: live providers

- Explicit demo can opt in to live providers with `CIVICOPS_DEMO_LIVE_PROVIDERS=llm,elevenlabs`; the default stays fully simulated.
- Azure OpenAI reasoning provider (`CIVICOPS_LLM_PROVIDER=azure-openai`): managed identity or key, `max_completion_tokens` budget for reasoning models, deterministic fallback on any failure.
- Provider chain (for example `azure-openai,gemini`) with a Google Gemini AI Studio lane (`GEMINI_API_KEY`, `GEMINI_MODEL`), then deterministic rules.
- Optional advisory intake room (`CIVICOPS_INTAKE_ROOM=true`): coordinator, implementer and reviewer seats ported from the Tablekeeper Band classroom, with a labelled recommendation and no automatic dispatch.
- The ElevenLabs resident agent works in the demo through a server-minted signed URL, with the labelled simulated agent as fallback. See [live providers](docs/LIVE_PROVIDERS.md).

## Round 13 — multi-agency operations (cut-down) — 13 October 2026

- Added nine first-class agencies (utilities plus Metro Police, Traffic, Fire & Rescue, EMS and Disaster Management) with per-municipality enable/disable, branding and agency role names mapped onto existing base roles. Metro Police incident details are redacted for staff outside that agency.
- Added the additive `agencies.v1.json` incident catalogue with hazards, PPE, skills, certifications, SLAs and priorities, exposed through the API for later VR module mapping.
- Added cross-agency incidents with a shared audited timeline, ICS-lite incident commander assignment and human-decided mutual-aid requests (recorded only across municipalities).
- Added the `IIncidentDetector` scene-event adapter with an immutable allow-list, a closed schema that rejects imagery and identification fields, and a deterministic simulated camera feed. Detections are proposals until a human confirms or dismisses them. [Multi-agency operations](docs/AGENCIES.md).
- Dashboard screens for the agency board are not part of this cut-down round.

## Round 12 — SOS / panic for residents and staff — 12 October 2026

- Added a tenant-bound SOS alert for the resident desk, crew PWA and Android crew app. A three-second hold and a short cancel window guard against pocket triggers. Staff can choose a silent device mode; the control room still shows an audible and visual alarm. Silence alarm does not clear that banner.
- Location is shared only after SOS, only during the follow-up window (default 30 minutes or until the alert is resolved), and only for a crew member who is already on shift with explicit consent and a visible indicator. Otherwise the person supplies a landmark. SOS does not start tracking. Resident contacts are stored as a tenant-bound hash.
- Control-room staff acknowledge, assess, dispatch or hand over, then resolve. The acknowledgement target defaults to 60 seconds. Unacknowledged alerts record a simulated supervisor note, then a simulated duty-manager note. Hand-over is an audited note that CivicOps did not place the 10111/10177 call. False alarms require an operator reason. A repeated false alarm from the same requester is flagged for human review and is not a score or penalty.
- SMS and WhatsApp whole-message panic phrases (`SOS`, `HELP ME`, `HULP MY`, `NGISIZE`, `NGICELA USIZO`, `SIZA`) record one idempotent alert. Bare `HULP`/`USIZO` stay help text. Voice and IVR stay emergency-number information. The simulated USSD menu is `*120*xxx#`. Every SOS surface states the 10111/10177/112 notice with tap-to-call or dial links. CivicOps does not call responders.
- Paired crew devices may raise and update only their own SOS. They cannot acknowledge, dispatch or read the queue. New blank settings: `CIVICOPS_SOS_LOCATION_WINDOW_MINUTES`, `CIVICOPS_SOS_ACK_TARGET_SECONDS`, `CIVICOPS_SOS_CANCEL_WINDOW_SECONDS`. Live aggregator delivery, responder hand-over staffing and device accessibility remain owner-pending. [SOS operating contract](docs/SOS.md).
- A dispatcher cannot confirm a high-hazard map assignment. That refusal is returned before an optimistic version conflict, so a concurrent case update cannot hide the supervisor requirement.

## Round 11 — unified voice, SMS and WhatsApp intake — 9 October 2026

- Fixed the prior runner-only map accessibility failure with a keyboard-focusable inspector, preserving every browser assertion and adding real keyboard/focus checks.
- Added shared inbound/outbound contracts, encrypted tenant-bound contacts, durable signed replay/message-ID receipts, normalized case routing, per-resident/channel threads, privacy gates, call-taker/callback queues and emergency-information command stubs. Every case still requires human approval.
- Consolidated six native provider adapters and a bounded loopback authentication relay for Twilio voice/SMS, Clickatell, WhatsApp Cloud, ElevenLabs post-call and municipal SIP events. Native signatures, receiver bindings and fresh gateway envelopes fail closed. Default behavior and all captured sends remain explicitly simulated; no live provider writes occurred.
- Added persistent multilingual STOP/STOPALL/START preferences, HELP/REPORT/STATUS, en/af/zu versioned notices and message templates, named human send approval, bounded automatic purge and durable ambiguous-send reconciliation receipts.
- Shipped an accessible unified inbox, per-conversation timelines, reviewed quick replies, channel readiness/metrics and admin template preview/editing. Added provider, pipeline, privacy and browser guards, CI coverage, three inspected captures and [setup/acceptance documentation](docs/CHANNELS.md).
- Live provider registration/delivery/playback/failover, municipal notices/translations/POPIA acceptance, TLS relay deployment and operational staffing remain owner-pending. Agency emergency response, SOS dispatch and training packs require future rounds. [Verification](docs/VERIFICATION.md) records measured local evidence.

## Round 10 — consent-based crew applications — 9 October 2026

- Added a Kotlin/Compose Android crew application with Material 3, assigned jobs and safety procedures, human status reports, notes, sanitized photos, AppAuth PKCE, Keystore-backed credential storage and a Room/WorkManager offline outbox. The pinned official toolchain builds locally; physical-device and municipal MDM acceptance remain pending.
- Shipped the installable `/crew/` PWA with a bounded IndexedDB outbox, tenant-scoped offline shell, explicit conflict review and no stored location pings. Server-verified shifts and revocable consent gate visible location sharing; hidden/offline pages stop collection and require a new explicit start.
- Added supervised one-use pairing, scoped expiring/revocable devices, trusted native OIDC exchange, closed OpenAPI contracts, tenant policy, own-history export and automatic raw-history/photo purge. Off-shift, missing-consent and invalid-device batches fail with an audited 403. Idempotency receipts bind privacy actions and UTC-day location batches.
- Connected accepted crew pings to the supervisor map and added audited work-evidence review to the dashboard. Crew completion remains a report awaiting a dispatcher decision. Readiness checks crew-state integrity in every tenant. SOS is an honest stub that contacts nobody and dispatches nobody.
- Extended responsible-AI, privacy, client-manifest and server guards, browser/axe checks, native unit tests and CI definitions. Five inspected PWA/map captures, [crew deployment/POPIA notice](docs/CREW_APP.md), [native build evidence](docs/ANDROID_BUILD.md) and [verification](docs/VERIFICATION.md) record implemented behavior and acceptance limits. New blank configuration: `CIVICOPS_CREW_OIDC_CLIENT_ID`.

- Kept native generated outputs with the external toolchain and restricted a secrets-scan prose exception to one exact OpenAPI match in two named files; full history and directory scans retain credential detection.

## Round 10 baseline repair — 9 October 2026

- The mandatory starting verifier reported 1,224 passing tests and one failure: the training-integrity restart fixture received a disposed deferred-host provider exception instead of the original integrity exception. The fixture now runs the actual startup recovery service against fresh stores, requires the exact `InvalidDataException` for startup and subsequent reads, and verifies that the tampered file remains unchanged. Application integrity rules and all existing test cases are retained.

## Round 9 verification repair — 9 October 2026

- Fixed workflow conflicts caused by Zendesk delivery receipts arriving during unlocked communication drafting or crew eligibility checks. Requests without an explicit version preserve pure delivery metadata atomically; explicit versions and concurrent human, safety, consent, contact or location changes still conflict. Existing delivery and human audit events remain intact.
- Separated stream-test HTTP setup/intake from the original deadlines for individual event reads, with guaranteed disconnect cleanup. The ward-override success fixture now owns delivery timing; a deterministic stale-version rejection and refreshed-version recovery test preserves the strict API contract.
- Added 24 regression cases per enabled storage backend, retaining all prior tests and responsible-AI/location guards. Updated API, architecture, storage and human-approval evidence; [verification](docs/VERIFICATION.md) records failed baseline/reproduction attempts and final measured gates. No new environment variables or live integrations.

## Round 9 — maps and live operations — 8 October 2026

- Pinned Node 24 Actions and Ubuntu 24.04 runners after read-only release/CI review; owner GHAS opt-ins preserved. CI runs both browser suites with bounded expanded job budgets.
- Vendored Leaflet 1.9.4 and markercluster 1.5.3 with licences, reviewed integrity, SBOM and continuous npm advisory checks; repository canvas heat rendering, narrow tile CSP and tested outline fallback.
- Tenant-scoped durable GeoJSON, optional explicitly shared case geofencing, audited jurisdiction overrides, allow-listed human-reviewed hazards and automatic coordinate/crew/hazard purge.
- Twelve map operations with bounded layers, ETags, aggregate heat, qualification evidence, authenticated bounded SSE and human-confirmed nearest dispatch; closed portable schemas and synchronized OpenAPI.
- Dark control-room/case maps with clustering, status/priority shapes, hazard radii, ward review statistics, search, layers/time filters, stale ages and complete keyboard feature alternatives. Revoked authorization clears private data; live refreshes preserve decision drafts and feedback.
- Deterministic 120-case/25-crew/10-hazard evening scenario and four inspected offline captures. Added responsible-AI/schema/consent guards, tamper/readiness tests and map browser smoke; live worker/camera ingestion remains disabled.
- Bounded PII match timeouts now redact the complete value instead of aborting intake; no match deadline was relaxed. [Round handover](docs/ROUND9_STATUS.md), [map guide](docs/MAPS.md), [responsible AI](docs/RESPONSIBLE_AI.md) and [verification](docs/VERIFICATION.md) record acceptance limits and measured results.

## Round 8 — enterprise handover and judge refresh — 8 October 2026

- Eleven MADR architecture decisions with code/test references; operating and incident runbooks, expanded DPIA, proposed support terms and municipal onboarding.
- One evidence-backed [readiness matrix](docs/ENTERPRISE_READINESS.md), refreshed README/submission and [dated final verification](docs/VERIFICATION.md).
- Commented blank environment inventory, offline Markdown-link/configuration gate and CI coverage.
- Human-readable decision events, one resolution milestone retaining both audit records, grouped resident-update drafts preserving every recorded run; refreshed inspected timeline/control-room screenshots.

## Round 7 — deployment, accessibility and contracts — 8 October 2026

- Pinned non-root container, PostgreSQL Compose, strict-validated Helm and one-active-instance scaling guardrails; optional ROCm/OTLP assets.
- Bounded file/PostgreSQL k6 workloads and storage projection fixes; axe/browser checks, focus/reflow/contrast improvements.
- English/Afrikaans/isiZulu resident resources and versioned OpenAPI examples/snapshot/Spectral gate.
- [Handover](docs/ROUND7_STATUS.md), baseline head `6e4cd2a`; 1,024 default / 1,086 PostgreSQL tests and 158 browser checks.

## Round 6 — durability, recovery and observability — 8 October 2026

- PostgreSQL backend for all durable entities, versioned migrations, shared storage contracts and tested file import.
- Checksummed/optionally age-encrypted backups, both-backend restore drills, signed audit exports and offline verification.
- Readiness/liveness separation, redacted OTLP signals, provider circuits and proposed SLOs.
- [Handover](docs/ROUND6_STATUS.md), head `ad35475`; 960 default / 1,019 PostgreSQL tests and 91 browser checks.

## Round 5 — security and privacy — 8 October 2026

- Nonced CSP, CSRF, bounded requests/partitioned quotas, mounted-file secrets and heuristic PII/secret redaction.
- Versioned purpose consent, separate optional contacts, retention/tombstones and operator-reviewed DSAR workflows.
- ASVS/STRIDE evidence inventories, disclosure policy, advisory gates, SBOM and configured security CI.
- [Handover](docs/ROUND5_STATUS.md), head `f676093`; 887 tests and 89 browser checks.

## Round 4 — staff identity and municipality administration — 8 October 2026

- OIDC code/PKCE and claim mapping, local staff sessions, RBAC, assigned-crew constraints and municipality partitions.
- Hashed tenant-scoped service accounts with rotation/revocation and accessible administration of catalogue/workforce.
- [Handover](docs/ROUND4_STATUS.md), head `c0db3bb`; 755 tests and 82 browser checks.

## Round 3 — product presentation — 8 October 2026

- Polished resident/control-room layouts, outcome KPIs, agent evidence, qualification badges and responsive accessibility.
- Actual synthetic browser captures and README screenshot gallery; honesty labels preserved.
- [Handover](docs/ROUND3_STATUS.md), head `798d98b`; 625 tests and 51 browser checks.

## Round 2 — workforce, training and integration readiness — 8 October 2026

- Shared work-order catalogue, durable qualified workforce, audited routing override and scoped training extension API.
- Private voice session orchestration, provider dry-run/read-only checks, idempotency/restart/retention hardening and milestone metrics.
- [Handover](docs/ROUND2_STATUS.md), head `9694fe0`; 625 tests and 27 browser checks.

## Round 1 — workflow foundations — 8 October 2026

- Resident web/Open311/chat intake, bounded backend reasoning agents, deterministic hazard floor and human dispatch lifecycle.
- Audit/durable delivery primitives, simulated ElevenLabs/Zendesk/email modes, provider adapters, dashboard and one-command demo.
- Round 1 baseline, head `80482f1`; 454 tests and 19 browser checks.

Earlier retained repository work is preserved in Git history. Current totals supersede historical counts only in [VERIFICATION.md](docs/VERIFICATION.md); previous evidence remains dated to its round.
