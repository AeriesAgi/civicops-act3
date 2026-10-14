# Round 9 — maps and live operations

Completed locally on 8 October 2026 on `grok/civicops-finish`. Nothing was pushed, no workflow was triggered or rerun, and no repository settings were changed. All scenarios and positions are synthetic; no provider, tile, identity-provider or model write API was used.

## Delivered by acceptance item

0. **CI hygiene:** read-only run/release review, current SHA-pinned Node 24 Actions, Ubuntu 24.04 on all jobs, unchanged owner GHAS opt-ins and clean actionlint. Both browser suites run when runner Chrome is present; temporary evidence uses runner temp. Expanded job budgets preserve every check on small runners.
1. **Map stack:** local Leaflet 1.9.4/markercluster 1.5.3, licences and verified integrity; repository canvas heat. Credential-free configurable tiles with exact-origin CSP, required OSM credit and documented tile-policy limits. Disabled/failed tiles retain styled jurisdiction outlines. Strict-CSP cancellation avoids data-image allowances.
2. **Geo model:** tenant-bound durable synthetic municipalities, six wards, consented case points, latest synthetic crew positions, twelve allow-listed object/situation hazards and six assets. Pure geofencing covers edges/holes and the regional envelope; named human jurisdiction overrides are audited. Corrupt geo affects readiness; case-backed map reads fail closed on invalid incident integrity.
3. **APIs:** twelve map operations, including paged/limited GeoJSON with bbox/layers/since/ETag, wards, aggregated case/hazard heat, bounded authenticated SSE, crew qualification detail, human-confirmed dispatch, reviewed hazards, polygon administration and explicit demo seed. OpenAPI has 89 paths/97 operations/119 schemas; two closed portable map schemas were added.
4. **Control room:** dark Live map and compact case map, clustered coloured/shaped priority/status markers, stale crew rings/ages, hazard radii, ward statistics, layers/time/search, click-through and distance/qualification review before human dispatch. Keyboard alternatives cover every feature. Authorization loss clears private state; refreshes preserve drafts and confirmed decisions.
5. **Scenario:** deterministic 120 cases, 25 matching crews, 10 hazards and six assets around fictional Demo Metro on the KZN coastline, labelled SYNTHETIC/SIMULATED. Explicit loading creates real local case records; the original 15-case demo remains unchanged.
6. **Verification:** 1,201 default / 1,267 PostgreSQL tests passed, zero failed/skipped; prior 1,024 / 1,086 retained. Unit/API/storage/tenancy/ethics guards, existing 169-check browser/workflow smokes and new 62-check offline map smoke passed. Exact commands and invocation failures are recorded in [verification](VERIFICATION.md).
7. **Captures:** inspected actual browser renders: [control room](media/map-control-room.png), [heatmap](media/map-heatmap.png), [ward hover](media/map-ward-hover.png), [case detail](media/map-case-detail.png). Every PNG is below 1 MB; routine smoke leaves tracked captures unchanged.

## Ethics, privacy and operating limits

[Responsible AI](RESPONSIBLE_AI.md) maps the non-negotiable rules to code and tests. No facial/biometric identification, person/neighbourhood risk scoring, predictive policing, stored video frames or live worker tracking is implemented. Worker ingestion stays disabled even for callers asserting shift/consent/indicator. Coordinates require an explicit report-sharing choice with a versioned purpose notice. Incidents/hazards, never people, supply heat density. Authorized humans approve safety and dispatch actions; actors/reasons/times are audited.

Automatic hourly retention removes report coordinates after 30 days, synthetic latest crew positions after 24 hours and hazards after seven days. Reads exclude expired data immediately. Access logging omits coordinates/narratives/contacts. Local anonymisation removes current coordinates; historical audit/backups/provider records/prior exports retain the existing documented limits.

Nearest distance is straight-line, not an ETA or an arrival promise. Real geometry, tiles, certifications, municipal notices/retention and production acceptance remain owner-pending. Production asset import/provisioning needs engineering. Metro Police/traffic, Fire, EMS, Disaster Management, SOS, Android consent workflows, camera adapters and agency-specific training are future rounds. Real AMD execution, live channels/SSO, container/cluster execution and submission publication remain unverified. File writes are single-process, with no cross-document geo/audit transaction or automatic missing-mirror repair; PostgreSQL geo decisions and mirrored audit writes are transactional. SSE/sessions/quotas remain single-active-instance.

## New configuration

Both examples contain only blank placeholders; the complete inventory is 144 settings.

| Variable | Default |
|---|---|
| `CIVICOPS_MAP_TILE_URL` | `https://tile.openstreetmap.org/{z}/{x}/{y}.png`; `none` disables tiles |
| `CIVICOPS_MAP_ATTRIBUTION` | `© OpenStreetMap contributors` |
| `CIVICOPS_MAP_CREW_STALE_MINUTES` | `15`; accepted range 1–240 |

No environment variable enables live worker/camera ingestion. [Map guide](MAPS.md) describes configuration guards, OSM policy and API limits. [Enterprise readiness](ENTERPRISE_READINESS.md) remains the current implementation/acceptance matrix.
