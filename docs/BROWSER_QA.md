# CivicOps browser QA and presentation images

The browser smoke uses an existing Chrome/Chromium and Playwright Core module. It starts a fresh local explicit-demo server, blocks external browser requests, keeps providers disabled, uses synthetic reports and checks the resident, dispatcher and audit workflows. The private ElevenLabs checks use an offline SDK double. No provider session is contacted.

```bash
export PATH=$HOME/.dotnet:$HOME/.local/bin:$PATH
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 TMPDIR=/workspace/civicops/tmp
export CIVICOPS_PLAYWRIGHT_MODULE=/workspace/civicops/tmp/browser-tools/node_modules/playwright-core/index.mjs
./scripts/smoke_browser.sh
```

Routine screenshots and the synthetic evidence download go to `$TMPDIR/browser-smoke`. Routine QA leaves the tracked media untouched. CI explicitly disables media updates, checks that the working tree stays clean, and uploads temporary browser evidence separately from test results.

To intentionally refresh the affected control-room, case-timeline and admin-console captures (other product shots remain unchanged):

```bash
CIVICOPS_UPDATE_MEDIA=1 ./scripts/smoke_browser.sh
```

Inspect every PNG after regeneration before committing:

- `docs/media/admin-console.png`: municipality, role, catalogue and workforce administration.
- `docs/media/resident-desk.png`: 1440-wide resident hero, intake and completed simulated conversation.
- `docs/media/control-room.png`: 1440 × 1500 header, synthetic outcome strip, queue and selected case with current crew eligibility.
- `docs/media/case-timeline.png`: 1440 × 1000 expanded provenance and decision timeline, including the actual simulated Zendesk reference, expanded grouped draft history and one resolution milestone with both audit records accessible.
- `docs/media/crew-certifications.png`: electrical crew eligibility with an actually expired synthetic authorisation.
- `docs/media/audit-verify.png`: verified durable audit banner and recorded events.
- `docs/media/simulated-agent-chat.png`: the resident agent card with a complete synthetic exchange.
- `docs/media/mobile-resident.png`: 390 × 1100 resident viewport.

The timeline image uses temporary **layout-only** capture CSS to expand the actual selected case evidence into two columns and crop out unrelated queue, qualifications and communication sections. It opens the native counted draft-history disclosure; every displayed agent run, decision, reference and honesty label comes from the rendered application. The other shots use the regular application layout.

The resident viewport height follows the complete intake and lookup cards so their edges remain visible. Captures use device scale factor 1, a fixed light colour scheme, reduced motion, `en-ZA` formatting and the Johannesburg timezone. The demo seed uses its existing idempotency keys. Report references, event timestamps and measured timings remain real synthetic runtime results; they are never replaced by invented performance data. The smoke waits for the rendered content and a stable paint, and resets page/queue scroll before control-room capture. Each named product shot must be below 1 MB.

Round-8 browser evidence: **169 checks, 15 axe scans, zero WCAG A/AA violations**. Keyboard expansion/collapse checks preserve every draft ID, original sequence, model/provider, rationale, tool permissions and timestamp, plus both folded lifecycle source records. Live microphone/provider operation and manual assistive-technology review remain pending. See [dated verification](VERIFICATION.md).
