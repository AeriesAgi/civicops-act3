# Live municipal operations map

Round 9 adds an authenticated control-room map and a compact map in case detail. The map uses the same tenant-bound incidents, approval workflow, workforce qualifications and audit as the queue. **Demo Metro, its wards, sites, case locations and crew positions are SYNTHETIC.** The shapes sit near the real KwaZulu-Natal coastline for geographic context; they are not real municipal boundaries or addresses. No operational validity is claimed for these exercise coordinates.

## Local demonstration and offline mode

```bash
CIVICOPS_DEMO_MODE=true CIVICOPS_MAP_TILE_URL=none dotnet run --project products/civicops --urls http://localhost:5080
```

Open **Live map**, then choose **Load evening scenario**. Loading the map itself does not create cases. The separate map scenario uses a fixed random seed to create 120 case records, 25 synthetic crew positions and 10 structured hazards. The existing 15-case walkthrough remains available through its original button. Cases on the map open the ordinary authenticated case detail, and subsequent workflow changes are reflected on the map.

With `CIVICOPS_MAP_TILE_URL=none`, the map draws synthetic municipality/ward outlines on a styled background. Tile errors switch to the same outline view. This fallback needs no tile server, API key or runtime CDN. It does need a running CivicOps server for fresh operational data: the service worker caches the interface, never cases, locations or tiles. A disconnected map cannot claim that its operational state is live.

The browser uses vendored Leaflet 1.9.4 (BSD-2-Clause) and Leaflet.markercluster 1.5.3 (MIT), with upstream licence files and package integrity records under `products/civicops/wwwroot/vendor/`. Heat rendering is a small repository-owned canvas layer using server-aggregated cells.

## Tiles and configuration

| Setting | Default | Bounds and purpose |
|---|---|---|
| `CIVICOPS_MAP_TILE_URL` | `https://tile.openstreetmap.org/{z}/{x}/{y}.png` | `none`, or one HTTPS host with `{z}`, `{x}`, `{y}` placeholders; no credentials, queries, fragments, subdomain templates or nonstandard ports |
| `CIVICOPS_MAP_ATTRIBUTION` | `© OpenStreetMap contributors` | Configured plain text at most 240 characters; OSM credit is appended if absent when using the standard OSM host |
| `CIVICOPS_MAP_CREW_STALE_MINUTES` | `15` | Integer 1–240; stale positions fade and cannot qualify for nearest-crew suggestions |

Only the validated tile origin is added to CSP `img-src`. Script, connection and inline-style permissions remain unchanged. Tile images send an origin-only referrer; application-wide `no-referrer` remains in effect for other requests. API configuration includes no credentials.

The [OSMF standard tile policy](https://operations.osmfoundation.org/policies/tiles/) requires visible attribution, browser identification/referrers and normal HTTP caching. CivicOps requests only the visible viewport, with no tile prefetch, downloads, cache bypass or offline tile archive. QA blocks external traffic and uses outline-only rendering. Standard tiles have limited, best-effort availability and no production SLA. A municipality should provide a self-hosted or contracted tile service with suitable capacity, privacy terms and support; review its licence and attribution before enabling it. Outline fallback is an operating contingency, not a downloaded OSM basemap.

## Data, privacy and human decisions

Report coordinates are optional and require an explicit choice to share them for that report. CivicOps does not read browser geolocation or track a resident. Point-in-polygon geofencing assigns municipality and ward when a volunteered point falls within configured polygons. An unlocated or out-of-area report remains unassigned; textual areas are not converted to invented coordinates. Exterior polygon edges belong to the polygon; holes, including hole edges, are excluded. Shared edges use stable identifier order. The accepted regional bounding box is longitude 16–33, latitude −35–−22, with no antimeridian crossing; it is a validation envelope, not a claim of national-boundary accuracy.

Round 10 adds a separate [crew API and client](CREW_APP.md) for consented on-shift pings. The server verifies current shift and consent records; ordinary map mutations still cannot inject worker positions. Supervisors see current crew-app positions with on-shift/sharing state and last-seen age. Workers can inspect/export their own retained history. Ended/expired shifts and withdrawn consent remove live positions. The evening scenario and crew-app verification pings remain explicitly synthetic in demo. There is no covert surveillance or continuous tracking.

The heatmap aggregates **case or hazard events** into roughly 0.008-degree grid cells for 24 hours or seven days. It never consumes crew/resident/person location feeds and does not predict crime. Low-count cells still describe operational incidents; access remains restricted to authorized municipal operations staff.

Nearest-crew suggestions filter department, availability, current skills and valid certifications, then compare straight-line distance from fresh synthetic crew positions. They are advisory; distance is not driving time, an ETA or proof that a crew can reach a site. Assignment requires an already reviewed/approved case, current version, named authorized human and an explicit reason. High-hazard supervisor requirements remain in effect. A suggestion cannot approve or assign a case.

Structured hazard types are restricted to reviewed object/situation events. A named supervisor records the safety review. Frames, face data, biometric identifiers, plates linked to people and person/neighbourhood risk scores have no supported schema or endpoint. No camera detector or police-action integration is implemented. See [Responsible AI boundaries and guard tests](RESPONSIBLE_AI.md).

Map reads are authorization-checked and logged without coordinates, narratives or contacts. Case-backed layers, ward statistics, heat and nearest suggestions verify the incident chain and snapshot before projection; tampering produces a value-free 503 rather than unverified positions. The legacy audit/queue inspection paths remain available for investigation. Geo configuration/hazards have a tenant-specific hash chain mirrored into the incident audit. PostgreSQL commits those mutations and mirrors together; file mode replaces each document atomically in sequence, and does not provide a cross-document transaction or automatic repair of a missing mirror after a crash. The geo chain remains the durable record of the decision. Readiness fails on geo-store corruption while process liveness remains available. Crew points older than 24 hours and hazards older than seven days are automatically purged hourly; reads exclude expired records. Report coordinates expire after 30 days by default, independently of optional closed-case retention, and local privacy anonymisation removes current coordinates. Historic audit, backups and prior exports remain subject to the established privacy limits.

## Acceptance limits

This map is a locally tested implementation. Real municipal polygons/assets, approved coordinate notices/retention, production tile capacity and live operation remain owner-pending. An authenticated, audited polygon administration API is implemented; production asset import/provisioning is additional engineering, with only synthetic assets seeded. [Round-10 crew clients](CREW_APP.md) add shift/consent location controls; native runtime/deployment evidence is distinguished in the build guide. Metro Police, traffic, Fire, EMS and Disaster Management workflows, camera ingestion and agency training remain future rounds. Synthetic map icons are exercise hazards, not a claim that these agencies are connected. The existing single-active-instance deployment limit applies to map SSE, sessions and quotas.
