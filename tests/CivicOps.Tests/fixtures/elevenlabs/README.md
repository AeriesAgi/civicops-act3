# Reviewed ElevenLabs request fixtures

`default.json` and `amd.json` are complete offline `elevenlabs provision --json` plans, with secret/phone references redacted. They contain five tool payloads, one content-addressed synthetic text knowledge document and two agent payloads. Tests compare the actual dry-run output structurally against these checked-in fixtures. Treat a changed fixture as a provider-contract review, not an automatic snapshot update.

Reviewed on 8 October 2026 against all 37 saved `el__*.md` references and their current official `.md` URLs. The live Markdown matched the saved documents after removing the index notice. The expected contracts include:

- `tool_config.type=webhook`; API URL/method, header maps, JSON object body schemas and system dynamic variables.
- Secret headers use `{secret_id}`; conversation headers use `{variable_name}`. Neither uses a literal credential or an invented `type` discriminator.
- Agent prompt `tool_ids` and `knowledge_base:[{type,name,id,usage_mode}]`; IDs remain placeholders until apply.
- Custom LLM `llm=custom-llm`, full URL, `model_id`, `api_type=chat_completions`; optional bearer auth uses `api_key:{secret_id}`.
- `language_presets.af.overrides.agent` with Afrikaans first message and language; callback wording retains callback consent.
- Legacy supported `platform_settings.data_collection` map of typed analysis items, auth/hostname allowlist and widget voice/text settings.
- Optional configured webhook attachment uses `platform_settings.workspace_overrides.webhooks` with `post_call_webhook_id`, `events:[transcript,call_initiation_failure]`, JSON format and no audio. This optional block, secret injection, allowlists and human-transfer injection have focused tests.

Session/outbound endpoint shapes are exercised separately with in-memory fake HTTP, including signed WebSocket URL vs WebRTC token and `agent_id`, `agent_phone_number_id`, `to_number`, initiation dynamic variables for both Twilio and SIP. No live API request is made by a test or dry run.

Sources: [Create tool](https://elevenlabs.io/docs/eleven-agents/api-reference/tools/create), [Create Agent](https://elevenlabs.io/docs/eleven-agents/api-reference/agents/create), [Knowledge text](https://elevenlabs.io/docs/eleven-agents/api-reference/knowledge-base/create-from-text), [Signed URL](https://elevenlabs.io/docs/eleven-agents/api-reference/conversations/get-signed-url), [WebRTC token](https://elevenlabs.io/docs/eleven-agents/api-reference/conversations/get-webrtc-token), [Twilio](https://elevenlabs.io/docs/eleven-agents/api-reference/integrations/twilio/outbound-call), [SIP](https://elevenlabs.io/docs/eleven-agents/api-reference/sip-trunk/outbound-call).
