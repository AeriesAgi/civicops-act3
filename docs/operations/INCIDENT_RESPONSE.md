# CivicOps incident response

Reviewed **8 October 2026 (SAST, UTC+02:00)**. This is a municipal adoption procedure and template, **documented-only until owners assign responders, approve contacts and exercise it**. It sends no messages or statutory notices. Store actual incident records and personal information in the municipality's restricted system; the repository and local exercises use synthetic data only.

## Severity and accountable roles

| Severity | Trigger | Initial action / proposed update cadence |
|---|---|---|
| S1 Critical | Suspected personal-information exposure, cross-tenant access, compromised privileged credential, unexplained audit failure or widespread loss of durable intake | Activate incident commander, security lead and Information Officer immediately; contain affected writes/access; internal updates every 30 minutes |
| S2 High | Significant department/channel degradation, prolonged delivery backlog, storage instability with a working continuity route; no current evidence of unauthorized access | Duty operator and service owner coordinate recovery; security lead assesses scope; updates hourly |
| S3 Moderate | Limited reproducible defect with safe workaround and intact integrity | Record owner and workaround; update each business day |
| S4 Low | Minor defect, documentation/support request | Normal support queue; update at agreed milestones |

Treat uncertainty about exposure as S1 while investigating. A low availability severity never removes a privacy-reporting duty. [SLA_SUPPORT.md](../SLA_SUPPORT.md) contains proposed response/restoration targets; it is uncontracted.

| Role | Responsibility |
|---|---|
| Municipal service owner / incident commander | Own severity, continuity, resources, decision log and reopening approval; appoint a deputy |
| Duty operator / infrastructure-database owner | Isolate services, preserve storage, restore verified infrastructure and report fresh probes/queue evidence |
| Security lead | Scope unauthorized access, preserve forensic evidence, coordinate credential containment and validate fixes |
| Registered Information Officer / deputy | Assess notification obligations, own Regulator/data-subject submission and receipt, coordinate lawful evidence/retention decisions |
| Dispatch supervisor | Maintain human hazard/assignment decisions and approved manual service continuity; account for delayed cases |
| Identity/provider administrators | Authorized account containment/rotation and provider-side evidence/reconciliation; act through existing municipal accounts |
| Communications lead / municipal legal adviser | Prepare accurate accessible notices, approve audience/channel and legal advice without delaying required notification |
| Scribe | Record SAST and UTC timestamps, facts versus hypotheses, affected scope, decisions, evidence hashes and next update |

Before go-live, fill a protected contact sheet for each role, after-hours deputy, hosting/IdP/provider escalation, Information Regulator portal access and the municipality's public status channel. No live on-call roster, portal account or alert receiver is provisioned here.

## Detection, containment and recovery

1. Open an incident reference and record detection/discovery time, reporter, sanitized signal, tenant/service scope, severity and next update. Confirm independent health/intake probes; collect safe correlation IDs, provider mode/backlog counts and audit verification results. Gauges retained during storage failure are not fresh evidence.
2. Contain according to scope: restrict affected ingress/access, revoke a scoped CivicOps service account, stop affected writes/application or have the authorized issuer/provider owner revoke compromised access. Capture relevant evidence before routine restart where practicable. Do not enable demo access or remove live Zendesk credentials to force simulation; see [RUNBOOK.md](RUNBOOK.md).
3. Preserve restricted storage snapshots/database backups, current signed audit bundles and manifests, migration/image versions, safe application/ingress/issuer/provider logs and the chronological decision log. Record custodian, hash, source, collection time and every transfer. Encrypt/access-control the evidence outside this repository. Do not edit chain hashes or purge evidence; legal holds require an external owner workflow because no per-record legal-hold control is implemented.
4. Determine exposed data categories/subjects, tenancy boundaries, access window, compromised permissions and any upstream/downstream copies. Direct voice reaches ElevenLabs before backend redaction; audit/history/backups/exports may still contain historic personal information. A backend redactor or local anonymisation is not proof those copies are safe.
5. Correct the cause, rotate affected credentials, patch/reconfigure or restore a verified backup into an empty target. Stop all writers for restoration. Assess and record backup age/data-loss implications and provider intents requiring reconciliation. Follow [BACKUP_RESTORE.md](BACKUP_RESTORE.md); an image rollback does not reverse migrations.
6. Validate readiness, intended modes, tenant/role denials, each affected audit chain, synthetic consented human workflow and queue reconciliation. Security lead and Information Officer assess residual exposure; commander authorizes reopening and records deferred actions. Preserve required notification and affected-resident support even after technical recovery.

## POPIA notifications and timelines

Under §21(2), an operator informs the responsible party **immediately** on reasonable grounds of unauthorized access/acquisition. Under §22, the responsible party notifies the Regulator and identifiable affected data subjects **as soon as reasonably possible after discovery**; POPIA sets no general 72-hour deadline. Timing takes account of legitimate law-enforcement needs and measures reasonably needed to scope the compromise and restore system integrity. Only the relevant public investigating body or Regulator can determine that data-subject notification should be delayed because it would impede a criminal investigation. Document that determination. Written subject notices use post, email, a prominent municipal website notice, media or a Regulator-directed channel. Include consequences, containment/remedial steps, protective advice and the unauthorized person's identity if known. [POPIA §§21–22](https://www.justice.gov.za/legislation/acts/2013-004.pdf).

