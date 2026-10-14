# 0003 — Humans approve and assign municipal work

- Status: accepted; municipal operating procedures and qualification validation remain owner-pending.
- Date recorded: 2026-10-08 (SAST).
- Scope: case lifecycle, crew assignment and resident draft publication.

## Context and Problem Statement

A resident report may contain a hazardous situation, an unverified location or incomplete facts. Agents can help organize this material, but confidence is not authority to dispatch a crew, attest its qualifications or promise an arrival time. New information must not silently inherit an earlier approval.

## Decision Drivers

- Keep accountable staff identity on consequential actions.
- Prevent bypass of approval, qualifications and case-version checks.
- Escalate urgent/high-hazard work to supervisor authority.
- Preserve human review when new facts or work-order overrides change the decision.

## Considered Options

- Let agents approve, assign and send their own recommendations.
- Require staff to re-enter all intake and triage manually.
- Let agents draft/recommend while authenticated humans control approval, assignment and publication.

## Decision Outcome

Choose bounded recommendations with explicit human commands. [IncidentCoordinator.cs](../../products/civicops/Services/IncidentCoordinator.cs) creates every case in `PendingApproval` with review required regardless of model confidence. Approval and assignment are separate operations. [IncidentWorkflow.cs](../../products/civicops/Services/IncidentWorkflow.cs) rejects direct assignment through a status update; `InProgress` needs an eligible assigned crew and recorded approval with no outstanding review.

[CrewEligibilityService.cs](../../products/civicops/Services/CrewEligibilityService.cs) checks department, availability, skills/levels and dated certifications against the shared catalogue. Capability gaps always block assignment; certificate gaps follow `CIVICOPS_CERT_POLICY`, defaulting to `block` outside explicit demo and `warn` in demo. Recommendation does not reserve or assign a crew. [EnterpriseAccessMiddleware.cs](../../products/civicops/Services/EnterpriseAccessMiddleware.cs) and [PermissionMatrix.cs](../../products/civicops/Services/PermissionMatrix.cs) enforce role, crew-resource and high-hazard supervisor checks; audit actors come from server-authenticated identity rather than submitted labels.

Work-order override clears stale approval, assignment and recommendation and requests renewed review. Additional information and signed provider replies can reopen review while preserving a valid current lifecycle position. [Program.cs](../../products/civicops/Program.cs) requires explicit approval of the current CommsAgent draft before its public Zendesk comment is queued. Old pending drafts are superseded. Safe resident status templates and private ticket synchronization remain separate system behavior; draft approval does not enable unrestricted SMTP delivery.

## Consequences

Staff retain decision authority and a reviewable trail. Optimistic versions may require refreshing a case when a concurrent action wins. Human review can become a bottleneck, so the municipality must staff queues and escalation routes rather than interpret catalogue targets as arrival promises.

The explicit demo's synthetic supervisor identity is visibly unverified. Production requires real staff access, approved work procedures, accurate crews/qualifications and operational training. CivicOps is not an emergency service and does not validate resident-supplied location or external certificate authenticity merely by accepting a record.

## Confirmation

Implemented and tested locally: [ClassifierWorkflowTests.cs](../../tests/CivicOps.Tests/ClassifierWorkflowTests.cs) enumerates transition boundaries; [WorkforceExtensionTests.cs](../../tests/CivicOps.Tests/WorkforceExtensionTests.cs) checks eligibility, certificate policy and renewed review after override. [RbacTests.cs](../../tests/CivicOps.Tests/RbacTests.cs) verifies role/resource restrictions; [ElevenLabsEndpointTests.cs](../../tests/CivicOps.Tests/ElevenLabsEndpointTests.cs) checks renewed hazard review. [smoke_lifecycle.sh](../../scripts/smoke_lifecycle.sh) exercises authenticated approval and assignment. See [RBAC](../RBAC.md) for the policy matrix and live identity limits.
