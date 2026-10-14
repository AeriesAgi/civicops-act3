# Municipal CivicOps privacy impact assessment

Reviewed **8 October 2026 (SAST, UTC+02:00)**. **Engineering sections prefilled; municipality-specific decisions/sign-off owner-pending. This is not an approved DPIA or compliance certification.** Complete it before real resident/staff data processing. Keep the completed assessment, contracts, real data inventory and signatures in protected municipal records; this repository remains synthetic. The [POPIA control map](POPIA.md), [threat model](../security/THREAT_MODEL.md), [readiness matrix](../ENTERPRISE_READINESS.md) and [onboarding checklist](../ONBOARDING_MUNICIPALITY.md) provide implementation evidence.

## Assessment control and accountable people

| Required decision | Municipal completion / evidence |
|---|---|
| Responsible party; deployment/tenant ids and service area | [owner completion] |
| Registered Information Officer, deputy, approved public contact and registration reference | [owner completion in protected records; do not publish personal details here] |
| Assessment owner/version/date; service owner; legal/security/department reviewers | [owner completion] |
| Pilot duration, resident/staff population, volumes, departments and enabled channels | [owner completion] |
| Processing purposes and lawful basis **per flow**, applicable statutory record duties | [owner completion; a purpose checkbox does not settle lawful basis] |
| Children, representatives, special information or vulnerable groups likely to be encountered | [owner assessment; no dedicated child/special-information workflow exists] |
| Languages, accessibility, non-digital alternatives, emergency route and consultation participants | [owner completion; af/zu resources need native review] |
| Approved resident notice/PAIA manual, operator terms and request/objection channels | [owner completion / protected document version] |
| Next review and triggers | [date; new purpose/provider/country/channel, major security/retention/tenancy change or incident] |

## Processing purpose and boundaries

The implemented purpose is to assess, route and manage a municipal service issue, with service updates only when separately requested. Production intake requires current versioned purpose acceptance; optional email/phone and callback choice are separate. Residents have no authenticated identity proof in this build. Municipality staff must verify facts and identity where needed. Human staff approve dispatch and public communications; agents do not make those decisions. Locations remain unverified and catalogue review targets are not arrival commitments.

The training API receives workforce qualifications/completions and applicable simulation-certificate attestations; it cannot read incident information and does not supply a VR curriculum or verify an independent accredited assessor. Reusing incident or workforce information for analytics, research, model training or marketing requires a separate documented purpose/assessment; current access is not permission for those uses.

Tenant partitioning/RBAC is tested, but OIDC/provider credentials, Information Officer contact and retention settings are process-wide. Assess whether one installation's policy fits every municipality; different accounts/policies require separate deployment or a tested extension. PostgreSQL stores all durable municipal entities transactionally; file mode is single-process with individually atomic documents and fail-closed integrity binding. Sessions, quotas and SSE remain local; deployments support one active instance.

## Prefilled data inventory and trust boundaries

Owner completes **necessity/lawful basis, data classification, country/location, recipient/operator/suboperator, exact retention/hold, deletion method and contract/evidence reference** for every enabled flow. “Not enabled” requires a configuration/evidence reference. Do not insert real sample records here.

| Flow / collected data | Implemented minimisation and recipient boundary | Owner decisions still required |
|---|---|---|
| Resident form/Open311 → case store: service description, area/location assertion, chosen language, reference, workflow/consent receipt | Backend narratives redact detectable PII; current-purpose acceptance recorded; public status omits description/contact/transcript/internal actors. Municipal authorized staff see permitted case projections | Approved fields/purpose, source quality, hosting/encryption, ordinary/sensitive-content procedure, retention and emergency/manual alternatives |
| Optional contact → separate contact store → requested updates/callback | Optional email/phone; distinct contact permission; digest-bound storage; contact omitted from standard views, hydrated for permitted delivery and verified admin access export | Contact necessity, withdrawal, verified recipients, delivery channels, contact retention and legal holds |
| Voice/hosted text → ElevenLabs → speech/hosted or custom LLM → tools/post-call | Direct speech/widget text reaches provider **before** backend redaction. Tools require scoped authentication/current consent; signed post-call/replay handling does not prove original resident identity | Voice notice/consent/recordings, account privacy controls, processor/subprocessor countries, retention/deletion, transfers and actual language accessibility |
| Backend case context → configured AMD/vLLM/model endpoint | String values redacted before inference; bounded schema/timeout/fallback; provenance recorded; deterministic mode needs no provider | Private endpoint hosting/country/logging/model terms, no-training guarantees, access/egress, prompt-injection/model quality and live measurement |
| Case/update intents → Zendesk | Private operational notes; requester contact only with consent/valid contact; public comment only after explicit staff approval; durable retries and signed return webhook | Account recipients/permission, OAuth expiry, cross-border terms, provider retention/erasure, deduplication and actual trigger rendering |
| Approved local email/optional SMTP | Labelled local outbox by default; SMTP optional; delivery failure is not proof of receipt | Mail operator/recipient policy, encryption, sent-copy retention and an approved alternative |
| Staff issuer → validated claims → server session | OIDC code/PKCE; explicit role/group/tenant mapping; opaque secure cookie; no persisted provider access/refresh token; server tickets expire/restart | Issuer registration/MFA, source of claims, joiner/leaver review, Google mapping/Entra group-overage limits, session lifetime and actual logout |
| Workforce/training → municipal store/scoped API | Catalogue skills/evidence references, certification dates/source and completion evidence hash; training clients isolated from incidents | Real employee/contractor basis, verified certificates/assessor, HR access/retention and trusted-client sign-off controls; seeds are synthetic |
| Audit/history → exports; app logs/traces/metrics → collector; state → backups | Hash chains detect alteration, HMAC exports require separate key; body-free logs and best-effort PII/secret redaction; bounded labels; checksummed restore with optional age | Historic PII review, encryption/key custody, operator access, exporter/resource scrubbing, retention/off-site immutability and verified destruction |
| Resident request → external identity check → admin access/correction/local anonymisation | Audited verification note without identity documents; versioned case correction; no-store JSON export; local anonymisation suppresses delivery | Approved proof/representative method, response/refusal/appeal procedure, secure export route, recipient correction and completion of provider/backup/history actions |

