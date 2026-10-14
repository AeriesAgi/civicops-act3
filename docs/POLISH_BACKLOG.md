# Round 14 polish backlog

Judge's-eye sweep on 13 October 2026 (SAST) of the resident desk, control room, live map, unified inbox, agency board, audit and administration at 1440 px desktop and 390 px phone, signed in as the synthetic demo supervisor. Ranked by judge impact. Status reflects the `grok/civicops-finish` branch at the end of round 14.

| # | Weakness | Impact | Status |
|---|---|---|---|
| 1 | Round 13 multi-agency features had no screen, so judges could not see them | High | Fixed: Agencies view with cross-agency board, incident workspace and camera feed |
| 2 | Simulated camera events were API-only; the human confirmation gate was invisible | High | Fixed: camera feed card with SIMULATED badge, scene-only notice, confidence and Confirm/Dismiss |
| 3 | Rapid clicking during a recording hits the per-minute staff limit and shows “Request quota exceeded” on several views | High | Mitigated: [demo script](DEMO_SCRIPT.md) starts the recording server with `CIVICOPS_DISPATCH_LIMIT=1000`; production default unchanged |
| 4 | README first screen did not tell the expanded story | High | Fixed: feature grid (Live map · Crew app · Omnichannel · SOS · Multi-agency · Training · Enterprise · Responsible by design) and agency board hero |
| 5 | Three-minute demo script omitted the map, SOS and agencies | High | Fixed: timed beats for all three, with honest simulated labels |
| 6 | Resident SOS card squeezed its text into a narrow column on phones | Medium | Fixed: card stacks below 760 px |
| 7 | Submission draft still promised AMD measurements | Medium | Fixed: [submission](SUBMISSION.md) states no AMD endpoint and requires the `deterministic-fallback` label |
| 8 | Readiness matrix listed multi-agency work as documented only | Medium | Fixed: two implemented+tested rows with evidence links |
| 9 | Agency incident forms made the workspace very tall | Medium | Fixed: commander and mutual-aid forms are collapsible |
| 10 | Camera-confirmed incidents showed “Location: Not supplied” despite coordinates | Medium | Fixed: coordinates shown when no landmark exists |
| 11 | Screenshots predated rounds 12–13 and there was no contact sheet | Medium | Fixed: regenerated captures and [contact sheet](media/CONTACT_SHEET.md) |
| 12 | Agency board copy is English only; af/zu keys are not present for it | Medium | Open: needs i18n keys and native-speaker review |
| 13 | Live map is an empty dark canvas until the evening scenario is loaded | Low | Open: the demo script loads the scenario first |
| 14 | Outline-only map mode (no tiles) looks sparse in recordings | Low | Open: approved tile service is an owner decision |
| 15 | Agency board refreshes by 15-second polling rather than the existing SSE stream | Low | Open: acceptable for the demo |

Accessibility: the agency board passes an axe WCAG 2.2 A/AA scan in `scripts/agency_browser_smoke.mjs`, and the existing browser smoke still runs its full-view scans.
