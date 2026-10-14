# LabLab ACT III submission draft

This draft uses the owner's event brief: online build 12–18 October 2026, submission deadline Sunday 18 October 2026 at 17:00 SAST (15:00 UTC). Confirm the portal's exact fields, track names and deadline before submitting. The official event page confirms 12–18 October but lists the detailed schedule as to be announced; the 17:00 SAST cutoff remains owner-brief information, not independently confirmed. Reviewed the [official event brief](https://lablab.ai/ai-hackathons/amd-developer-hackathon-act-iii) on 8 October 2026 SAST. No submission, signup, publication, deployment or provider write was performed.

## Build-window disclosure

Rounds 1–8, completed on 8 October 2026 SAST, predate the 12–18 October online build window. They include the full synthetic workflow, provider adapters, bounded agents, polished multilingual dashboard, municipality identity/RBAC, tenant isolation, catalogue/workforce/training API, security/privacy controls, PostgreSQL storage, tested recovery/telemetry, statically validated deployment assets, operational handover and screenshots. [Round history](../CHANGELOG.md) and [dated local verification](VERIFICATION.md) identify that baseline. Disclose this existing code to organizers and confirm eligibility; do not represent it as event-week work.

Rounds 9–11 (8–9 October 2026) also predate the window: maps, crew apps and unified voice/SMS/WhatsApp intake. Work inside the 12–18 October window, each with dated commits on `grok/civicops-finish`:

- Round 12 (12–13 October): resident, crew PWA and Android SOS with a bounded location window, control-room acknowledge/assess/dispatch/resolve, keyword and USSD adapters and emergency-number notices.
- Round 13 (13 October): nine agencies with agency roles and Metro Police redaction, an additive multi-agency incident catalogue, cross-agency incidents with ICS-lite command and human-decided mutual aid, and a simulated scene-event camera adapter with an immutable allow-list. Agency board and camera feed screens.
- Round 14 (13 October): judge's-eye polish ([backlog](POLISH_BACKLOG.md)), refreshed screenshots and [contact sheet](media/CONTACT_SHEET.md), updated readiness and verification evidence.

**AMD status:** no AMD endpoint was provisioned and the owner has written off AMD credits for this event. The AMD/vLLM adapter and benchmark exist but have **not** run on AMD hardware. Every recording and description must show the `deterministic-fallback` provider label and say "simulated" for reasoning. The organizers' brief asks for a meaningful AMD workload; the owner decides whether to submit on that basis. No deployment or live provider validation has happened.

## Title

**Culltron CivicOps: a conversational front door for municipalities, with a human decision at every dispatch**

## Short description

CivicOps gives municipalities one path from multilingual resident reports to qualified crews. Bounded agents triage and draft; humans approve dispatch and updates. Staff SSO/RBAC, tenant isolation, durable delivery and verified audit preserve accountability. SOS, multi-agency incidents with mutual aid and a privacy-bounded simulated camera feed share the same human-decision model. AMD reasoning and ElevenLabs/Zendesk adapters are implemented; none has been validated live.

## Long description

Municipal and utility service desks need a reliable path from a resident's words to an accountable response. CivicOps accepts voice, web chat, forms and Open311 reports, gives the resident a reference, and keeps the conversation, model reasoning, ticket and dispatcher decision together.

ElevenLabs provides the resident/callback agents, authenticated server tools, synthetic municipal knowledge, multilingual presets and signed post-call transcription. An open model served by vLLM on ROCm on AMD Instinct performs the backend agents' intake extraction, triage, optional duplicate confirmation, crew recommendation and resident update drafting. Every decision records its model/provider, confidence, rationale, latency and fallback state. An optional ElevenLabs custom LLM setting uses that same AMD endpoint as its conversational brain.

A dispatcher reviews each case, approves the response and assigns an eligible crew. Agents cannot approve, assign, merge duplicates or send unreviewed drafts. A deterministic urgent-hazard floor remains active even when a model disagrees. Zendesk receives durable ticket delivery through an integration outbox, with retries, dead letters, private provenance and explicitly approved public updates. Signed replies return to the audit for human review. The responsive dashboard shows the queue, SLA timers, channel mix, reasoning timeline, integrations and audit verification.

The enterprise control room adds municipality-bound identity/data, scoped service tokens, qualifications, an audited training extension API, versioned purpose consent and local privacy-request handling. PostgreSQL covers all durable entities; backup/restore, signed audit exports and OTLP signals have local drill evidence. Deployment remains one active application instance; sessions, quotas and SSE are process-local. The [readiness matrix](ENTERPRISE_READINESS.md) separates tested capabilities from live and operational gaps.

Culltron's own support desk is the intended first private live proof. The repository provides a runnable offline product, simulated integration fallbacks, automated tests, an end-to-end smoke, container assets and an AMD benchmark using production prompts. Actual AMD measurements and live provider evidence must be attached before the submission claims them.

## Suggested tags

`AMD`, `ROCm`, `vLLM`, `open-models`, `ElevenLabs`, `voice-agents`, `Zendesk`, `civic-tech`, `human-in-the-loop`, `service-operations`, `dotnet`

## Track fit and judging evidence

The owner should choose the main challenge based on the demonstrated user. **Reinvent Commerce** is the closest customer-support fit for a utility or Culltron service desk. **Intelligent Industry** could fit an industrial maintenance/crew use case if the event demo proves that user and outcome. The present municipal desk is an adjacent use case; confirm organizer fit. **Create a New Kind of Experience** becomes relevant only if an eligible experience is built during the event; the future VR layer is not part of this build.

The optional **Evolus Business Agents on AMD** prize does not currently apply: CivicOps has no Evolus integration. Its workspace, AMD/vLLM main agent, two building blocks and API-use requirements would require separate eligible event-week work. The official brief requires a meaningful AMD workload in the judged working product; the present offline fallback does not satisfy that requirement. Confirm track fit, prior-work eligibility and portal requirements at kickoff.

| Judging criterion | Product evidence to show |
|---|---|
| Application of Technology | Live open-model inference on AMD, structured-output validation, explicit agent scopes/provenance, ElevenLabs server tools and signed post-call integration, Zendesk ticket delivery |
| Business Value | One reference across channels, review queue, eligible assignment, status updates and visible delivery failures; intended Culltron desk pilot |
| Originality | Conversation-to-ticket-to-human handoff with recorded AI reasoning and an independent safety floor, with human authority at each consequential decision |
| Presentation | Three-minute end-to-end recording, clear architecture, real benchmark evidence and candid labels for simulated components |

Do not invent reduced response times, cost savings, adoption numbers or impact metrics. The synthetic workflow is demonstrable; institutional impact needs measured pilot evidence.

## Owner deliverables before submission

- Provide the AMD Developer Cloud host/access, selected open model, protected endpoint and exact hardware/provider label. Run the benchmark and capture device, ROCm, image digest and a live reasoning decision.
- Provide the existing ElevenLabs grant key, allowed voice/model, workspace tool/optional LLM secret references, public HTTPS tool/webhook base, signing secret, agent ids and optional phone-number id/consented callback settings.
- Provide the existing Zendesk subdomain, eligible API authentication or OAuth token, webhook signing secret and verified trigger mapping. Test only synthetic ticket/reply data first.
- Complete [municipality onboarding](ONBOARDING_MUNICIPALITY.md): approved tenant/SSO roles, verified real workforce, private PostgreSQL/storage, HTTPS/proxy controls, Information Officer/DPIA/operator agreements, retention/backup and support staffing. Keep synthetic seed workforce out of live dispatch eligibility.
- Supply and verify the app URL, public GitHub URL after history clearance, demo video URL, slides, cover image and any team/owner profile fields requested by the portal.
- Re-run build, tests, smoke checks and both secret scans from the final submission commit; record exact counts, SAST dates and achieved-versus-proposed boundaries in [VERIFICATION.md](VERIFICATION.md).

## Links to fill at submission

| Field | Current state |
|---|---|
| GitHub | Canonical repository is `https://github.com/CulltronOrg/CivicOpspublic`; public visibility requires owner clearance |
| App URL | Not deployed; owner must supply |
| Video | Not recorded/uploaded; follow [DEMO_SCRIPT.md](DEMO_SCRIPT.md) |
| Slides | Owner-pending; not generated/published |
| Cover image | Owner-pending; not generated/published |
| AMD benchmark | Production-schema script ready; no actual AMD run or hardware attestation |
| Licence | MIT for this repository; separate provider/model terms apply |

## Judge walkthrough of the current baseline

Run the [one-command synthetic demo](../README.md#run-the-synthetic-demo), then show resident intake → recorded fallback reasoning → simulated Zendesk request → human approval → qualification-checked crew assignment → resolution → reviewed update → verified audit. Open draft history and the paired resolution audit disclosure. Show the [control room](media/control-room.png), [decision evidence](media/case-timeline.png) and [admin console](media/admin-console.png).

Use the [three-minute recording plan](DEMO_SCRIPT.md) and replace each simulated segment only after owner-run live synthetic validation. For a judge, separate repository capability, actual AMD measurements, real service delivery and measured municipal impact. The local k6 regression uses loopback and a shared VM; it provides no GPU, production-capacity or response-time-savings claim.