Attach a deployment-specific diagram showing browser/phone, issuer, ingress, application, database/file store, AMD/model, channel providers, training client, collector, exports and backup system. Mark operator control, network/identity trust boundaries, countries and disabled flows. Include subcontractors and copies at each boundary; a backend redaction claim does not describe the upstream voice boundary.

## Necessity, proportionality and consultation worksheet

| Assessment question | Required recorded decision |
|---|---|
| Why is each field/source needed for the defined service purpose? | Mandatory/optional fields, alternatives, data-quality check, rejected unnecessary IDs/payment/secrets, and approved escalation for sensitive narratives |
| Can the purpose be met with less data or a disabled channel? | Form/phone/assisted alternatives, optional contact rationale, whether live speech is necessary and how residents decline |
| Which basis applies to ordinary municipal service, employee qualifications and optional contact? | Flow-specific municipal legal decision, compatible further uses, consequences of objection/withdrawal and any required statutory record |
| How do residents understand/report/contest decisions? | Notice version/languages, native/accessibility review, supervisor appeal/correction, verified location facts and non-digital access |
| What happens for children, representatives and special information? | External verification and authorized procedure, allowed scope or pilot exclusion, escalation/removal policy; no automatic safeguard is claimed |
| Who was consulted, and what changed? | IO, departments, security/HR, relevant residents/accessibility/native-language reviewers; date, issues, action/evidence and outstanding objections |
| Can providers/roles/account relationships meet policy? | Actual contract/country controls, role least privilege, synthetic-seed exclusion and separate-deployment decision where process-wide settings differ |

Document an access/correction/objection/anonymisation test using synthetic records. Protect verification outside CivicOps; notes describe method/outcome without document copies. Backend redaction detects common contacts/ID patterns and labelled names but misses ordinary names/addresses/unusual prose; it is not anonymity. Avoid collecting biometric/health/ID documents or raw headset evidence into the service narrative/training API.

## Risk assessment and treatment

Suggested owner scoring: likelihood **1 rare–5 almost certain**, impact **1 limited–5 severe**, score `L × I`; impact includes harm to residents/workers, discrimination/safety, confidentiality/integrity/availability and ability to exercise rights. Proposed bands 1–4 low, 5–9 moderate, 10–14 high, 15–25 critical must be approved locally. Missing evidence leaves residual risk **unassessed**, not low. The owner sets treatment and escalation/acceptance rules; critical unresolved exposure, unknown lawful basis or missing essential notice/security controls blocks the proposed pilot.

| Risk | Existing engineering evidence / remaining limitation | Baseline L/I; residual L/I; further action, owner, due date and acceptance evidence |
|---|---|---|
| Cross-tenant or privileged access | Tenant/permission matrix and role-route tests; real issuer/ingress/grants and correct routing remain required | [owner completion] |
| Resident/worker information exposed to speech/model/provider | Backend redactor and scoped clients; direct voice boundary, false negatives and provider copies remain | [owner completion] |
| Unlawful secondary use or unsuitable country transfer | Training denied incident access; no provider contract/transfer approval is provided | [owner completion] |
| Wrong-person export/correction/anonymisation | Verification notes/admin policy/version checks tested; identity proof is external/unverified | [owner completion] |
| Wrong routing, hazard, crew or communication | Bounded validated agents/deterministic fallback and human approval; real model quality/worker certificate authenticity not established | [owner completion] |
| Synthetic seed certificates used in real operations | Visible synthetic records; seed crews must be unavailable and real qualifications independently accepted before live assignment | [owner completion] |
| Contacts/history/backups/exports retained too long or erased unlawfully | Hourly closed-case/contact purge and local anonymisation; no per-record hold, full historical/provider/backup erasure or secure media wipe | [owner completion] |
| Secret, signed evidence or recovery key compromise | Hashed scoped tokens/mounted sources/redacted logs; HMAC-holder can forge exports, external anchoring/key custody remain owner work | [owner completion] |
| File crash, corruption or outage prevents resident participation | Integrity binding fails closed; PostgreSQL transactions and tested restore available; backup schedule/PITR/production recovery unproven | [owner completion] |
| Process-wide policy mismatch / attempted multi-node deployment | Tenant isolation; one-active-instance chart guards; distinct tenant policies and distributed sessions/quotas/SSE not implemented | [owner completion] |
| Leaver retains issuer roles/session or provider token expires | Local rotation/revocation tested; OIDC claim snapshot until re-sign-in/expiry, no continuous directory sync/OAuth refresh | [owner completion] |
| Monitoring/support/breach notice fails in practice | Local OTLP/probe/recovery controls; no deployed alert receiver, staffed roster or exercised statutory submission | [owner completion] |