The Regulator requires reporting through its **eServices Security Compromises portal**, mandatory since **1 April 2025**. The Information Officer/deputy submits through the municipality's existing authorized account and retains the acknowledgement/reference. Do not use obsolete email/form submission instructions. If the portal is unavailable, record the outage and attempted submission, seek the Regulator's current assistance route and complete submission as soon as possible; an internal ticket is not a filed notice. [Regulator reporting announcement](https://inforegulator.org.za/wp-content/uploads/2025/04/MEDIA-STATEMENT-INVITATION-TO-REPORT-SECURITY-COMPROMISES-THROUGH-THE-eSERVICES-PORTAL-.pdf), [eServices portal](https://eservices.inforegulator.org.za/compromises/default.aspx).

The Regulator advises using available facts and updating later; the investigation need not be complete. Its guidance does not allow a low-risk reporting exemption. [Regulator security-compromise fact sheet](https://inforegulator.org.za/wp-content/uploads/2025/08/Fact-Sheet-on-security-compromises-2.pdf).

The following are **proposed internal escalation targets**, not legal grace periods. Notification proceeds sooner whenever reasonably possible; weekends/support hours do not suspend statutory duties.

| Since discovery | Internal target / recorded evidence |
|---|---|
| Immediately | Operator informs responsible party/Information Officer; record discovery and receipt times. Commander starts containment and comms log |
| First hour | IO/security lead begin reasonable-grounds assessment, identify subjects/categories and draft initial notices; record known facts, unknowns and the next review |
| First four hours | Aim to submit initial Regulator report and issue approved subject notice as soon as reasonably possible. Any remaining delay requires contemporaneous reasons, accountable IO and urgent review; four hours is not permission to wait |
| Every material change / at least daily while unresolved | Update affected parties/Regulator with new verified facts as appropriate; retain receipts, channels and notices. Record any subject whose identity cannot be established and practical notice measures |
| After containment | Confirm delivery/publication, support requests, remedial commitments and follow-up reporting; retain evidence under approved legal hold/retention |

The Information Officer determines the actual obligations with municipal legal advice. Other laws/contracts may impose additional reporting duties; record them separately. This runbook is not a compliance certification.

## Communication templates

Use approved municipal channels, reviewed translations and accessible formats. Do not disclose resident lists, secrets, raw transcripts or exploit instructions in operational updates. Never assert “no data affected” before evidence supports it.

**Internal activation:** “Incident [reference], severity [S1–S4], discovered [SAST/UTC]. Confirmed impact: [service/tenant scope and safe facts]. Exposure assessment: [known/unknown]. Commander [role/contact in protected roster]; Information Officer notified [time]. Actions [containment/workaround]; decision needed [owner/action]. Next update [time/channel].”

**Public service update:** “The municipality is investigating [service interruption] discovered [time]. [Available municipal alternative] remains available. Existing references [verified status/what residents should do]. Current action [plain-language step]; next update [time]. Contact [approved service desk]. Immediate danger must use the published emergency route.”

**Information Officer reporting worksheet:** incident reference; responsible party/Officer details; operator and processing flow; discovery/occurrence window; grounds and categories; approximate subjects/records and identification limits; consequences; containment/protection; subject-notice channel/time; reasons for any delay and lawful determination; remaining unknowns; next supplement; restricted evidence references. Enter the current portal's required fields, retain its receipt, and update it as facts change.

**Data-subject notice:** “On [discovery date], [municipality] identified [verified compromise and affected information categories/window]. Possible consequences are [specific harms]. We have [containment] and will [remedial steps]. You can protect yourself by [relevant practical steps; no request for passwords/payments]. The unauthorized person is [known identity / not currently known]. For assistance contact [Information Officer/approved channel]. Further verified information will be provided [channel/time]. [Regulator reference if available].” The Officer checks this against the §22 content and channel requirements before release.

## Post-incident review template

Hold a proposed review within **five business days of stabilization**, without delaying notices or corrective work. Store it in protected municipal records and publish only a privacy-reviewed summary when approved.

| Review field | Completion |
|---|---|
| Incident / commander / review date / participating roles | [owner completion] |
| Impact, affected flows/tenants/data and uncertainty | [owner completion] |
| Timeline: occurrence, discovery, acknowledgement, containment, IO/operator notices, Regulator receipt, subject notice, restoration | [SAST and UTC; evidence references] |
| Root cause / contributing conditions / detection gaps | [facts with evidence, separately identified hypotheses] |
| SLO/error-budget impact; backup age, actual loss and recovery duration | [measured values; unknown where unmeasured] |
| Why controls failed or helped; verified tenant/audit/provider outcomes | [evidence] |
| Notification assessment and any delay/determination; resident support/corrections | [IO approval and restricted receipts] |
| Corrective/preventive actions | [one accountable owner, priority, due date and acceptance test per action] |
| Closure and follow-up | [service owner / security lead / IO approval, unresolved risks, next drill date] |

Validate the procedure at least before pilot and after material tenancy/provider/privacy changes using a synthetic outage and a tabletop security compromise. Existing [restore](../../scripts/smoke_restore.sh), [privacy/security](../../scripts/smoke_security_privacy.sh), [telemetry](../../scripts/smoke_telemetry.sh) and [audit export](../../scripts/smoke_audit_export.sh) smokes validate software controls; they do not exercise real staff comms, statutory submission or production disaster recovery.
