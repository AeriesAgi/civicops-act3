# CivicOps application security

The authoritative security and pilot boundary is [../../SECURITY.md](../../SECURITY.md). It covers dispatcher authentication, partitioned quotas, input/body bounds, provider signatures, conversation binding, consent, human dispatch gates, public projections, outboxes, audit verification and the single-process persistence limit.

The default local experience is an explicitly labelled synthetic demo. Demo mode forces email outbox and simulated Zendesk delivery. Missing ElevenLabs and model configuration select simulated resident chat and deterministic backend reasoning. Keep optional live credentials unset for offline walkthroughs and CI.

For a protected runtime set `CIVICOPS_DEMO_MODE=false`, configure named dispatcher token hashes and use a private persistent `CIVICOPS_DATA_DIR`. Provider credentials must come from the private environment. Raw cases, contact information, transcripts, outboxes and provisioning state never belong in Git or public reports. See [the API guide](docs/api.md) and the provider setup documents before changing the pilot boundary.
