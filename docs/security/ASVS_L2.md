# OWASP ASVS Level 2 engineering review

Reviewed 8 October 2026. Scope: the current CivicOps application and synthetic local tests. This is a gap assessment, **not ASVS certification, a penetration test, a legal compliance opinion or an approval to process live resident data**.

The owner requested V1–V14, so identifiers are explicitly pinned to [OWASP ASVS 4.0.3](https://github.com/OWASP/ASVS/tree/v4.0.3/4.0), rather than silently using the differently organized ASVS 5.x chapters. The [official 4.0.3 CSV](https://github.com/OWASP/ASVS/blob/v4.0.3/4.0/docs_en/OWASP%20Application%20Security%20Verification%20Standard%204.0.3-en.csv) defines the requirements. The 259 rows below cover every requirement included at L2, including inherited L1 requirements, and deliberately omit L3-only requirements. Focus descriptions are CivicOps-specific paraphrases; use the official requirement for a formal verification.

**met** means the scoped application behavior is implemented with the linked local evidence; **partial** means an implementation exists but the full requirement or deployment scope remains incomplete; **N/A** means the relevant feature/interpreter is absent; **owner** means the control belongs to institutional/issuer/hosting configuration and is not locally attested. No percentage is presented as an assurance score.

Current table counts: **92 met, 69 partial, 68 owner, 30 N/A**. PostgreSQL makes V5.3.4 and V5.3.5 applicable; their parameterized-query implementation is assessed as partial pending independent query/deployment review. These counts are an inventory, not certification.

## Changes closing inexpensive gaps

Strict nonce CSP and security headers, cookie antiforgery regression tests, canonical bounded input/JSON/header controls, tenant/principal/IP quotas, env/mounted secret sources and sentinel-safe failure/logging, separate optional contact storage, versioned consent, administrative data-subject workflow, redacted model input/logging and retention tombstones are implemented in this round. Release builds now run latest-recommended analyzers and fail on warnings. Disposable mutation gates, cached serializers, invariant mail/signature formatting and structured logger delegates address analyzer findings. Only allocation-performance suggestions for independent test fixtures are suppressed; no security analyzer is disabled.

CI declares C#/JavaScript CodeQL, dependency review, fail-on-advisory NuGet/npm checks and a pinned official CycloneDX inventory/artifact. These workflows are **statically validated locally; remote execution and repository feature enablement remain owner-pending**. The generator inventories application/test NuGet packages and pinned optional browser bundles; runtime/OS/container/model/embedded bundle dependencies require a deployment inventory.

## Evidence index

Each short key in the requirement table links to concrete files and local test classes below. Deployment documents are evidence of a plan, not evidence that deployment happened.

| Key | Implementation / checks |
|---|---|
| LIFECYCLE | [Threat model](THREAT_MODEL.md), [CI](../../.github/workflows/dotnet.yml), [Verification](../../scripts/verify.sh), [Security policy](../../SECURITY.md) |
| AUTH | [EnterpriseIdentity](../../products/civicops/Services/EnterpriseIdentity.cs), [IdentityOptions](../../products/civicops/Services/IdentityOptions.cs), [IdentityTests](../../tests/CivicOps.Tests/IdentityTests.cs), [OidcIntegrationTests](../../tests/CivicOps.Tests/OidcIntegrationTests.cs) |
| RBAC | [PermissionMatrix](../../products/civicops/Services/PermissionMatrix.cs), [Resource guards](../../products/civicops/Services/EnterpriseAccessMiddleware.cs), [RbacTests](../../tests/CivicOps.Tests/RbacTests.cs), [TenantEndpointTests](../../tests/CivicOps.Tests/TenantEndpointTests.cs) |
| INPUT | [RequestValidation](../../products/civicops/Services/RequestValidation.cs), [RequestHardening](../../products/civicops/Services/RequestHardening.cs), [RequestHardeningTests](../../tests/CivicOps.Tests/RequestHardeningTests.cs) |
| UI | [Browser rendering](../../products/civicops/wwwroot/app.js), [Browser identity](../../products/civicops/wwwroot/admin.js), [Browser smoke](../../scripts/browser_smoke.mjs) |
| SECRET | [SecretProvider](../../products/civicops/Services/SecretProvider.cs), [SecretProviderTests](../../tests/CivicOps.Tests/SecretProviderTests.cs), [Blank inventory](../../.env.example) |
| AUDIT | [AuditLog](../../products/civicops/Services/AuditLog.cs), [IncidentStore](../../products/civicops/Services/IncidentStore.cs), [PersistenceAuditTests](../../tests/CivicOps.Tests/PersistenceAuditTests.cs), [Signed exports](../operations/AUDIT_EXPORT.md), [AuditExportTests](../../tests/CivicOps.Tests/AuditExportTests.cs) |
| PRIVACY | [PrivacyService](../../products/civicops/Services/PrivacyService.cs), [PrivacyConsent](../../products/civicops/Services/PrivacyConsent.cs), [Separate contacts](../../products/civicops/Services/PrivacyContactStore.cs), [PiiRedactor](../../products/civicops/Services/PiiRedactor.cs), [PrivacyTests](../../tests/CivicOps.Tests/PrivacyTests.cs), [POPIA mapping](../privacy/POPIA.md) |
| HEADER | [SecurityHeaders](../../products/civicops/Services/SecurityHeaders.cs), [SecurityHeadersTests](../../tests/CivicOps.Tests/SecurityHeadersTests.cs), [HTTP security](HTTP_SECURITY.md) |
| CSRF | [Antiforgery guard](../../products/civicops/Services/EnterpriseAccessMiddleware.cs), [CsrfTests](../../tests/CivicOps.Tests/CsrfTests.cs) |
| TOKEN | [ServiceAccountStore](../../products/civicops/Services/ServiceAccountStore.cs), [BootstrapUserStore](../../products/civicops/Services/BootstrapUserStore.cs), [ServiceAccountTests](../../tests/CivicOps.Tests/ServiceAccountTests.cs) |
| PROVIDER | [HMAC verification](../../products/civicops/Services/ElevenLabsSignature.cs), [ZendeskClient](../../products/civicops/Services/ZendeskClient.cs), [ProviderReadinessTests](../../tests/CivicOps.Tests/ProviderReadinessTests.cs), [Signature tests](../../tests/CivicOps.Tests/ElevenLabsSignatureTests.cs) |
| WORKFLOW | [IncidentWorkflow](../../products/civicops/Services/IncidentWorkflow.cs), [IncidentCoordinator](../../products/civicops/Services/IncidentCoordinator.cs), [ClassifierWorkflowTests](../../tests/CivicOps.Tests/ClassifierWorkflowTests.cs), [TrainingTests](../../tests/CivicOps.Tests/TrainingTests.cs), [DurableStorage](../../products/civicops/Services/DurableStorage.cs), [PostgresStorageTests](../../tests/CivicOps.Tests/PostgresStorageTests.cs), [Storage boundaries](../operations/STORAGE.md) |
| RATE | [Partitioned quotas](../../products/civicops/Program.cs), [Request/body bounds](../../products/civicops/Services/RequestHardening.cs), [FoundationTests](../../tests/CivicOps.Tests/FoundationTests.cs), [RequestHardeningTests](../../tests/CivicOps.Tests/RequestHardeningTests.cs) |
| DEPENDENCY | [Analyzers](../../Directory.Build.props), [Advisory gate](../../scripts/security_dependencies.sh), [SBOM generator](../../scripts/sbom.sh), [Current SBOM](sbom.cdx.json), [CodeQL](../../.github/workflows/codeql.yml) |
| DEPLOY | [Dockerfile](../../Dockerfile), [Compose](../../docker-compose.yml), [Architecture](../../docs/ARCHITECTURE.md), [Deployment risks](THREAT_MODEL.md), [Backup/restore](../operations/BACKUP_RESTORE.md), [Recovery drill](../../scripts/smoke_restore.sh) |
| NO_FEATURE | [Mapped routes](../../products/civicops/Program.cs), [Complete route matrix](../../products/civicops/Services/PermissionMatrix.cs), [Dependencies](../../products/civicops/CivicOps.csproj) |

## V1 — Architecture, Design and Threat Modeling

| Requirement | Focus | Status | Evidence | Scope / remaining work |
|---|---|---|---|---|
| V1.1.1 | Security lifecycle | partial | [LIFECYCLE](#evidence-index) | Institutional lifecycle/deployment review must be repeated; no independent certification. |
| V1.1.2 | Threat review during changes | met | [LIFECYCLE](#evidence-index) | Documented boundaries, change review checklist and risk register; owner must apply them to future changes. |
| V1.1.3 | Security acceptance criteria | partial | [LIFECYCLE](#evidence-index) | Institutional lifecycle/deployment review must be repeated; no independent certification. |
| V1.1.4 | Trust boundaries and flows | met | [LIFECYCLE](#evidence-index) | Documented boundaries, change review checklist and risk register; owner must apply them to future changes. |
| V1.1.5 | Connected-service architecture | met | [LIFECYCLE](#evidence-index) | Documented boundaries, change review checklist and risk register; owner must apply them to future changes. |
| V1.1.6 | Shared security enforcement | met | [RBAC](#evidence-index) | Central route policies and tenant/assignment guards; every mapped method/route checked by RBAC tests. |
| V1.1.7 | Developer security checklist | met | [LIFECYCLE](#evidence-index) | Documented boundaries, change review checklist and risk register; owner must apply them to future changes. |
| V1.2.1 | Least-privilege OS identities | owner | [DEPLOY](#evidence-index) | Non-root/container assets exist; daemon unavailable; host identities, network isolation and deployment integrity unverified. |
| V1.2.2 | Authenticated component links | partial | [AUTH](#evidence-index) | OIDC and scoped services are tested; legacy shared bootstrap identities and incomplete security-event monitoring remain. |
| V1.2.3 | Vetted authentication with monitoring | partial | [AUTH](#evidence-index) | OIDC and scoped services are tested; legacy shared bootstrap identities and incomplete security-event monitoring remain. |
| V1.2.4 | Consistent authentication pathways | partial | [AUTH](#evidence-index) | OIDC and scoped services are tested; legacy shared bootstrap identities and incomplete security-event monitoring remain. |
| V1.4.1 | Server-side authorization | met | [RBAC](#evidence-index) | Central route policies and tenant/assignment guards; every mapped method/route checked by RBAC tests. |
| V1.4.4 | One shared permission mechanism | met | [RBAC](#evidence-index) | Central route policies and tenant/assignment guards; every mapped method/route checked by RBAC tests. |
| V1.4.5 | Resource attributes alongside roles | met | [RBAC](#evidence-index) | Central route policies and tenant/assignment guards; every mapped method/route checked by RBAC tests. |
| V1.5.1 | Input/output and privacy contracts | met | [INPUT](#evidence-index) | Defined bounded server-side DTOs, allowlists and privacy purposes. |
| V1.5.2 | Safe serialization trust boundary | partial | [INPUT](#evidence-index) | JSON DTOs are necessary at API boundaries; no arbitrary runtime type binding; TLS deployment pending. |
| V1.5.3 | Server-side validation | met | [INPUT](#evidence-index) | Defined bounded server-side DTOs, allowlists and privacy purposes. |
| V1.5.4 | Context-aware output encoding | met | [UI](#evidence-index) | HTML escaped on rendering; JSON serialized with framework; narrative never treated as executable code. |
| V1.6.1 | Cryptographic key lifecycle | partial | [SECRET](#evidence-index) | Env/mounted-file providers and replaceable scoped tokens implemented; remote vault adapter/key lifecycle owner-pending. |
| V1.6.2 | Protected secret consumers | partial | [SECRET](#evidence-index) | Env/mounted-file providers and replaceable scoped tokens implemented; remote vault adapter/key lifecycle owner-pending. |
| V1.6.3 | Replaceable keys and credentials | partial | [SECRET](#evidence-index) | Env/mounted-file providers and replaceable scoped tokens implemented; remote vault adapter/key lifecycle owner-pending. |
| V1.6.4 | No browser secret as security boundary | met | [TOKEN](#evidence-index) | Production staff use opaque cookies; server tool secrets stay server-side; trainer token belongs in trusted backend. |
| V1.7.1 | Consistent structured logging | partial | [LIFECYCLE](#evidence-index) | Institutional lifecycle/deployment review must be repeated; no independent certification. |
| V1.7.2 | Remote protected log collection | owner | [AUDIT](#evidence-index) | Remote collector, transport protection, SIEM rules and incident escalation are deployment duties. |
| V1.8.1 | Data classification | met | [PRIVACY](#evidence-index) | Contact, narrative, workforce, credential, audit and provider artifacts classified in privacy/threat review. |
| V1.8.2 | Protection requirements per classification | partial | [LIFECYCLE](#evidence-index) | Institutional lifecycle/deployment review must be repeated; no independent certification. |
| V1.9.1 | Encrypted component communications | partial | [PROVIDER](#evidence-index) | Configured provider/issuer HTTPS with default peer validation; operator model endpoint/local demo may use HTTP. |
| V1.9.2 | Peer certificate validation | partial | [PROVIDER](#evidence-index) | Configured provider/issuer HTTPS with default peer validation; operator model endpoint/local demo may use HTTP. |
| V1.10.1 | Traceable controlled source changes | partial | [LIFECYCLE](#evidence-index) | Descriptive local Git commits exist; protected remote branch, approvals and issue traceability require owner setup. |
| V1.11.1 | Documented business components | met | [WORKFLOW](#evidence-index) | Architecture and STRIDE table identify component business/security responsibilities. |
| V1.11.2 | Synchronized sensitive state | partial | [WORKFLOW](#evidence-index) | File mode is single-process; PostgreSQL case/contact/audit/outbox transactions and cross-instance versions are tested. Coarse database locking, process-local sessions/quotas and external delivery remain boundaries. |
| V1.12.2 | Safe serving of uploaded content | N/A | [NO_FEATURE](#evidence-index) | No resident upload/download execution feature; fixed evidence/JSON exports only. |
| V1.14.1 | Network trust-zone separation | owner | [DEPLOY](#evidence-index) | Non-root/container assets exist; daemon unavailable; host identities, network isolation and deployment integrity unverified. |
| V1.14.2 | Authenticated signed deployment | owner | [DEPLOY](#evidence-index) | Non-root/container assets exist; daemon unavailable; host identities, network isolation and deployment integrity unverified. |
| V1.14.3 | Dependency security pipeline | met | [DEPENDENCY](#evidence-index) | NuGet findings fail gate; npm manifests require lockfiles and audit; remote CI execution pending. |
| V1.14.4 | Secure deployment verification | owner | [DEPLOY](#evidence-index) | Non-root/container assets exist; daemon unavailable; host identities, network isolation and deployment integrity unverified. |
| V1.14.5 | Runtime isolation | owner | [DEPLOY](#evidence-index) | Non-root/container assets exist; daemon unavailable; host identities, network isolation and deployment integrity unverified. |
| V1.14.6 | Supported browser technology | met | [UI](#evidence-index) | HTML, modern JavaScript and standard browser media APIs; no legacy plug-ins. |

## V2 — Authentication

| Requirement | Focus | Status | Evidence | Scope / remaining work |
|---|---|---|---|---|
| V2.1.1 | Minimum password length | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.1.2 | Long password acceptance | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.1.3 | No password truncation | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.1.4 | International password characters | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.1.5 | Password-change capability | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.1.6 | Current credential for password change | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.1.7 | Breached-password screening | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.1.8 | Password-strength feedback | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.1.9 | Avoid arbitrary composition rules | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.1.10 | Avoid forced periodic password replacement | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.1.11 | Password-manager and paste support | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.1.12 | Optional password visibility | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.2.1 | Bounded credential attempts | partial | [RATE](#evidence-index) | Tenant/IP/principal quotas implemented; an issuer-specific 100-failures-per-hour account rule is not independently guaranteed. |
| V2.2.2 | Strong primary authentication | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.2.3 | Authentication-change notifications | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.3.1 | Secure temporary activation secrets | partial | [TOKEN](#evidence-index) | Random administrator-issued bootstrap credentials expire/rotate; they are longer-lived bearer credentials, not one-use password activation. |
| V2.3.2 | User hardware-authenticator enrollment | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.3.3 | Authenticator expiry reminders | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.4.1 | Costed password hashes | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.4.2 | Unique password salts | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.4.3 | PBKDF2 work factor | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.4.4 | Bcrypt work factor | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.4.5 | Separately protected password pepper | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.5.1 | Confidential activation-secret delivery | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.5.2 | No security questions | N/A | [NO_FEATURE](#evidence-index) | CivicOps has no password hints, security questions or password recovery; issuer policy remains owner responsibility. |
| V2.5.3 | No recoverable password disclosure | N/A | [NO_FEATURE](#evidence-index) | CivicOps has no password hints, security questions or password recovery; issuer policy remains owner responsibility. |
| V2.5.4 | No shared default account | partial | [AUTH](#evidence-index) | No production default administrator password; labelled shared demo/legacy bootstrap identities remain explicit exceptions. |
| V2.5.5 | Factor-change notification | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.5.6 | Secure credential recovery | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.5.7 | Equivalent identity proof for recovery | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.6.1 | Single-use recovery codes | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.6.2 | High-entropy recovery codes | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.6.3 | Offline-resistant recovery codes | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.7.1 | Strong out-of-band options | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.7.2 | Short out-of-band expiry | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.7.3 | Single-use request-bound codes | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.7.4 | Separate protected verification channel | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.7.5 | Hashed verification codes | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.7.6 | Random verification codes | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.8.1 | OTP expiry | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.8.2 | Protected OTP seeds | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.8.3 | Approved OTP cryptography | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.8.4 | Single-use OTP window | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.8.5 | OTP replay alerts | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.8.6 | Immediate lost-device revocation | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.8.7 | Biometrics only as additional factor | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.9.1 | Protected authenticator keys | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.9.2 | Unique challenge nonces | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.9.3 | Approved challenge-response cryptography | owner | [AUTH](#evidence-index) | Identity-provider authentication, MFA, enrollment and recovery policies require municipal validation. |
| V2.10.1 | Rotatable service credentials | partial | [TOKEN](#evidence-index) | Scoped tokens expire/rotate/revoke; provider keys and legacy bootstrap secrets still require manual rotation. |
| V2.10.2 | No default service credentials | met | [TOKEN](#evidence-index) | No configured production credentials supplied by default; synthetic demo credentials work only in explicit demo. |
| V2.10.3 | Offline-resistant service credential storage | partial | [TOKEN](#evidence-index) | Random account credentials stored hashed; env/mounted provider secrets are readable to host and require protected mounts. |
| V2.10.4 | Secrets excluded from source control | partial | [SECRET](#evidence-index) | Blank inventory and sentinel-safe env/file sources; vault adapter, mount permissions and offline host protection pending. |

## V3 — Session Management

| Requirement | Focus | Status | Evidence | Scope / remaining work |
|---|---|---|---|---|
| V3.1.1 | No session secret in URL | met | [AUTH](#evidence-index) | Production cookie mode; explicit localhost demo is an identified exception. |
| V3.2.1 | New session at authentication | met | [AUTH](#evidence-index) | Production cookie mode; explicit localhost demo is an identified exception. |
| V3.2.2 | Session entropy | met | [AUTH](#evidence-index) | Production cookie mode; explicit localhost demo is an identified exception. |
| V3.2.3 | Protected browser token storage | met | [AUTH](#evidence-index) | Production cookie mode; explicit localhost demo is an identified exception. |
| V3.2.4 | Cryptographic session generation | met | [AUTH](#evidence-index) | Production cookie mode; explicit localhost demo is an identified exception. |
| V3.3.1 | Logout and expiry invalidate sessions | partial | [AUTH](#evidence-index) | Local logout and expiry tested; global issuer/downstream logout needs real provider validation. |
| V3.3.2 | Bounded reauthentication interval | met | [AUTH](#evidence-index) | Production cookie mode; explicit localhost demo is an identified exception. |
| V3.3.3 | Revoke sessions after credential change | partial | [AUTH](#evidence-index) | Bootstrap role/revocation checked per request; no user device/session inventory or global OIDC session-revoke UI. |
| V3.3.4 | Device/session management | partial | [AUTH](#evidence-index) | Bootstrap role/revocation checked per request; no user device/session inventory or global OIDC session-revoke UI. |
| V3.4.1 | Secure cookie flag | met | [AUTH](#evidence-index) | Production cookie mode; explicit localhost demo is an identified exception. |
| V3.4.2 | HttpOnly cookie flag | met | [AUTH](#evidence-index) | Production cookie mode; explicit localhost demo is an identified exception. |
| V3.4.3 | SameSite cookie flag | met | [AUTH](#evidence-index) | Production cookie mode; explicit localhost demo is an identified exception. |
| V3.4.4 | Host-only cookie prefix | met | [AUTH](#evidence-index) | Production cookie mode; explicit localhost demo is an identified exception. |
| V3.4.5 | Precise isolated cookie path | met | [AUTH](#evidence-index) | Production __Host cookie has path=/ and no Domain; dedicate an application host in deployment. |
| V3.5.1 | Revocable linked OAuth grants | N/A | [NO_FEATURE](#evidence-index) | No user-linked OAuth grant feature; provider service credentials rotated outside the resident browser. |
| V3.5.2 | Session credentials for humans | partial | [AUTH](#evidence-index) | Staff cookies use sessions; explicit legacy bootstrap bearer pathways are retained for compatible installations. |
| V3.5.3 | Protected stateless session tokens | N/A | [AUTH](#evidence-index) | Browser gets an opaque server-side ticket; application does not expose stateless staff JWT sessions. |
| V3.7.1 | Full authentication or step-up for sensitive action | owner | [AUTH](#evidence-index) | Admin authorization exists; issuer MFA/conditional access and step-up transaction policy need institutional enforcement. |

## V4 — Access Control

| Requirement | Focus | Status | Evidence | Scope / remaining work |
|---|---|---|---|---|
| V4.1.1 | Trusted authorization enforcement | met | [RBAC](#evidence-index) | Server enforcement tested; real issuer/host controls require deployment validation. |
| V4.1.2 | Protected role/resource attributes | met | [RBAC](#evidence-index) | Server enforcement tested; real issuer/host controls require deployment validation. |
| V4.1.3 | Least-privilege permissions | met | [RBAC](#evidence-index) | Server enforcement tested; real issuer/host controls require deployment validation. |
| V4.1.5 | Fail-closed authorization | met | [RBAC](#evidence-index) | Server enforcement tested; real issuer/host controls require deployment validation. |
| V4.2.1 | Object-level isolation | met | [RBAC](#evidence-index) | Server enforcement tested; real issuer/host controls require deployment validation. |
| V4.2.2 | CSRF and unauthenticated automation defenses | met | [CSRF](#evidence-index) | Cookie mutation/login antiforgery tested; service bearer/HMAC requests exempt by design; intake quotas limit automation. |
| V4.3.1 | Administrator MFA | owner | [AUTH](#evidence-index) | Admin authorization exists; issuer MFA/conditional access and step-up transaction policy need institutional enforcement. |
| V4.3.2 | No directory or metadata browsing | met | [NO_FEATURE](#evidence-index) | Only wwwroot static assets are served; no directory browsing or runtime data static provider. |
| V4.3.3 | Step-up and separation of duties | owner | [AUTH](#evidence-index) | Admin authorization exists; issuer MFA/conditional access and step-up transaction policy need institutional enforcement. |

## V5 — Validation, Sanitization and Encoding

| Requirement | Focus | Status | Evidence | Scope / remaining work |
|---|---|---|---|---|
| V5.1.1 | Parameter pollution defenses | partial | [INPUT](#evidence-index) | Bounds, typed DTOs, Unicode/header/duplicate JSON defenses tested; positive grammar on every evolving field needs continuing review. |
| V5.1.2 | No uncontrolled mass assignment | met | [INPUT](#evidence-index) | Explicit request DTOs and route-authorized mutations cannot mass-assign internal approval/actor/tenant state. |
| V5.1.3 | Positive input validation | partial | [INPUT](#evidence-index) | Bounds, typed DTOs, Unicode/header/duplicate JSON defenses tested; positive grammar on every evolving field needs continuing review. |
| V5.1.4 | Typed constrained structured data | partial | [INPUT](#evidence-index) | Bounds, typed DTOs, Unicode/header/duplicate JSON defenses tested; positive grammar on every evolving field needs continuing review. |
| V5.1.5 | Safe redirect destinations | met | [AUTH](#evidence-index) | Sign-in return target accepts only bounded local paths; provider hosts come from trusted configuration. |
| V5.2.1 | Sanitized rich HTML | N/A | [NO_FEATURE](#evidence-index) | No resident rich-HTML, SVG, expression-language, Markdown or stylesheet input renderer. |
| V5.2.2 | Bounded safe free text | met | [INPUT](#evidence-index) | Bounded narrative, control-character validation and canonicalization; Unicode/language text stays plain data. |
| V5.2.3 | Mail-system injection defenses | met | [INPUT](#evidence-index) | Validated mail recipients, bounded settings and header-control rejection; no raw resident mail headers. |
| V5.2.4 | No uncontrolled dynamic evaluation | met | [UI](#evidence-index) | No evaluation of resident data; template fragments escape values before insertion. |
| V5.2.5 | Template injection defenses | met | [UI](#evidence-index) | No evaluation of resident data; template fragments escape values before insertion. |
| V5.2.6 | Restricted outbound request targets | partial | [PROVIDER](#evidence-index) | No resident-supplied outbound URL; provider/issuer origins validated; operator LLM URL and network egress require review. |
| V5.2.7 | No untrusted active SVG | N/A | [NO_FEATURE](#evidence-index) | No resident rich-HTML, SVG, expression-language, Markdown or stylesheet input renderer. |
| V5.2.8 | No untrusted expression/stylesheet language | N/A | [NO_FEATURE](#evidence-index) | No resident rich-HTML, SVG, expression-language, Markdown or stylesheet input renderer. |
| V5.3.1 | Context-specific encoders | met | [UI](#evidence-index) | Framework JSON serialization, safe parsing and context escaping; browser XSS/CSP coverage retained. |
| V5.3.2 | Preserved safe international text | partial | [INPUT](#evidence-index) | Unicode stays UTF-8; unsafe control/bidi abuse is rejected; linguistic correctness requires municipal language review. |
| V5.3.3 | Stored/reflected/DOM XSS defenses | met | [UI](#evidence-index) | Framework JSON serialization, safe parsing and context escaping; browser XSS/CSP coverage retained. |
| V5.3.4 | Parameterized database queries | partial | [WORKFLOW](#evidence-index) | PostgreSQL data values use Npgsql parameters; the only dynamic schema identifier is bounded to ASCII letters/digits/underscore. Embedded migrations are trusted build assets. Independent query review and production penetration testing remain owner-pending. |
| V5.3.5 | Safe fallback query escaping | partial | [WORKFLOW](#evidence-index) | No value-escaping fallback is used: values are parameterized and schema selection is allowlisted. SQL migrations come from embedded build assets. Independent query/deployment review and production penetration testing remain owner-pending. |
| V5.3.6 | Safe JSON serialization/parsing | met | [UI](#evidence-index) | Framework JSON serialization, safe parsing and context escaping; browser XSS/CSP coverage retained. |
| V5.3.7 | LDAP injection defenses | N/A | [NO_FEATURE](#evidence-index) | No LDAP query interpreter is implemented; PostgreSQL storage does not introduce LDAP. |
| V5.3.8 | Command injection defenses | N/A | [NO_FEATURE](#evidence-index) | API does not invoke a shell or operating-system command with resident-controlled arguments. |
| V5.3.9 | File inclusion defenses | met | [INPUT](#evidence-index) | Record paths generated server-side; tenant identifiers validated; no resident file inclusion. |
| V5.3.10 | XML/XPath injection defenses | N/A | [NO_FEATURE](#evidence-index) | No XML/XPath query interpreter is implemented; PostgreSQL storage does not introduce XML/XPath. |
| V5.4.1 | Memory-safe data operations | met | [DEPENDENCY](#evidence-index) | Managed .NET/JavaScript; no unsafe blocks or native request-processing code in this repository. |
| V5.4.2 | Constant format strings | met | [AUDIT](#evidence-index) | Constant logger templates; resident text is data, not a format string. |
| V5.4.3 | Integer range and overflow controls | partial | [INPUT](#evidence-index) | Version/count/score/range checks and bounded bodies tested; no formal overflow proof for all counters. |
| V5.5.1 | Trusted serialized-object integrity | partial | [AUDIT](#evidence-index) | Stored snapshots have audit/state integrity checks; public JSON DTOs rely on validation and deployment TLS. |
| V5.5.2 | Restricted XML parser | N/A | [NO_FEATURE](#evidence-index) | No XML input parser or document feature. |
| V5.5.3 | Safe untrusted deserialization | met | [INPUT](#evidence-index) | Typed System.Text.Json with depth/body limits; no polymorphic resident object activation. |
| V5.5.4 | Browser JSON parsing without eval | met | [UI](#evidence-index) | Browser uses response.json/JSON.parse; no eval JSON parsing. |

## V6 — Stored Cryptography

| Requirement | Focus | Status | Evidence | Scope / remaining work |
|---|---|---|---|---|
| V6.1.1 | Personal data encrypted at rest | owner | [DEPLOY](#evidence-index) | Application files are not encrypted at rest; host-volume/backups encryption and key custody required before real PII. |
| V6.1.2 | Health data encrypted at rest | N/A | [PRIVACY](#evidence-index) | Health/financial processing is outside intended purpose; incidental sensitive narrative requires minimization and owner review. |
| V6.1.3 | Financial data encrypted at rest | N/A | [PRIVACY](#evidence-index) | Health/financial processing is outside intended purpose; incidental sensitive narrative requires minimization and owner review. |
| V6.2.1 | Fail-closed cryptographic errors | met | [TOKEN](#evidence-index) | Platform CSPRNG/SHA-256/HMAC and constant-time comparisons; no custom cipher or obsolete application hashing. |
| V6.2.2 | Vetted cryptographic primitives | met | [TOKEN](#evidence-index) | Platform CSPRNG/SHA-256/HMAC and constant-time comparisons; no custom cipher or obsolete application hashing. |
| V6.2.3 | Secure encryption modes and IVs | N/A | [NO_FEATURE](#evidence-index) | No application-managed at-rest encryption/IV mechanism; ASP.NET cookie/data protection delegated to framework. |
| V6.2.4 | Upgradable cryptographic policy | partial | [TOKEN](#evidence-index) | Dependencies/provider secrets can be replaced; audit-chain algorithm migration and re-encryption runbook are absent. |
| V6.2.5 | No weak algorithms | met | [TOKEN](#evidence-index) | Platform CSPRNG/SHA-256/HMAC and constant-time comparisons; no custom cipher or obsolete application hashing. |
| V6.2.6 | Nonrepeating encryption nonces | N/A | [NO_FEATURE](#evidence-index) | No application-managed at-rest encryption/IV mechanism; ASP.NET cookie/data protection delegated to framework. |
| V6.3.1 | Unpredictable security randomness | met | [TOKEN](#evidence-index) | RandomNumberGenerator and framework/Guid.NewGuid identifiers; no predictable security bearer generation. |
| V6.3.2 | Secure random GUIDs | met | [TOKEN](#evidence-index) | RandomNumberGenerator and framework/Guid.NewGuid identifiers; no predictable security bearer generation. |
| V6.4.1 | Managed secret lifecycle | owner | [SECRET](#evidence-index) | Env/mounted files are fully implemented; Azure/HashiCorp adapter interface is documented-only, not remote key isolation. |
| V6.4.2 | Isolated cryptographic key operations | owner | [SECRET](#evidence-index) | Env/mounted files are fully implemented; Azure/HashiCorp adapter interface is documented-only, not remote key isolation. |

## V7 — Error Handling and Logging

| Requirement | Focus | Status | Evidence | Scope / remaining work |
|---|---|---|---|---|
| V7.1.1 | No credential/payment logging | met | [PRIVACY](#evidence-index) | Generic provider errors, credential redaction and PII log policy; synthetic sentinel tests cover logs/readiness/exceptions. |
| V7.1.2 | No private-data logging | met | [PRIVACY](#evidence-index) | Generic provider errors, credential redaction and PII log policy; synthetic sentinel tests cover logs/readiness/exceptions. |
| V7.1.3 | Security-event coverage | partial | [AUDIT](#evidence-index) | Mutation audit exists; comprehensive authentication/denial monitoring and remote alerting are incomplete. |
| V7.1.4 | Investigation event context | partial | [AUDIT](#evidence-index) | Mutation audit exists; comprehensive authentication/denial monitoring and remote alerting are incomplete. |
| V7.2.1 | Authentication decision logs | partial | [AUDIT](#evidence-index) | Mutation audit exists; comprehensive authentication/denial monitoring and remote alerting are incomplete. |
| V7.2.2 | Authorization decision logs | partial | [AUDIT](#evidence-index) | Mutation audit exists; comprehensive authentication/denial monitoring and remote alerting are incomplete. |
| V7.3.1 | Log injection defenses | met | [INPUT](#evidence-index) | Control-character rejection, structured logging and fixed generic failure codes prevent raw log-line injection. |
| V7.3.3 | Log access and integrity protection | partial | [AUDIT](#evidence-index) | API audit protected by role/tenant and hash verification; host root can rewrite chain; external anchoring pending. |
| V7.3.4 | Synchronized UTC timestamps | owner | [AUDIT](#evidence-index) | UTC TimeProvider timestamps used; host time synchronization and provider-skew monitoring require deployment setup. |
| V7.4.1 | Generic unexpected error response | met | [INPUT](#evidence-index) | ProblemDetails and central exception handling; production responses omit stack traces and provider bodies. |
| V7.4.2 | Expected and unexpected error handling | met | [INPUT](#evidence-index) | ProblemDetails and central exception handling; production responses omit stack traces and provider bodies. |
| V7.4.3 | Final unhandled-exception handler | met | [INPUT](#evidence-index) | ProblemDetails and central exception handling; production responses omit stack traces and provider bodies. |

## V8 — Data Protection

| Requirement | Focus | Status | Evidence | Scope / remaining work |
|---|---|---|---|---|
| V8.1.1 | Server/proxy cache protection | owner | [HEADER](#evidence-index) | Application cache policy exists; ingress/CDN caching and host temporary/backup ACLs must be configured and verified. |
| V8.1.2 | Protected temporary data | owner | [HEADER](#evidence-index) | Application cache policy exists; ingress/CDN caching and host temporary/backup ACLs must be configured and verified. |
| V8.1.3 | Minimal request metadata | met | [PRIVACY](#evidence-index) | Optional contact data, purpose/consent and narrow DTOs; staff cookie and CSRF token only as needed. |
| V8.1.4 | Abnormal-request detection and alerts | partial | [RATE](#evidence-index) | Requests are rate limited; centralized anomaly alerts are not implemented. |
| V8.2.1 | Sensitive-response cache prevention | met | [HEADER](#evidence-index) | Sensitive API/session/export responses use no-store; service worker caches shell assets only. |
| V8.2.2 | No sensitive browser persistent storage | partial | [UI](#evidence-index) | Staff server cookies are opaque; intentional legacy token UI compatibility requires operational caution, no case cache. |
| V8.2.3 | Clear client state at session termination | partial | [UI](#evidence-index) | Logout clears credentials and UI state; session timeout/browser-back residual DOM requires continuing browser review. |
| V8.3.1 | Private data outside URLs | met | [INPUT](#evidence-index) | Resident contact/narrative/credentials supplied in bodies or headers; case reference in URL is minimal status locator. |
| V8.3.2 | Data export/removal workflow | partial | [PRIVACY](#evidence-index) | Admin identity-verified JSON export/correction/anonymization with audit; external provider/backup erasure requires operator action. |
| V8.3.3 | Clear notice and opt-in | met | [PRIVACY](#evidence-index) | Versioned plain-language intake purpose/consent recorded; optional contact consent is separate. |
| V8.3.4 | Classified sensitive data policy | met | [PRIVACY](#evidence-index) | POPIA mapping identifies data classes, processing purposes, redaction, retention and owner responsibilities. |
| V8.3.5 | Audit sensitive-data access | partial | [PRIVACY](#evidence-index) | DSR exports/mutations and workflow changes audited; every sensitive read is not comprehensively logged. |
| V8.3.6 | Clear sensitive process memory | partial | [SECRET](#evidence-index) | Managed strings cannot guarantee memory zeroization; limit retention and prohibit production process dumps. |
| V8.3.7 | Authenticated encryption of private data | owner | [DEPLOY](#evidence-index) | At-rest encryption, TLS and backup key management need deployment verification. |
| V8.3.8 | Scheduled classified retention | partial | [PRIVACY](#evidence-index) | Configurable contact/case retention and audit tombstones; workforce/provider/backup policies remain owner responsibility. |

## V9 — Communication

| Requirement | Focus | Status | Evidence | Scope / remaining work |
|---|---|---|---|---|
| V9.1.1 | TLS-only client access | owner | [DEPLOY](#evidence-index) | Production ingress, TLS/ciphers, revocation, internal links and monitoring must be tested after deployment. |
| V9.1.2 | Strong prioritized cipher suites | owner | [DEPLOY](#evidence-index) | Production ingress, TLS/ciphers, revocation, internal links and monitoring must be tested after deployment. |
| V9.1.3 | Current TLS protocol versions | owner | [DEPLOY](#evidence-index) | Production ingress, TLS/ciphers, revocation, internal links and monitoring must be tested after deployment. |
| V9.2.1 | Trusted peer certificates | owner | [DEPLOY](#evidence-index) | Production ingress, TLS/ciphers, revocation, internal links and monitoring must be tested after deployment. |
| V9.2.2 | TLS on every sensitive component link | owner | [DEPLOY](#evidence-index) | Production ingress, TLS/ciphers, revocation, internal links and monitoring must be tested after deployment. |
| V9.2.3 | Authenticated encrypted external connections | owner | [DEPLOY](#evidence-index) | Production ingress, TLS/ciphers, revocation, internal links and monitoring must be tested after deployment. |
| V9.2.4 | Certificate revocation checks | owner | [DEPLOY](#evidence-index) | Production ingress, TLS/ciphers, revocation, internal links and monitoring must be tested after deployment. |

## V10 — Malicious Code

| Requirement | Focus | Status | Evidence | Scope / remaining work |
|---|---|---|---|---|
| V10.2.1 | No hidden data collection | partial | [PROVIDER](#evidence-index) | Unconfigured modes disabled/simulated; configured integrations perform disclosed processing; live provider privacy behavior unverified. |
| V10.2.2 | Minimal sensor permissions | met | [HEADER](#evidence-index) | Microphone self permission only with configured voice; no camera/sensor requests by default. |
| V10.3.1 | Signed secure automatic updates | N/A | [NO_FEATURE](#evidence-index) | No application self-update agent; controlled source/deployment workflow only. |
| V10.3.2 | Trusted code with integrity protection | partial | [UI](#evidence-index) | Pinned SDK/widget bundles with SRI and CSP; full CDN worklet graph/live execution needs owner review. |
| V10.3.3 | DNS/subdomain lifecycle protection | owner | [DEPLOY](#evidence-index) | DNS expiry/subdomain takeover monitoring is hosting responsibility. |

## V11 — Business Logic

| Requirement | Focus | Status | Evidence | Scope / remaining work |
|---|---|---|---|---|
| V11.1.1 | Ordered business transitions | met | [WORKFLOW](#evidence-index) | Ordered lifecycle, optimistic versions, tenant quotas, qualification floor and human review tested. |
| V11.1.2 | Realistic transaction timing | partial | [RATE](#evidence-index) | Per-principal action limits exist; artificial human-delay enforcement is absent; automation explicitly allowed for intake/training. |
| V11.1.3 | Per-user business limits | met | [WORKFLOW](#evidence-index) | Ordered lifecycle, optimistic versions, tenant quotas, qualification floor and human review tested. |
| V11.1.4 | Automation abuse controls | met | [WORKFLOW](#evidence-index) | Ordered lifecycle, optimistic versions, tenant quotas, qualification floor and human review tested. |
| V11.1.5 | Domain-specific risk constraints | met | [WORKFLOW](#evidence-index) | Ordered lifecycle, optimistic versions, tenant quotas, qualification floor and human review tested. |
| V11.1.6 | Sensitive-operation race protection | partial | [WORKFLOW](#evidence-index) | File gates/versions and PostgreSQL fresh-read transactions, optimistic versions and outbox leases prevent tested races. Process-local sessions/quotas and external provider deduplication remain boundaries. |
| V11.1.7 | Unusual workflow-event monitoring | partial | [AUDIT](#evidence-index) | Audit trail and refusal responses exist; real-time configurable abuse alerts are not implemented. |
| V11.1.8 | Configurable business alerts | partial | [AUDIT](#evidence-index) | Audit trail and refusal responses exist; real-time configurable abuse alerts are not implemented. |

## V12 — Files and Resources

| Requirement | Focus | Status | Evidence | Scope / remaining work |
|---|---|---|---|---|
| V12.1.1 | Upload size bounds | met | [INPUT](#evidence-index) | No general uploads; bounded webhook JSON/transcripts cannot exceed route limits, including chunked bodies. |
| V12.1.2 | Archive expansion bounds | N/A | [NO_FEATURE](#evidence-index) | No public file upload, archive extraction or user-supplied executable/file path feature exists. |
| V12.1.3 | Per-user file/storage quotas | partial | [RATE](#evidence-index) | Body/action quotas enforced; no fleet/per-tenant storage-byte quota, even though public uploads are absent. |
| V12.2.1 | Content-based file-type checks | N/A | [NO_FEATURE](#evidence-index) | No public file upload, archive extraction or user-supplied executable/file path feature exists. |
| V12.3.1 | No untrusted filesystem names | N/A | [NO_FEATURE](#evidence-index) | No public file upload, archive extraction or user-supplied executable/file path feature exists. |
| V12.3.2 | Local file inclusion prevention | N/A | [NO_FEATURE](#evidence-index) | No public file upload, archive extraction or user-supplied executable/file path feature exists. |
| V12.3.3 | Remote file inclusion prevention | N/A | [NO_FEATURE](#evidence-index) | No public file upload, archive extraction or user-supplied executable/file path feature exists. |
| V12.3.4 | Fixed safe download filenames | met | [INPUT](#evidence-index) | Evidence/data-subject JSON exports use server-generated attachment filenames; no user filename reflection. |
| V12.3.5 | No file-metadata command execution | N/A | [NO_FEATURE](#evidence-index) | No public file upload, archive extraction or user-supplied executable/file path feature exists. |
| V12.3.6 | Trusted executable dependencies | partial | [DEPENDENCY](#evidence-index) | Pinned NuGet/SDK/widget with audits and integrity; CDN nested asset graph and execution are owner-unverified. |
| V12.4.1 | Private upload storage | N/A | [NO_FEATURE](#evidence-index) | No public file upload, archive extraction or user-supplied executable/file path feature exists. |
| V12.4.2 | Malware scanning of uploads | N/A | [NO_FEATURE](#evidence-index) | No public file upload, archive extraction or user-supplied executable/file path feature exists. |
| V12.5.1 | Restricted static file extensions | met | [NO_FEATURE](#evidence-index) | Only bundled wwwroot assets exposed; data, secrets, Git and source paths outside web root. |
| V12.5.2 | Uploaded content never executable | N/A | [NO_FEATURE](#evidence-index) | No public file upload, archive extraction or user-supplied executable/file path feature exists. |
| V12.6.1 | Outbound system/resource allowlist | partial | [PROVIDER](#evidence-index) | Resident cannot choose outbound destinations; trusted owner model/provider config plus network egress review required. |

## V13 — API and Web Service

| Requirement | Focus | Status | Evidence | Scope / remaining work |
|---|---|---|---|---|
| V13.1.1 | Consistent encoding/parsers | partial | [INPUT](#evidence-index) | Shared UTF-8/System.Text.Json and canonical text validation; broad proxy/URL normalization review pending. |
| V13.1.3 | No API secrets in URLs | met | [TOKEN](#evidence-index) | No staff/service API credentials in application URLs; ephemeral provider signed URL is a documented provider contract exception. |
| V13.1.4 | Route and resource authorization | met | [RBAC](#evidence-index) | Route permission matrix plus tenant, crew assignment and hazard resource guards. |
| V13.1.5 | Expected request media types | met | [INPUT](#evidence-index) | Expected JSON media types, typed DTO validation, malformed/oversize/Unicode/header tests. |
| V13.2.1 | Method-specific permissions | met | [RBAC](#evidence-index) | Explicit method/route permissions and policy completeness checks; no blanket write grant. |
| V13.2.2 | Validated JSON contracts | met | [INPUT](#evidence-index) | Expected JSON media types, typed DTO validation, malformed/oversize/Unicode/header tests. |
| V13.2.3 | Cookie API CSRF | met | [CSRF](#evidence-index) | Antiforgery required for authenticated cookie writes; bearer/HMAC authentication is CSRF-exempt by design. |
| V13.2.5 | Explicit REST content type | met | [INPUT](#evidence-index) | Expected JSON media types, typed DTO validation, malformed/oversize/Unicode/header tests. |
| V13.2.6 | Protected transport/message integrity | partial | [PROVIDER](#evidence-index) | Webhook message signatures verified; transport TLS enforcement belongs to production ingress/model-host configuration. |
| V13.3.1 | XML schema plus field validation | N/A | [NO_FEATURE](#evidence-index) | REST JSON only; no SOAP/XML or GraphQL query API. |
| V13.3.2 | Signed SOAP payloads | N/A | [NO_FEATURE](#evidence-index) | REST JSON only; no SOAP/XML or GraphQL query API. |
| V13.4.1 | Bounded GraphQL query complexity | N/A | [NO_FEATURE](#evidence-index) | REST JSON only; no SOAP/XML or GraphQL query API. |
| V13.4.2 | Business-layer GraphQL authorization | N/A | [NO_FEATURE](#evidence-index) | REST JSON only; no SOAP/XML or GraphQL query API. |

## V14 — Configuration

| Requirement | Focus | Status | Evidence | Scope / remaining work |
|---|---|---|---|---|
| V14.1.1 | Repeatable secured build/deployment | partial | [DEPLOY](#evidence-index) | Repeatable build/Compose assets and locally tested file/PostgreSQL recovery; container execution, secured deployment and production recovery rehearsal remain pending. |
| V14.1.2 | Compiler/runtime memory safeguards | met | [DEPENDENCY](#evidence-index) | Managed runtime, latest-recommended analyzers and Release warnings as errors; only test fixture performance exceptions. |
| V14.1.3 | Hardened framework/server config | partial | [DEPLOY](#evidence-index) | Repeatable build/Compose assets and locally tested file/PostgreSQL recovery; container execution, secured deployment and production recovery rehearsal remain pending. |
| V14.1.4 | Rebuild and restore runbooks | partial | [DEPLOY](#evidence-index) | Repeatable build/Compose assets and locally tested file/PostgreSQL recovery; container execution, secured deployment and production recovery rehearsal remain pending. |
| V14.2.1 | Maintained dependency checks | met | [DEPENDENCY](#evidence-index) | Pinned maintained NuGet/npm packages with local advisory gate and trusted official package sources; future upgrades still required. |
| V14.2.2 | Remove unused production features | partial | [NO_FEATURE](#evidence-index) | Runtime static assets are restricted; owner must disable explicit demo, unnecessary endpoints/features and developer hosting. |
| V14.2.3 | External asset integrity | partial | [UI](#evidence-index) | SDK/widget pinned with SRI; internally loaded CDN worklet dependency integrity is not independently verified at runtime. |
| V14.2.4 | Trusted dependency sources | met | [DEPENDENCY](#evidence-index) | Pinned maintained NuGet/npm packages with local advisory gate and trusted official package sources; future upgrades still required. |
| V14.2.5 | Current software bill of materials | partial | [DEPENDENCY](#evidence-index) | Committed/CI CycloneDX inventory covers NuGet app/test and optional CDN bundles; runtime/OS/model/nested bundle inventory excluded explicitly. |
| V14.2.6 | Encapsulated third-party behavior | partial | [PROVIDER](#evidence-index) | HttpClient adapters bound payloads/timeouts and provider actions; process/container network sandboxing not runtime-tested. |
| V14.3.2 | Production debug features disabled | met | [HEADER](#evidence-index) | Central production ProblemDetails; Kestrel server header disabled; generic failures disclose no detailed versions. |
| V14.3.3 | No component-version disclosure | met | [HEADER](#evidence-index) | Central production ProblemDetails; Kestrel server header disabled; generic failures disclose no detailed versions. |
| V14.4.1 | Correct response content type/charset | partial | [INPUT](#evidence-index) | JSON/text/html types set for response bodies; empty/auth/SSE edge responses require continuous endpoint review. |
| V14.4.2 | Fixed API attachment filename | partial | [INPUT](#evidence-index) | Sensitive exports use fixed attachment filenames; routine interactive JSON API responses are inline by deliberate compatibility design. |
| V14.4.3 | Strict content security policy | met | [HEADER](#evidence-index) | Strict nonce CSP, nosniff, private referrer policy and frame-ancestors/X-Frame-Options tested; browser reports zero CSP violations. |
| V14.4.4 | MIME-sniffing prevention | met | [HEADER](#evidence-index) | Strict nonce CSP, nosniff, private referrer policy and frame-ancestors/X-Frame-Options tested; browser reports zero CSP violations. |
| V14.4.5 | HSTS and subdomain coverage | partial | [HEADER](#evidence-index) | Production HTTPS HSTS includes subdomains; owner must verify HTTPS on every municipality subdomain before deployment. |
| V14.4.6 | Private referrer policy | met | [HEADER](#evidence-index) | Strict nonce CSP, nosniff, private referrer policy and frame-ancestors/X-Frame-Options tested; browser reports zero CSP violations. |
| V14.4.7 | No unexpected third-party framing | met | [HEADER](#evidence-index) | Strict nonce CSP, nosniff, private referrer policy and frame-ancestors/X-Frame-Options tested; browser reports zero CSP violations. |
| V14.5.1 | Only intended HTTP methods | partial | [INPUT](#evidence-index) | Only mapped method handlers mutate state; method-denial alerting is not comprehensive. |
| V14.5.2 | Origin never an identity | met | [AUTH](#evidence-index) | Identity comes from authenticated issuer/bearer/session, never Origin or forwarding header. |
| V14.5.3 | Strict CORS origins | met | [HEADER](#evidence-index) | No permissive CORS registration; same-origin API, no null-origin grant. |
| V14.5.4 | Authenticate proxy/SSO supplied identity | met | [AUTH](#evidence-index) | SSO tokens validated by framework; caller-forwarded identity/municipality headers never authenticate a principal. |

## Required owner verification

Record actual issuer MFA/recovery/access policies, HTTPS/TLS/forwarding and DNS results, encrypted-volume/backup permissions, external provider/model processor terms, mounted-secret rotation, incident contacts/remote alerting, production restore exercises and the DPIA before a resident pilot. PostgreSQL transactions, signed local audit exports and file/PostgreSQL recovery drills are implemented and locally tested. File mode remains single-process; PostgreSQL uses a coarse database lock, while staff tickets and quotas remain process-local. Sign-in-time SSO role refresh, external delivery deduplication and externally retained audit checkpoints still require deployment controls. Signed HMAC exports do not defeat an attacker with the signing key or provide an external anchor by themselves. Full host/database compromise, provider copies, backup erasure, undiscovered free-text PII and model hallucination remain residual risks. See [storage boundaries](../operations/STORAGE.md), [signed exports](../operations/AUDIT_EXPORT.md), [backup/restore](../operations/BACKUP_RESTORE.md), [STRIDE](THREAT_MODEL.md), [POPIA](../privacy/POPIA.md) and [SECURITY.md](../../SECURITY.md).

All 259 requirement identifiers are retained. Only V5.3.4 and V5.3.5 change status in this refresh, from N/A to partial for the now-applicable PostgreSQL adapter; other assessment statuses remain unchanged. Reassess rows whenever a relevant feature changes; N/A changes to applicable as soon as uploads, XML, GraphQL, native code or local passwords are introduced. Maintain a separate current-standard assessment if an institution requires ASVS 5.x; this pinned review does not claim equivalence.
