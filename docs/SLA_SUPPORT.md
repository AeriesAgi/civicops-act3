# CivicOps proposed SLA and support schedule

Reviewed **8 October 2026 (SAST, UTC+02:00)**. **Documented proposal; uncontracted and owner-pending.** No staffed support organization, support contact, achieved production availability or service-credit commitment is supplied by this repository. The municipality and hosting/support operator must sign a schedule, assign responders, deploy measurement/alerting and prove capacity/recovery before a resident pilot. The [readiness matrix](ENTERPRISE_READINESS.md) records the engineering evidence and gaps.

## Proposed support tiers and hours

| Tier | Proposed coverage | Included work / approval requirement |
|---|---|---|
| Municipal pilot | Monday–Friday **08:00–17:00 SAST**, excluding South African public holidays | Named municipal administrator, operational questions and reproducible defects; limited users/volume and an approved manual continuity channel. After-hours response is unstaffed unless separately agreed |
| Municipal enterprise | Same business hours for normal requests; **24×7 S1/S2 duty coverage** | Assigned duty operator/deputy, incident coordination, agreed infrastructure/provider escalation and periodic recovery/security review. Requires an actual funded roster and tested contact route |
| Managed critical service | Enterprise coverage plus an agreed deployment/capacity/recovery scope | Named incident commander, infrastructure ownership, measured SLO reporting and agreed maintenance windows; scope and remedies negotiated per municipality |

The municipal service desk receives resident requests; product/hosting support receives technical incidents through an owner-approved private ticket/phone channel. Never include raw resident narratives, contact details or tokens in ordinary support tickets. Use sanitized symptoms, environment/image version, opaque case/correlation references, provider modes and reproduction steps. CivicOps is not an emergency dispatch service.

## Proposed severity targets

Response means a qualified responder acknowledges ownership and starts triage; an automated ticket receipt is insufficient. Restore/resolve means the affected technical service works again or an approved safe workaround is accepted; permanent remediation is tracked separately. Targets start at receipt by the contracted channel and run only within the tier's covered hours, except enterprise/managed S1/S2 run continuously. Statutory breach notification duties apply independently of support hours.

| Severity | Impact | First qualified response | Target restoration / resolution | Update cadence |
|---|---|---|---|---|
| S1 Critical | Suspected privacy/tenant/privileged compromise, audit integrity failure or widespread loss of durable intake | **15 minutes** with enterprise/managed coverage | **4 elapsed hours** to safe restoration/containment or an approved continuity route; permanent-fix plan within 1 business day | Every 30 minutes |
| S2 High | Major department/channel degradation or prolonged live outbox delay with safe continuity and no evidence of exposure | **30 minutes** with enterprise/managed coverage | **8 elapsed hours** to restoration/workaround; permanent-fix plan within 2 business days | Hourly |
| S3 Moderate | Limited defect; integrity intact and workaround available | **4 business hours** | **3 business days** to resolution or agreed workaround | Each business day |
| S4 Low | Minor usability/documentation issue or general request | **1 business day** | **10 business days** to resolution or a dated delivery decision | Agreed milestones |

For pilot S1/S2 use the same covered-time response targets, with service-owner escalation to the municipal continuity procedure outside staffed hours. These targets are not a guarantee that compromised systems can safely reopen by a fixed hour. Escalate missed targets with cause, next action and a revised estimate; protect integrity and residents first. Disaster-recovery **RPO ≤15 minutes / RTO ≤60 minutes** are separate proposed infrastructure objectives requiring schedules/PITR and drills, as described in [BACKUP_RESTORE.md](operations/BACKUP_RESTORE.md); neither is established locally.

## Proposed availability commitment and measurement

For an agreed production deployment, the proposed intake commitment is **99.9% over a rolling 30-day window**, using exactly the eligible valid-attempt/durable-response denominator in [SLO.md](operations/SLO.md). Include failed independent probes, storage errors and provider-related intake failure; idempotency replays count as available responses. Separate demo traffic and malformed/unauthorized requests. Planned maintenance counts unless a published exception is explicitly agreed in advance. A `/health/live` 200 or degraded `/health/ready` 200 alone cannot establish intake availability.

The same proposed schedule tracks **triage p95 ≤30 seconds**, **95% of live Zendesk intents delivered within 5 minutes / 99% within 15 minutes**, and **100% verified audit chains with zero unexplained gaps**. Pending and dead-letter deliveries remain in the unmet delivery population. Missing/stale measurements are unknown, not success. Intake's 0.1% budget is approximately 43.2 minutes of equivalent downtime in 30 days; freeze discretionary reliability risk when the budget is exhausted, under the service owner's recovery plan.

Provider outages stay in intake measurement because fallback and durable queueing are part of the intended service. Live provider delivery has its own SLI; a simulated outbox/ticket is not delivered service. Municipal human review, crew arrival and repair deadlines are separate operational policies: catalogue SLA targets are examples awaiting municipal approval and cannot be marketed as a resident arrival promise.

## Scope, exclusions and escalation

The proposed scope covers the approved application version and single-active-instance deployment, its durable intake, authorization/tenant isolation, staff workflow, audit and configured integrations. Any exclusions must be signed and visible alongside the SLO report; they do not silently remove failures from the technical SLI.

| Outside this proposal unless separately contracted | Treatment |
|---|---|
| Municipal crew staffing, field repair, emergency response or resident connectivity | Municipal operational/customer-service ownership; no promised arrival or real-world outcome |
| Provider account procurement, telecom routing, legal notices, accreditation and identity verification | Owner approvals/workflows with their own agreements; support coordinates authorized escalations |
| Live AMD hardware performance, multi-node HTTP operation and unsupported custom extensions | Require separate engineering, capacity and acceptance evidence; present repo supports one active instance |
| Customer changes outside reviewed configuration, unapproved network/IdP policy or unsupported versions | Triage remains available under agreed coverage; repair scope and cost require owner decision, with technical impact still reported |
| Force majeure or explicitly agreed maintenance exceptions | Define narrowly in the signed schedule and disclose excluded duration/impact; no blanket provider-outage exclusion for intake |
| Service credits, penalties, insurance or contractual liability | Negotiated legal/commercial terms; none are implemented or promised here |

Escalation proceeds from municipal service desk → duty operator → incident commander/service owner → hosting/identity/provider owner. Security/privacy events notify security lead and Information Officer immediately; use [INCIDENT_RESPONSE.md](operations/INCIDENT_RESPONSE.md) for statutory assessment and comms. At an unmet response/restoration target or audit failure, escalate to the commander without waiting for the next normal update. Owners fill names/deputies/contact routes and test them before go-live.

A monthly service review records eligible attempt counts, availability/error budget, triage/delivery tails with pending age, fresh tenant audit verification, incidents, backup/restore drills, measurement gaps and improvement owners. Actual production measurements, funded support tiers, signed commitments and remedies remain owner-pending.
