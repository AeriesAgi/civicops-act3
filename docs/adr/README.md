# Architecture decision records

These MADR records explain the architecture present in CivicOps on 8 October 2026 (SAST). **Accepted** means the decision is reflected in the repository. It does not mean a municipality has approved a production deployment. The records reconstruct existing decisions; their dates identify this documentation round rather than inventing an earlier approval meeting.

Each record includes the problem, alternatives, decision, consequences and concrete implementation/test evidence. Local tests use synthetic records, fake providers or disposable infrastructure. Live providers, municipal SSO, deployed infrastructure and AMD performance require separate owner evidence. [Round 7 evidence](../ROUND7_STATUS.md) describes the tested baseline; [architecture](../ARCHITECTURE.md) describes the complete flow.

| Record | Decision |
|---|---|
| [0001 — Storage](0001-storage.md) | Preserve the zero-configuration file store; use opt-in PostgreSQL for transactional durable state. |
| [0002 — Audit](0002-audit-hash-chain.md) | Authenticate append-only events and retained snapshots with SHA-256 chains; export HMAC-authenticated bundles. |
| [0003 — Dispatch](0003-human-in-the-loop-dispatch.md) | Require authenticated human approval, eligibility checks and explicit assignment. |
| [0004 — Agents](0004-bounded-agents-and-fallback.md) | Limit agent permissions, schemas and budgets; retain deterministic fallback. |
| [0005 — AMD](0005-amd-vllm-reasoning.md) | Target vLLM/ROCm on AMD Instinct behind the OpenAI-compatible reasoning contract. |
| [0006 — ElevenLabs](0006-elevenlabs-channel-layer.md) | Keep conversational voice/text separate from municipal decision authority. |
| [0007 — Zendesk](0007-zendesk-outbox.md) | Deliver through a recoverable outbox with bounded retries and reconciliation. |
| [0008 — Tenancy](0008-tenancy-model.md) | Bind every operational request/store/stream to a trusted municipality. |
| [0009 — Identity](0009-oidc-and-rbac.md) | Use validated OIDC staff sessions, explicit route policies and scoped service accounts. |
| [0010 — Observability](0010-observability-stack.md) | Export redacted OpenTelemetry signals and distinguish liveness, readiness and provider degradation. |
| [0011 — Training](0011-training-extension.md) | Expose versioned, scoped training contracts without coupling CivicOps to rendering or curriculum. |

When a decision changes, add a superseding record and link it from the old record. Keep implemented controls distinct from proposed extensions and owner-operated controls. Source links identify behavior; test links identify the assertions that protect it.
