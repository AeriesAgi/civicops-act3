# CivicOps round 3 — visual presentation, 8 October 2026

Branch: `grok/civicops-finish`. Baseline: `9694fe0`. This round changes presentation, browser QA and documentation only. No backend feature was added. All work was local to this repository, with prescribed temporary verification artifacts under `/workspace/civicops/tmp`. No push, remote/PR/merge/rebase/force/amend operation, Docker run, provider/SMTP/LLM write API call, secret/environment file or real PII was used. `/workspace/meta-vr` was untouched.

## Visual changes

- Stronger resident hero, clear navigation, consistent card spacing and restrained teal/lime accents using system fonts. No framework, font download or new CDN asset.
- Compact queue overview plus seven audit-derived outcome KPIs: median report → triage, approval, assignment and resolution; digital intake share; AI versus fallback counts; duplicate candidate rate. Sample counts, method and **SYNTHETIC DEMO DATA** remain visible. Small bars represent the displayed shares, not invented histories or performance gains.
- Native disclosure rows share typography, custom chevrons, hover/focus states and comfortable spacing. Routing override and conversation headings no longer run together.
- Agent runs form a compact default-collapsed pipeline with provider/model, measured duration and status. Sub-millisecond durations show `<1 ms`; fallback runs omit artificial confidence. Relevant model-reported confidence appears only within expanded reasoning, labelled uncalibrated. Operator provider labels never attest AMD hardware.
- Crew eligibility is a reason list. Eligible, warned, blocked, valid and expired badges use text plus contrast-safe colour. The electrical evidence image contains a genuinely expired synthetic authorisation from the existing workforce seed.
- All three views were checked at 1440 and 390 widths. Control boundaries, readable muted text, focus outlines, labels, reduced motion, mobile disclosure targets, the skip target and keyboard-scrollable audit region were reviewed.
- README opens with the resident hero, followed by a compact two-column gallery. EXPLICIT DEMO, SIMULATED, synthetic and unverified disclosures remain intact.

## Screenshots

Every final PNG was inspected individually after regeneration. All seven are below 1 MB each; the set totals 1,072,008 bytes.

| Path | Dimensions at device scale 1 | Bytes |
|---|---|---:|
| `docs/media/resident-desk.png` | 1440 × 1286 | 195837 |
| `docs/media/control-room.png` | 1440 × 1500 | 345571 |
| `docs/media/case-timeline.png` | 1440 × 1000 | 225596 |
| `docs/media/crew-certifications.png` | 781 × 550 | 55668 |
| `docs/media/audit-verify.png` | 1440 × 1000 | 118689 |
| `docs/media/simulated-agent-chat.png` | 649 × 500 | 43860 |
| `docs/media/mobile-resident.png` | 390 × 1100 | 86787 |

Control-room framing starts at the top and includes its header, synthetic KPIs, full first queue card, selected case and complete eligibility card. Resident framing includes complete intake/chat/lookup cards. The evidence image uses documented **layout-only** capture CSS to place actual provenance and timeline content alongside each other; it includes all recorded runs, resolution and the final simulated Zendesk receipt. Other shots use the regular application layout.

Captures fix colour scheme, reduced motion, locale and timezone and use the existing idempotent synthetic seed. Actual generated references, timestamps, measurements and audit data are retained. They are not substituted with fictitious performance values. [Browser reproduction and capture details](BROWSER_QA.md).

## Exact local verification

```bash
export PATH=$HOME/.dotnet:$HOME/.local/bin:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 TMPDIR=/workspace/civicops/tmp
export CIVICOPS_PLAYWRIGHT_MODULE=/workspace/civicops/tmp/browser-tools/node_modules/playwright-core/index.mjs
dotnet build -c Release
dotnet test -c Release --no-build --logger 'trx;LogFileName=round3.trx' --results-directory "$TMPDIR/test-results"
./scripts/smoke_e2e.sh
./scripts/smoke_lifecycle.sh
CIVICOPS_UPDATE_MEDIA=1 ./scripts/smoke_browser.sh
unset CIVICOPS_UPDATE_MEDIA
./scripts/smoke_browser.sh
gitleaks detect --source . --log-opts="--all" --redact
gitleaks dir . --redact
git diff --check
git status --short
```

| Gate | Result |
|---|---|
| Release build | 0 warnings, 0 errors |
| Full tests | **625 passed**, 0 failed, 0 skipped; TRX counters independently checked |
| End-to-end smoke | Passed simulated channels, deterministic agents, human lifecycle, reviewed comms, privacy and **10 verified audit events** |
| Lifecycle smoke | Passed non-demo auth, idempotency/version conflicts, verified actors, eligible crews, lifecycle, Open311 and audit |
| Browser smoke with media update | **51 passed**; all original 27 functional checks retained, plus presentation/accessibility/capture guards |
| Browser smoke without media update | **51 passed** from a clean tree and left it clean; SHA-256 of every checked-in PNG unchanged |
| JavaScript/shell/diff | `node --check` for app/browser harness, `bash -n scripts/smoke_browser.sh`, `git diff --check` passed |
| Gitleaks history/directory | Both clean |
| Git | Local commits only; final tree clean |

Routine browser captures and synthetic evidence downloads now go to `$TMPDIR/browser-smoke`. `docs/media` requires the explicit `CIVICOPS_UPDATE_MEDIA=1` opt-in. CI forces flag `0`, checks tracked/untracked cleanliness after the browser run and uploads temporary browser evidence. Its existing five-minute limit is retained. **Remote CI remains unverified because nothing was pushed.**

## Local commits

- `b921668` — Present the synthetic demo with a hero and screenshot gallery
- `3ae1122` — Present workflow metrics, agent provenance and crew qualifications clearly
- `c783bf8` — Polish responsive layouts, summaries and accessible status accents
- `a929b9f` — Strengthen control contrast and keyboard navigation
- `e9eba7b` — Keep browser QA artifacts temporary and compose judge screenshots
- `65b3999` — Publish seven inspected screenshots and updated presentation evidence

The status-document commit follows these; `git log 9694fe0..HEAD --oneline --reverse` lists the complete round.

Actual AMD inference/hardware/performance, live ElevenLabs/Zendesk delivery, deployed microphone/SDK behaviour, Docker and event eligibility remain unverified. Round 2's functionality, configuration and owner continuation boundaries remain in [ROUND2_STATUS.md](ROUND2_STATUS.md).
