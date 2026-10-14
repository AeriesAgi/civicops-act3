# Three-minute judge walkthrough

Record a synthetic workflow with a visible AMD provider label and actual AMD inference evidence once the host is configured. If the recording uses the fallback, say **simulated** on screen and in narration. Provider credentials, contact details, private transcripts and environment values never belong in the recording. The service is not an emergency line; voice-agent prompts redirect emergencies to the applicable emergency service numbers.

## Before recording

For a fast-clicking local recording, start the server with `CIVICOPS_DISPATCH_LIMIT=1000` so the per-minute staff request limit does not show “Request quota exceeded” mid-take. Keep the default limit everywhere else. After signing in through Staff access, reload the page once before the first action so the shell holds the current session security token.

Start the app, seed the 15 synthetic cases and open Resident, Dispatcher and Audit views. Make the model endpoint healthy and retain the real AMD benchmark report plus host/image evidence. Configure the ElevenLabs grant and Zendesk sandbox/private workspace through their setup guides only after owner authorization. Check the public app URL, screen size, microphone and permission prompts. Use a synthetic area, `example.invalid` address and dummy contact number wherever a form needs one.

| Time | Shot | Narration / proof |
|---|---|---|
| 0:00–0:15 | Resident landing page and channel status | “A resident report should not disappear between a phone call, a ticket and a crew. CivicOps keeps the handoff visible.” State whether channels are live or simulated. |
| 0:15–0:40 | Voice/widget or text report: synthetic blocked stormwater drain | Resident agent asks what and where, avoids an ETA, records the issue through a server tool and reads back the case reference. Show the same path through simulated chat when recording offline. |
| 0:40–1:05 | New case detail and agent timeline | Expand intake, triage, duplicate and dispatch runs. Show actual model/provider, confidence, reason, latency and the proposed Roads crew. Point out the human-review gate and the independent urgent-hazard floor. |
| 1:05–1:25 | Dispatcher approval and eligible assignment | “The model recommends; this verified dispatcher approves and assigns.” Record approval first, then assignment. Show the Zendesk ticket id or the exact simulated outbox delivery; no requester details on screen. |
| 1:25–1:45 | Live map → Load evening scenario | Clustered synthetic cases, crew status and hazard exclusion zones. Say: aggregated operations only, no person tracking or crime prediction. |
| 1:45–2:10 | Agencies → Load synthetic scenario | Crash with a downed pole: Traffic, EMS, Fire and Electricity on one incident. Assign the incident commander, accept the neighbouring metro's mutual-aid request, then confirm one **SIMULATED** camera event and dismiss another. Say: scene events only, no faces or plates, a human confirms every event. |
| 2:10–2:35 | SOS: crew PWA hold-to-confirm → control-room alert → acknowledge → resolve | Show the 10111/10177/112 notice, the bounded location indicator and the audited hand-over. Say: “CivicOps alerts the municipal control room; it never replaces emergency numbers or dispatches anyone automatically.” |
| 2:35–2:50 | Audit verification and reasoning provider | Show a verified chain. No AMD endpoint exists for this build: show the `deterministic-fallback` provider label and say **simulated** reasoning. |
| 2:50–3:00 | Control-room overview and closing product frame | “Municipal desks get a conversational front door, accountable AI assistance and a human decision at every dispatch, every mutual-aid request and every SOS.” |

## Backup shots

Keep a duplicate candidate, a low-confidence General service case and a synthetic urgent electrical hazard ready in the queue. A short insert can show that no duplicate is merged and urgency survives a disagreeing model. Demonstrate offline/stale state by disconnecting only after operational requests finish; the interface must not claim current data or an offline dispatch.

The automated `scripts/smoke_e2e.sh` produces the same offline channel-to-ticket-to-human workflow and verifies signed post-call replay plus the audit chain. It is test evidence, not footage of a live ElevenLabs call or AMD inference.

## Companion deck and cover

Suggested six-slide outline: the lost-handoff problem; resident experience; ElevenLabs → AMD agents → Zendesk → human control architecture; live AMD evidence; business value and Culltron pilot; limitations and next steps. Suggested cover: the CivicOps wordmark, a resident-to-dispatcher route and an AMD workload caption. Slides, cover image and the final video are owner deliverables for the next round; no completed presentation or hosted video is claimed here.
