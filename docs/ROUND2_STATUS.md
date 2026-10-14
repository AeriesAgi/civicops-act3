# CivicOps round 2 — 8 October 2026

Local branch: `grok/civicops-finish`. Round 1 baseline: `80482f1`. Work stayed in this repository with workspace-local temporary verification files. No push, remote/PR/merge/rebase/force operation, Docker run, live provider call, SMTP delivery, LLM write request, real PII or filled environment file was used. The owner will refresh the GitHub token's workflow scope before reviewing/pushing the preserved workflow changes.

## Delivered

1. **Training extension points:** validated versioned catalogue (9 work-order types, 9 skills, 6 certification types), shared classifier/service/model contracts, critical-hazard qualification floor and audited dispatcher override. Durable workforce has 6 crews and 12 synthetic workers with skills/levels, issue/expiry/source/evidence certificates and availability. Skills/availability/department gaps block; certificate gaps follow demo `warn` / non-demo `block`, configurable with `CIVICOPS_CERT_POLICY`.
2. **Scoped training API:** `/api/v1/training/work-order-types`, `/skills`, `/certification-types`, `/workers/{id}/qualifications`, `/work-order-types/{code}/scenarios-hint`; POST `/completions`, `/certifications`; GET `/stream`. Catalogue version/ETag, ten portable schemas, bounded payloads, required idempotency, per-client quotas and separate hashed clients. Training clients cannot access incidents or provider tools. Simulation certificate grants require passing applicable completion and trusted-client supervisor attestation; external/classroom/field certificates cannot be auto-granted. Hash chains and recoverable grant intents survive restarts. Dispatcher audit/verify routes cover training; named events also appear on the existing dispatcher stream.
3. **Live-readiness hardening:** all 37 saved ElevenLabs Markdown references compared with current official `.md` docs; reviewed default/AMD golden provision JSON. Private browser signed URL/WebRTC session orchestration uses pinned official client 1.27.0 with SRI, loaded only after a configured resident starts. ElevenLabs `provision --check` uses GET-only drift reconciliation; `zendesk --check` uses exactly GET `/api/v2/users/me.json`; neither runs at startup. Provider replies, pagination, redirects, demo isolation, webhook attachment and callback uncertainty are hardened. Dispatcher readiness reports names/states, never values.
4. **Durability/identity/retention:** malformed/duplicate named dispatcher configuration fails closed; one process owns a data directory. Files flush before atomic replacement. Configurable hourly closed-case snapshot retention protects undelivered/dead-letter cases, retains audit history and authenticated retired submission-key receipts, and uses a last-update fallback for legacy closing records. `/openapi.json` is generated from routes with embedded portable training schemas.
5. **Judge materials:** audit-derived milestone medians/sample counts, digital intake share, AI/fallback intake counts and duplicate-candidate rate; dashboard synthetic labels and qualification explanations; six inspected actual Chrome PNGs totaling under 1 MB; tighter README; submission pre-event disclosure and owner track options; numbered provider setup and complete blank environment inventory.

## Local commits

| Commit | Subject |
|---|---|
| `2af35e3` | Add versioned work-order taxonomy and durable qualified crews |
| `3a535df` | Benchmark triage against the shared work-order catalogue |
| `47f9c2d` | Add scoped durable training records, simulation certification grants and events |
| `a89a326` | Document training layer contracts and publish portable JSON schemas |
| `b2a0f75` | Keep dispatcher routing overrides coherent and reject unknown service codes |
| `84c4c53` | Wire training contracts and harden retention, persistence and readiness metrics |
| `1bfbf48` | Verify provider contracts and add read-only readiness checks |
| `a008bca` | Document exact owner setup and provider verification boundaries |
| `3e53941` | Measure triage milestones before downstream agent work |
| `5d535af` | Show crew qualifications and private agent sessions with browser evidence |
| `740ffa3` | Run optional Chrome smoke and pin CI scanner checksum |

The final documentation commit follows this table; `git log 80482f1..HEAD --oneline --reverse` gives its hash as well.

## Exact verification

```bash
export PATH=$HOME/.dotnet:$HOME/.local/bin:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 TMPDIR=/workspace/civicops/tmp
dotnet build -c Release
dotnet test -c Release --no-build --logger 'trx;LogFileName=round2.trx' --results-directory "$TMPDIR/test-results"
./scripts/smoke_lifecycle.sh
./scripts/smoke_e2e.sh
CIVICOPS_PLAYWRIGHT_MODULE=/workspace/civicops/tmp/browser-tools/node_modules/playwright-core/index.mjs ./scripts/smoke_browser.sh
gitleaks detect --source . --log-opts="--all" --redact
gitleaks dir . --redact
git diff --check
git status --short
```