For each treatment specify prevent/detect/respond control, scope, actual evidence, remaining failure cases and risk acceptance signatory. Do not replace a high residual score with a planned control's future effect. Reassess after synthetic acceptance and again after any material change.

## Retention, legal holds and resident participation

| Record/copy | Proposed period / basis / hold authority / deletion method / verification |
|---|---|
| Closed case snapshot and consent receipt | [owner completion; `CIVICOPS_RETENTION_DAYS` purges snapshot, immutable receipt/history may remain] |
| Optional email/phone | [owner completion; separate `CIVICOPS_PII_RETENTION_DAYS` after closing milestone] |
| Pending/dead-letter intent, post-call receipt and local/provider transcript/ticket/email | [owner completion; scheduled purge protects undelivered cases, explicit local anonymisation suppresses delivery] |
| Staff/worker/training/qualification record | [owner completion; closed-case retention does not purge these] |
| Audit/DSR records, exported copies and logs/collector | [owner completion; historic audit retained and exporter retention is external] |
| Backups, recovery keys and restored historical data | [owner completion; define expiry, encryption, legal-hold and reapplication of accepted erasure after restore] |

Retention is disabled until configured; invalid values disable the relevant schedule. No automated per-record hold or tenant-specific retention policy is implemented. Approve statutory duties/holds before enabling a process-wide purge. Local anonymisation removes current sensitive fields/artifacts but leaves references/workflow/consent/DSR/tombstones; providers, historic immutable audit, backups and prior exports require a separate Officer-led process. Do not promise complete erasure or secure physical wipe.

Record resident notice delivery, identity/representative verification, secure exports, request acknowledgement/decision/appeal/objection timelines approved by the Officer, and how affected recipients receive corrections. Missing external identity/copy-handling evidence is a go-live gap. The UI's Information Officer contact is `configured-unverified`; it is not registration.

## Incident readiness and acceptance evidence

Adopt [INCIDENT_RESPONSE.md](../operations/INCIDENT_RESPONSE.md) with roles/deputies, protected evidence custody, operator escalation, current Regulator/data-subject notification procedure and approved comms templates. Have the Officer verify applicable statutory timing and portal access; internal support targets do not replace those duties. Record the synthetic compromise tabletop and measured restore drill date, attendees, discovered gaps and assigned actions. No live notice is filed by this template.

Minimum evidence packet: approved processing inventory/diagram and notices; operator/transfer decisions; real deployed role/tenant/ingress checks; worker/certificate acceptance; independent probe/collector evidence; encrypted backup/key custody and restore measurements; audited request/retention test; language/accessibility review; incident/support roster/tabletop; risk register and signed residual-risk decision. Local automated results support application claims only, as recorded in [VERIFICATION.md](../VERIFICATION.md).

## Decision and sign-off

| Gate | Evidence / decision / accountable signatory and date |
|---|---|
| Flow-specific lawful basis, notices/PAIA manual, proportionality and consultation | [owner completion] |
| Provider/operator terms, hosting/countries/transfers, permitted purposes and deletion | [owner completion] |
| Identity/roles/tenant, certificates, TLS/encryption/secrets/security acceptance | [owner completion] |
| Retention/statutory holds, participation/verification/appeals and cross-copy handling | [owner completion] |
| Monitoring/support, backup key custody, actual recovery and incident tabletop | [owner completion] |
| Each residual risk accepted, treated before pilot or pilot blocked | [owner completion; list unresolved actions/owners/dates] |
| Information Officer + municipal service owner approval; authorized pilot scope and review date | [owner completion] |

Outcome: **[approved limited pilot / approved with specified conditions / blocked]**. Conditions require measurable acceptance evidence; owner-pending fields do not imply approval. Review on the agreed date and after a new purpose/provider/country, major security/retention change or incident.

Owner source review: [POPIA Act](https://www.justice.gov.za/legislation/acts/2013-004.pdf), [Information Regulator POPIA information](https://inforegulator.org.za/popia/) and [prescribed forms](https://inforegulator.org.za/popia-forms/). This assessment has not been submitted, municipally approved or accepted by the Regulator.
