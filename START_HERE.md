# CivicOps: start here

CivicOps is a municipal operations demo. It covers resident intake, bounded backend agents, human dispatch, multi-agency incidents and an append-only audit. All data in this repository is synthetic and every external provider is simulated by default.

1. Read [README.md](README.md) for the feature walkthrough and screenshots.
2. Review [ARCHITECTURE.md](docs/ARCHITECTURE.md) and [SECURITY.md](SECURITY.md) before changing dispatch, provider webhooks, public views or persistence.
3. Real integrations are optional and configured only through a private environment: see [ElevenLabs](docs/ELEVENLABS_SETUP.md), [Zendesk](docs/ZENDESK_SETUP.md) and [AMD](docs/AMD.md). Dry-run provisioning makes no external changes.
4. Run the Release build, the tests and the smoke scripts before opening a pull request.

```bash
CIVICOPS_DEMO_MODE=true dotnet run --project products/civicops --urls http://localhost:5080
```

Use .NET SDK 10. Generated data belongs under the ignored data directory or a temporary directory. The service worker caches only interface assets; operational data is network-only. Keep runtime data synthetic.