| Gate | Result |
|---|---|
| Release build | 0 warnings, 0 errors; 3.21 seconds locally |
| Full tests | **625 passed, 0 failed, 0 skipped**; 9 seconds, independently verified TRX counters |
| New tests versus round 1 | 171: catalogue/workforce 48, training 68, provider 47, operations 8 |
| Lifecycle smoke | Passed non-demo auth, idempotency/version/lifecycle, expanded Open311 catalogue and audit |
| End-to-end smoke | Passed simulated ElevenLabs → agents → Zendesk → human assignment → reviewed comms → post-call/replay/privacy → audit; 10 verified events |
| Browser smoke | **27 passed**, desktop/mobile/retry/offline/qualification checks and both private credential flows with an offline client double |
| Screenshots | Six PNGs inspected visually, under 1 MB total, checked into `docs/media` |
| Gitleaks all history / directory | Both clean; the final scan includes the documentation commit |
| Publish | `dotnet publish products/civicops/CivicOps.csproj -c Release --no-restore -o "$TMPDIR/round2-publish" /p:UseAppHost=false` passed; shipped catalogue present; published `elevenlabs provision --json` dry run parsed successfully with no requests |
| Source/assets | JS `node --check`, shell `bash -n`, Python AST and 18 JSON assets passed; both YAML files parsed; blank env inventories and local documentation links checked |
| Git | All round-2 work committed locally; final status clean |

CI keeps `timeout-minutes: 5`, correct root/project paths and executable smoke scripts. Browser smoke runs only when runner Chrome/Chromium exists, with pinned Playwright 1.64.0; missing Chrome skips cleanly. Gitleaks 8.30.1 uses the committed linux_x64 archive SHA-256 `551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb`. Download timeouts/retries are bounded. Local checks are fast; **remote ubuntu-latest CI has not run because no push was performed**.

## Extension files and contract

- `products/civicops/catalog/work-orders.v1.json`; `Models/Workforce.cs`; `Services/WorkOrderCatalog.cs`, `WorkforceStore.cs`, `CrewEligibilityService.cs`.
- `Models/Training.cs`; `Services/TrainingAccess.cs`, `TrainingEndpoints.cs`, `TrainingStore.cs`; `CaseEventStream.cs`.
- [`EXTENSIONS.md`](EXTENSIONS.md), [`schemas/`](schemas/), [`../layers/README.md`](../layers/README.md), [`../layers/training/README.md`](../layers/training/README.md).
- Ten schemas: work-order catalogue, work-order-types response, skills response, certification-types response, training completion request/response, certification request/response, worker certification and worker qualifications.
- Client format: `CIVICOPS_TRAINING_CLIENTS=name:sha256(token);another:sha256(token)`. A recognized bearer cannot use any non-training route. Dispatcher tokens do not authenticate training. Authentication precedes per-token fixed-window quotas. Duplicate credentials and dispatcher-token collisions fail startup.
- Public demonstration bearer `civicops-demo-training` exists only when explicit demo is true and configured clients are empty. Keep live client tokens in a trusted trainer backend, not in a VR/browser bundle.
- `training.completed` and `certification.granted` SSE signals have empty payloads. Consumers refresh authoritative state after reconnect; there is no durable event replay or outbound webhook sender.

## Full owner configuration inventory

Every placeholder in [root `.env.example`](../.env.example) and [product `.env.example`](../products/civicops/.env.example) is blank. Dotnet does not load them. Export selected values privately. The synthetic demo needs no provider key.

