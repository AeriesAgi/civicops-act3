# Multi-agency operations (round 13)

CivicOps now runs the shared operations backbone for utilities and safety agencies within one municipality. All catalogue content and demo data is **synthetic**. The hard ethics rules still apply: no facial recognition, no person-level risk scoring or predictive policing, no covert surveillance, and a human decides every police and safety action.

## Agencies and roles

`products/civicops/catalog/agencies.v1.json` defines nine agencies: Electricity, Water & Sanitation, Roads & Stormwater, Waste, Metro Police, Traffic, Fire & Rescue, EMS and Disaster Management. Each has a colour, an icon and agency role names such as `metro-police.dispatcher` or `fire.station-officer`. Every agency role maps onto an existing CivicOps base role (`dispatcher` or `supervisor`), so the permission matrix stays closed. Supervisors enable or disable agencies per municipality (`PUT /api/v1/agencies/{id}`); disabled agencies cannot join incidents.

**Restricted agencies.** Metro Police is marked restricted. Incidents that involve it return a redacted summary, no location and no timeline notes to staff outside that agency. Supervisors, auditors and admins, plus users who carry a `civicops:agency` claim for that agency, see the full record.

## Incident catalogue

The same file lists per-agency incident types (for example `TRAFFIC.CRASH_SCENE`, `FIRE.VEHICLE_EXTRICATION`, `EMS.MASS_CASUALTY`, `DISASTER.FLOOD_EVACUATION`) with hazards, PPE, skills, certifications (ICS 100/200, Hazmat awareness, Basic Life Support, Traffic point duty), SLA minutes and default priorities. It is additive: `work-orders.v1.json` is unchanged. `GET /api/v1/agencies/catalogue` exposes it so the VR training layer can map modules to it later.

## Cross-agency incidents and mutual aid

`POST /api/v1/agency-incidents` creates a parent incident that links agencies and optional existing case ids, with a shared timeline. Supervisors assign an incident commander with an ICS-lite role. Any participating agency can request mutual aid from another agency, or from a neighbouring municipality by tenant id, with a sharing scope of `Summary` or `SummaryAndLocation`. A supervisor accepts or declines each request; the decision, actor and note are recorded. Cross-municipality requests are **recorded only**: no data is sent to the other municipality in this build. Every change is idempotent by `operationId` and appended to the timeline with the acting human.

## Simulated traffic-camera adapter

`IIncidentDetector` delivers **scene events only**: `crash`, `stalled_vehicle`, `flooding`, `blocked_lane`, `debris`, `fire_smoke`, `signal_outage`, each with confidence, camera id, location and time. The allow-list is immutable at runtime. The request schema is closed, so any imagery, face, biometric, plate or tracking field is rejected with 400. `SimulatedCameraDetector` is a deterministic generator for demos (`POST /api/v1/detections/simulate`). Events below 0.7 confidence are flagged. A detection is only a proposal: a human confirms it (which creates an incident) or dismisses it. Detections never dispatch anyone. No real camera integration exists.

## Verification

`tests/CivicOps.Tests/AgencyTests.cs` covers the catalogue, permissions, redaction, tenant isolation, mutual aid, the allow-list and closed schema, and the human confirmation gate. `scripts/smoke_agencies.sh` runs the same flow against a live synthetic demo.

## Not in this build

Agency-specific dashboard screens, training packs per agency, live mutual-aid delivery between municipalities and any real camera feed remain future work.
