# CivicOps SOS operating contract

**Status: implemented and tested locally with synthetic data. External delivery, aggregator contracts and municipal acceptance remain owner-pending.** CivicOps SOS is a control-room alert and audit workflow. It does not replace national emergency services, and it does not call, text, dispatch or notify SAPS, EMS, Fire or any other responder by itself.

> CivicOps SOS alerts your municipality's control room. In a life-threatening emergency always call [10111 (police)](tel:10111), [10177 (ambulance/fire)](tel:10177) or [112 from a cell phone](tel:112).

## Trigger surfaces

Residents, signed-in crew members and the Android crew client use a deliberate one-tap action protected by a three-second hold (or an equivalent slide-to-confirm). A short cancellation window prevents a pocket trigger from becoming an operational alert. Crew has an additional silent mode for a threat situation; silent means no local alarm, not an unlogged or hidden control-room event. The resident web desk and crew PWA show the same emergency notice and tap-to-call links.

SMS and WhatsApp recognise the whole-message panic phrases `SOS`, `HELP ME`, `HULP MY`, `NGISIZE`, `NGICELA USIZO` and `SIZA`. Those phrases record one idempotent control-room alert after the channel journal accepts the message. Bare `HULP` and `USIZO` stay help text and do not open an alert. Voice and the IVR menu stay emergency-number information: they do not create an alert and still say CivicOps does not alert responders. A simulated USSD adapter exposes a `*120*xxx#`-style menu (`1` or `SOS` records an alert; `0` ends the session). Live USSD requires an aggregator contract, sender verification, a POPIA notice and operating acceptance. No provider write, call or message is made by the demo. The dedicated signed ingress route still accepts the historical allow-list, including `HELP`, `HULP`, `USIZO` and a message that contains ` SOS `.

Location is collected only after the person confirms SOS. If a resident has no location available, the surface asks for an address or landmark. A location may be shared during the active follow-up window (default 30 minutes, configurable) and stops automatically at expiry or resolution. The surface tells the person when location is being requested and when sharing has stopped. Location is never inferred from a report, collected in the background or accepted from an off-shift/no-consent worker.

## Control-room procedure

1. **Acknowledge.** Open the dedicated SOS queue and acknowledge the alert. The SLA timer starts at the trigger; the default acknowledgement target is 60 seconds (`CIVICOPS_SOS_ACK_TARGET_SECONDS`).
2. **Assess.** Review the minimum alert card: synthetic/resident hashed contact or staff name and crew, channel, trigger time, location and its accuracy, battery when available, and the audit timeline. A person with the authorised role records the assessment and reason.
3. **Dispatch or hand over.** A named authorised human selects the nearest eligible municipal crew/supervisor only after assessment, or records a hand-over note to SAPS/EMS using 10111/10177. CivicOps does not place that call. Every decision is audited.
4. **Resolve.** Record the outcome and resolution note. The follow-up location window ends immediately.

If an alert is not acknowledged by the target, reading the control-room queue records a supervisor escalation at the acknowledgement target and a duty-manager escalation at twice that target. Those notifications are simulated audit notes only. No SMS, push, call or message is sent, and no person is scored. The control room shows an accessible audible and visual banner for every unacknowledged alert. Silence alarm stops the sound for that browser and leaves the visual banner in place. Silent mode on a crew device hides only that device's on-screen alarm; the control room still alarms.

A named operator may mark a false alarm with a reason. The user notification is a simulated audit record. A later false alarm from the same requester (the same tenant-bound contact hash, or the same anonymous staff member and crew) sets `repeatedFalseAlarmReview` so a human can review it. That flag is not a count, score, penalty or restriction. Hand-over to SAPS/EMS is a logged dispatch note that states CivicOps did not place the 10111/10177 call. The operator uses the municipal procedure.

## API and lifecycle

The tenant and RBAC guards apply to every route. `POST /api/v1/sos` is idempotent on `operationId`; it returns the same alert for a replay. `POST /api/v1/sos/{id}/location` accepts a point only while the alert is active and inside the follow-up window. After expiry, resolution or false-alarm closure it rejects the write and retains no new coordinate.

The control-room actions are `acknowledge`, `assess`, `dispatch`, `resolve` and `false-alarm`. Each requires an authorised human, a reason and a unique operation ID. The lifecycle is:

`awaiting-location → triggered → acknowledged → assessing → dispatched → resolved`

An operator may mark an active alert `false-alarm` from the applicable open state, or cancel the short client-side confirmation before a trigger is recorded. Invalid transitions return a conflict. `GET` queue/detail responses contain no raw contact; residents are represented by a tenant-bound hash. `GET`/SSE queue updates are scoped to the signed-in municipality. The SSE endpoint is read-only and carries state changes for the control room.

The simulated channel and USSD ingress paths are bounded adapters: they authenticate their configured request shape, deduplicate their provider/session ID, and route only the allow-listed SOS keywords. They never send a message or call an emergency number.

## Privacy and safety boundaries

SOS is purpose-limited emergency assistance. Store only the minimum alert fields, hash resident contact values per municipality, log every access and state change, and purge coordinates after the configured follow-up/retention limit. Never add facial, biometric, voice-print, licence-plate identity, person-risk, suspicion or predictive-policing fields. A detector or agent may suggest or summarise; a named authorised human decides acknowledgement, dispatch, hand-over and resolution. See [Responsible AI and location boundaries](RESPONSIBLE_AI.md) and the [POPIA engineering controls](privacy/POPIA.md).

## Configuration and acceptance

Blank placeholders belong in `.env.example`; the demo defaults to local durable storage, simulated notifications and no external delivery.

| Variable | Default | Purpose |
| --- | --- | --- |
| `CIVICOPS_SOS_LOCATION_WINDOW_MINUTES` | `30` | Bounds location follow-up to 1–120 minutes. |
| `CIVICOPS_SOS_ACK_TARGET_SECONDS` | `60` | Acknowledgement target, bounded to 10–3,600 seconds. |
| `CIVICOPS_SOS_CANCEL_WINDOW_SECONDS` | `10` | Short client cancellation window, bounded to 3–60 seconds. |

Escalation has no separate environment variable. It is a simulated audit note at one and two times `CIVICOPS_SOS_ACK_TARGET_SECONDS`. Paired crew devices may call `sos-trigger` for their own alert and `crew:self` for shift work. They cannot acknowledge, dispatch, or read the control-room queue. Crew coordinates are rejected on create. A follow-up point is accepted only while the alert window is open, the worker is on the current shift, explicit consent is current, and the visible sharing indicator is already on. Otherwise the worker shares a landmark. SOS never starts location tracking.

Local tests cover the state machine, idempotency, location-window rejection, keyword/USSD parsing, tenant/RBAC guards, audit entries and emergency-number copy on every SOS surface. A production municipality still must approve the legal basis and notices, retention schedule, emergency operating procedure, staffing/SLA, translations, aggregator contracts, responder hand-over arrangements, accessibility on target devices and disaster/failover exercises.
