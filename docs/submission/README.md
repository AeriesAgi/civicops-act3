# CivicOps ACT III submission assets (local only)

Copies of the non-binary files live in the repository under `docs/submission/`; the video, deck, PDF and images are kept outside the repository. Produced 13 October 2026 (SAST) from branch `grok/civicops-finish`. Nothing here has been uploaded or submitted.

| File | What | Notes |
|---|---|---|
| `civicops-demo.mp4` | Demo video, 1920×1080 H.264, no audio, burned-in captions | 2:08 (4 s title card + 2:00 walkthrough + 5 s end card) |
| `civicops-demo.srt` | Caption track (13 cues) | Times include the 4 s title card |
| `civicops-deck.pptx` / `.pdf` | Six-slide deck | Cover, problem, product, event-week work, architecture and honest status, next steps |
| `cover.png` | Cover image, 1920×1080 | Agency board capture |
| `record_demo.mjs` | Playwright recorder for the walkthrough | Local server with `CIVICOPS_DEMO_MODE=true CIVICOPS_DISPATCH_LIMIT=1000 CIVICOPS_MAP_TILE_URL=none` |
| `make_cover.py`, `make_deck.py` | Regenerate the cover, cards and deck | Uses `docs/media` captures |
| `raw/` | Raw recording, cue marks, title and end cards | Intermediate files |

Recording was fully simulated: no ElevenLabs, Zendesk, SMS/WhatsApp, camera or AMD provider was contacted, and the browser blocked every non-local request. The video says on screen that reasoning is deterministic and simulated, and that no AMD endpoint exists for this build. There is no narration; captions carry the story.
