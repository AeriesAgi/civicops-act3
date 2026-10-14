# Live providers in the explicit demo (ElevenLabs voice, Azure OpenAI and Gemini reasoning, intake room)

By default `CIVICOPS_DEMO_MODE=true` keeps every provider simulated. `CIVICOPS_DEMO_LIVE_PROVIDERS` opts named providers back in. It accepts a comma list of `llm` and `elevenlabs`. Credentials come only from environment variables, mounted `*_FILE` secrets or Azure App settings and secrets. They are never stored in the repository and never sent to the browser.

## ElevenLabs resident agent
| Setting | Value |
|---|---|
| `CIVICOPS_DEMO_LIVE_PROVIDERS` | include `elevenlabs` |
| `ELEVENLABS_API_KEY` | secret: a restricted workspace key with ConvAI access |
| `ELEVENLABS_AGENT_ID` | `agent_1401m2qf3ag1eaxvtx7mrnzgvpng` ("CivicOps — private response specialists"). This is an identifier, not a secret. |
| `ELEVENLABS_SESSION_MODE` | `signed-url`. The agent has authentication enabled, so the server mints a short-lived signed WebSocket URL with the API key. `public` widget mode would not work. |
| `CIVICOPS_ELEVENLABS_TOOL_SECRET` | secret: recommended so that live agent tool calls to CivicOps are authenticated |

- The resident desk calls `GET /api/channels/elevenlabs/session`. The browser receives only the `signed_url` and starts the pinned `@elevenlabs/client` session over a WebSocket.
- The label reads **ELEVENLABS LIVE**.
- If the key or agent ID is missing, the channel stays **SIMULATED**.
- In the demo, if the live session cannot start, the page falls back to the labelled simulated agent and shows "SIMULATED (live voice unavailable)".
- The simulated chat endpoint stays available as that fallback.
- Agent-side notes:
  - **Voice:** the agent currently has "Text only" enabled. For voice, turn it off in ElevenLabs. The "Start text" button works either way.
  - **Language:** the agent is configured for English. Keep the resident UI language on English, or enable the language override on the agent.

## Reasoning provider chain
`CIVICOPS_LLM_PROVIDER=azure-openai,gemini` gives this order for each decision:
1. Azure OpenAI (primary).
2. Google Gemini (secondary).
3. The deterministic rules.

The run that is kept records which provider produced it. When the secondary produced the output, the error field reads, for example, `azure-openai:provider_credits (secondary used)`. When both providers fail, it reads `azure-openai:…;gemini:…` and the run is labelled `deterministic-fallback`. HTTP 402 is reported as `provider_credits`, the same credits signal the Tablekeeper Band seats use.

## Google Gemini (AI Studio) secondary
| Setting | Value |
|---|---|
| `GEMINI_API_KEY` | secret: an AI Studio key, sent as a bearer token to the fixed Gemini OpenAI-compatible endpoint `generativelanguage.googleapis.com/v1beta/openai/chat/completions` |
| `GEMINI_MODEL` | default `gemini-2.5-flash` |
| `GEMINI_PROVIDER_LABEL` | default `Google Gemini (<model>)` |

The Gemini lane shares `CIVICOPS_LLM_MAX_COMPLETION_TOKENS` (sent as `max_tokens`, which covers thinking), `CIVICOPS_LLM_REASONING_EFFORT` and `CIVICOPS_LLM_TIMEOUT_MS`.

## Intake room (optional, `CIVICOPS_INTAKE_ROOM=true`)
This is a port of the Tablekeeper Band classroom: three fixed seats run in parallel over the same provider chain, with one room timeout (`CIVICOPS_INTAKE_ROOM_TIMEOUT_MS`, default 25 s). The seats run after triage and the duplicate check.

