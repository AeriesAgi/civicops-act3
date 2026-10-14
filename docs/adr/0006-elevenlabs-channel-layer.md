# 0006 — ElevenLabs supplies the conversational channel

- Status: accepted; live account, phone and microphone behavior remains unverified.
- Date recorded: 2026-10-08 (SAST).
- Scope: resident voice/text, server tools, post-call evidence and callbacks.

## Context and Problem Statement

Residents should be able to explain a service issue conversationally without making a voice provider the authority for dispatch or resident identity. Browser credentials, signed callbacks and ambiguous outbound-call results also need explicit boundaries. The no-configuration demo must demonstrate the flow honestly without contacting the provider.

## Decision Drivers

- Reuse one municipal intake/lifecycle behind form, Open311 and voice/text channels.
- Keep long-lived provider keys on the server.
- Authenticate bounded tools and verify provider webhook bodies/replay markers.
- Distinguish simulated conversations, disabled features and configured live channels.

## Considered Options

- Put municipal dispatch logic inside a conversational agent.
- Build and host a speech stack inside CivicOps.
- Use ElevenLabs as a channel that invokes narrow CivicOps server tools.

## Decision Outcome

Choose a separate channel layer in [ElevenLabsChannels.cs](../../products/civicops/Services/ElevenLabsChannels.cs) and [ElevenLabsClient.cs](../../products/civicops/Services/ElevenLabsClient.cs). Five [tool definitions](../../products/civicops/elevenlabs/tools/) can list categories, report an issue, look up minimal status, add information or request human handoff. They cannot approve or assign. Tools use scoped service authentication or the default-tenant legacy tool secret, bounded payloads and conversation/turn idempotency; case mutations must match the originating conversation.

[app.js](../../products/civicops/wwwroot/app.js) loads the pinned official conversational client only for a configured session after a resident starts. Public agents use the widget; private signed-URL/WebRTC sessions obtain short-lived credentials through the server and return only required session fields. Provider keys stay outside the browser. [SecurityHeaders.cs](../../products/civicops/Services/SecurityHeaders.cs) restricts the conditional channel assets through the CSP.

[ElevenLabsSignature.cs](../../products/civicops/Services/ElevenLabsSignature.cs) verifies timestamp/raw-body HMAC for post-call callbacks. The service stores bounded transcript/analysis and durable receipts, ignores audio storage and requires review for new hazards. Outbound callbacks require both recorded contact and callback consent plus configured phone/agent settings. A persisted call intent prevents automatic retry of an ambiguous call result; operators reconcile it.

[ElevenLabsProvisioner.cs](../../products/civicops/Services/ElevenLabsProvisioner.cs) defaults to a redacted dry run; explicit checks are read-only and do not run at startup. Account-changing apply is an owner action. Missing key or explicit demo selects labelled simulated chat. Missing live agent/callback prerequisites disable the relevant capability honestly. Current live presets support en/af; simulated chat supports en/af/zu, while live isiZulu voice remains pending.

## Consequences

Conversational experience can evolve independently of municipal decision authority. Direct speech reaches ElevenLabs before backend redaction, so vendor processing/residency/retention and lawful-purpose approval are separate privacy obligations. Runtime provider credentials are global configuration; data tenancy does not provision separate municipal accounts. Live phone routes, restricted keys, allowed hosts, languages and account terms require owner validation.

## Confirmation

Implemented and tested locally: [ElevenLabsEndpointTests.cs](../../tests/CivicOps.Tests/ElevenLabsEndpointTests.cs), [ElevenLabsSignatureTests.cs](../../tests/CivicOps.Tests/ElevenLabsSignatureTests.cs), [ElevenLabsSessionTests.cs](../../tests/CivicOps.Tests/ElevenLabsSessionTests.cs) and [ElevenLabsProvisionerTests.cs](../../tests/CivicOps.Tests/ElevenLabsProvisionerTests.cs) exercise fake transports, signatures, replay, session shaping and demo isolation. [smoke_widget_csp.sh](../../scripts/smoke_widget_csp.sh) checks the browser boundary without provider requests. Live provider execution remains owner-pending; see [setup](../ELEVENLABS_SETUP.md) and [privacy limits](../privacy/POPIA.md).