- **Runtime/identity:** `ASPNETCORE_URLS`, `ASPNETCORE_ENVIRONMENT`, `CIVICOPS_DEMO_MODE`, `CIVICOPS_DATA_DIR`, `CIVICOPS_DISPATCHERS` (semicolon-separated verified name:SHA256-token pairs), legacy `CIVICOPS_DISPATCH_TOKEN`, `CIVICOPS_INTAKE_LIMIT`, `CIVICOPS_DISPATCH_LIMIT`, `PUBLIC_BASE_URL` (public HTTPS origin).
- **Crew/training/retention:** `CIVICOPS_CERT_POLICY` (`warn` in demo, `block` otherwise), `CIVICOPS_TRAINING_CLIENTS`, `CIVICOPS_TRAINING_LIMIT` (default 60/minute), `CIVICOPS_TRAINING_PASS_SCORE` (default 80/100), `CIVICOPS_TRAINING_CERTIFICATIONS_ENABLED` (default true), `CIVICOPS_RETENTION_DAYS` (1–36500; otherwise disabled).
- **AMD reasoning:** `CIVICOPS_LLM_BASE_URL`, `CIVICOPS_LLM_MODEL`, optional `CIVICOPS_LLM_API_KEY`, accurate `CIVICOPS_LLM_PROVIDER_LABEL`, optional `CIVICOPS_LLM_TIMEOUT_MS`; `CIVICOPS_LLM_PUBLIC_URL` is the separately reachable full Chat Completions endpoint for ElevenLabs custom LLM. Owner supplies protected AMD infrastructure and real hardware/latency/accuracy evidence.
- **ElevenLabs:** `ELEVENLABS_API_KEY`, optional `ELEVENLABS_API_BASE`, `ELEVENLABS_AGENT_ID`, `ELEVENLABS_CALLBACK_AGENT_ID`, `ELEVENLABS_SESSION_MODE`, `ELEVENLABS_WEBHOOK_SECRET`, new `ELEVENLABS_POST_CALL_WEBHOOK_ID`, `CIVICOPS_ELEVENLABS_TOOL_SECRET`, `ELEVENLABS_WORKSPACE_TOOL_SECRET_ID`, optional `ELEVENLABS_WORKSPACE_LLM_SECRET_ID`, `ELEVENLABS_VOICE_ID`, `ELEVENLABS_WIDGET_ALLOWED_HOSTS`; optional callbacks/transfer: `ELEVENLABS_AGENT_PHONE_NUMBER_ID`, `ELEVENLABS_PHONE_PROVIDER`, `ELEVENLABS_TRANSFER_PHONE` and recorded case consent. The owner must create restricted key permissions, workspace secrets, HMAC webhook, allowed hosts/languages/knowledge and agents via the reviewed provisioner. Exact numbered account steps: [`ELEVENLABS_SETUP.md`](ELEVENLABS_SETUP.md).
- **Zendesk:** `ZENDESK_SUBDOMAIN`, `ZENDESK_OAUTH_TOKEN` OR `ZENDESK_EMAIL` + `ZENDESK_API_TOKEN`, `ZENDESK_WEBHOOK_SECRET`; owner creates/reviews webhook trigger/signature mapping, invokes read-only connectivity check explicitly and validates synthetic tickets/replies. Exact steps: [`ZENDESK_SETUP.md`](ZENDESK_SETUP.md).
- **Optional email:** `CIVICOPS_EMAIL_MODE`, `CIVICOPS_EMAIL_FROM`, `CIVICOPS_EMAIL_NOTIFY_TO`, `CIVICOPS_EMAIL_SMTP_HOST`, `CIVICOPS_EMAIL_SMTP_PORT`, `CIVICOPS_EMAIL_SMTP_USER`, `CIVICOPS_EMAIL_SMTP_PASSWORD`, `CIVICOPS_EMAIL_USE_SSL`. Demo always forces outbox; SMTP is unnecessary for the walkthrough and was never called.
- **Deployment/utilities:** `CIVICOPS_VLLM_IMAGE`, `CIVICOPS_VLLM_PORT`, `CIVICOPS_VLLM_TENSOR_PARALLEL`, `CIVICOPS_MODEL_CACHE`, `CIVICOPS_BASE_URL`, `TMPDIR`; optional browser QA `CIVICOPS_BROWSER_BASE_URL`, `CIVICOPS_CHROME_PATH`, `CIVICOPS_PLAYWRIGHT_MODULE`.

## Still simulated or unverified

The walkthrough used deterministic reasoning, simulated ElevenLabs, exact Zendesk request outboxes and email outbox. Live AMD inference, deployed official SDK/CDN/microphone operation, provider account permissions, English/Afrikaans voice quality, phone routing/callback/transfer and actual Zendesk delivery remain unverified. Provider reconciliation uses fake GET handlers in tests; no live --check was run. Docker runtime and remote CI remain unverified.

Enterprise/resident identity, OAuth refresh, directory fsync, a transactional multi-instance database, external audit anchoring, full cross-artifact/provider PII erasure, backups and a pilot privacy notice remain owner work. Snapshot retention preserves audit/integration/training artifacts and is not complete privacy deletion. Supervisor sign-off is a trusted-client assertion, not independently verified supervisor identity.

All work predates the 12–18 October event build window. Disclose the baseline and confirm eligibility; planned event-week work is AMD measurement, live synthetic provider validation, deployment, video and pitch assets. The current build has no Evolus integration and does not claim its partner prize requirements. Owner chooses main-track fit. No VR/3D/WebXR implementation was added.