| Seat | Agent name | Output |
|---|---|---|
| coordinator (intake classifier) | `IntakeRoom.Coordinator` | queue: emergency-escalation, urgent-field, standard-field, information-only or needs-clarification |
| implementer (location and hazard extractor) | `IntakeRoom.Extractor` | the resident-supplied location hint restated (never verified) and hazard terms |
| reviewer (safety) | `IntakeRoom.Reviewer` | requiresHumanReview and safetyConcern |

- **Schemas and fallback:** each seat has its own strict schema and deterministic fallback.
- **Recommendation:** a rules-based chair writes `IntakeRoom.Recommendation`, labelled live, mixed or simulated with "n/3 seats live", plus an audit entry.
- **Life safety:** deterministic life-safety hazards cannot be downgraded by a model.
- **Human control:** the room never assigns, dispatches or contacts anyone. It can only raise the human-review flag.
- **Labels:** the control-room agent timeline labels each seat with the provider that produced it.

## Azure OpenAI reasoning (triage, duplicate, dispatch recommendation, comms drafts, room seats)
| Setting | Value |
|---|---|
| `CIVICOPS_DEMO_LIVE_PROVIDERS` | include `llm` |
| `CIVICOPS_LLM_PROVIDER` | `azure-openai` |
| `CIVICOPS_AZURE_OPENAI_ENDPOINT` | `https://<resource>.openai.azure.com`. Only `*.openai.azure.com` and `*.cognitiveservices.azure.com` over HTTPS are accepted. |
| `CIVICOPS_AZURE_OPENAI_DEPLOYMENT` | the deployment name, for example `tablekeeper-demo` (gpt-5-mini) |
| `CIVICOPS_AZURE_OPENAI_AUTH` | `managed-identity` (default when no key is set) or `key` |
| `CIVICOPS_AZURE_OPENAI_CLIENT_ID` | optional client ID of a user-assigned identity |
| `CIVICOPS_LLM_API_KEY` | secret: only for `key` auth, sent as the `api-key` header |
| `CIVICOPS_AZURE_OPENAI_API_VERSION` | default `2025-04-01-preview` |
| `CIVICOPS_LLM_MAX_COMPLETION_TOKENS` | default 4000, which covers gpt-5-mini's hidden reasoning tokens; clamped 256–16000 |
| `CIVICOPS_LLM_REASONING_EFFORT` | default `low` |
| `CIVICOPS_LLM_TIMEOUT_MS` | default 20000 for Azure |
| `CIVICOPS_LLM_PROVIDER_LABEL` | default `Azure OpenAI (<deployment>)`, shown on agent runs and the KPI tile |

- **Request:** the client sends strict JSON-schema chat completions to `/openai/deployments/<deployment>/chat/completions`. It sends no `temperature`, because reasoning models reject it, and it uses `max_completion_tokens`.
- **Managed identity:** uses the platform's `IDENTITY_ENDPOINT`/`IDENTITY_HEADER` with resource `https://cognitiveservices.azure.com`. Tokens are cached until five minutes before expiry. The identity needs the **Cognitive Services OpenAI User** role on the resource.
- **Fallback:** on timeout, HTTP error, truncation (`finish_reason=length`), schema failure, identity failure or an open circuit, each decision falls back to the deterministic rules. It is then labelled `deterministic-fallback` with the reason.
- **Human control:** a human still approves every dispatch.

## Hard spend caps

Global counters persist in `CIVICOPS_DATA_DIR/spend-caps.json` and reset each UTC day and month. `CIVICOPS_LLM_DAILY_CALL_CAP` (default 2000) and `CIVICOPS_LLM_MONTHLY_CALL_CAP` (default 20000) count logical model decisions; when either is reached, runs use the deterministic fallback and are labelled "Spend cap reached. Simulation mode". `CIVICOPS_VOICE_DAILY_SESSION_CAP` (default 100) and `CIVICOPS_VOICE_MONTHLY_SESSION_CAP` (default 1000) count minted ElevenLabs sessions; when reached, the resident page switches to the labelled simulated agent. `GET /api/spend-caps` reports the state and drives the banner.
