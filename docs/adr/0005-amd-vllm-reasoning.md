# 0005 — AMD vLLM as the intended reasoning backend

- Status: accepted as the deployment target; actual AMD execution remains unverified.
- Date recorded: 2026-10-08 (SAST).
- Scope: inference contract, platform assets and measurement honesty.

## Context and Problem Statement

CivicOps is an AMD Developer Hackathon ACT III entry and needs a substantive municipal inference workload. A vendor-specific inference dependency would couple workflow logic to a GPU host, while claiming performance from local deterministic tests would misrepresent the evidence. The same product must remain usable while the intended host is absent or loading a model.

## Decision Drivers

- Run bounded backend reasoning on an open-model endpoint served by vLLM/ROCm on AMD Instinct.
- Keep hardware hosting separate from case authority and conversational channels.
- Preserve a portable, schema-checked interface and no-configuration fallback.
- Capture actual host/model/version/latency evidence before claiming an AMD result.

## Considered Options

- Couple application code directly to a GPU/runtime SDK.
- Depend solely on a hosted proprietary reasoning service.
- Use an OpenAI-compatible HTTP contract and target vLLM/ROCm on AMD infrastructure.

## Decision Outcome

Choose the compatible HTTP contract in [AiReasoningClient.cs](../../products/civicops/Services/AiReasoningClient.cs). [AiOptions.cs](../../products/civicops/Services/AiOptions.cs) configures base URL, model, optional private API key, truthful provider label and decision timeout. It normalizes the endpoint to `/v1/chat/completions`; the default model name is `Qwen/Qwen3-8B`. Requests disable Qwen3 thinking to fit the small structured-output budget. Endpoint compatibility and bounded response validation remain application requirements regardless of the hosting platform.

[serve_vllm_rocm.sh](../../deploy/amd/serve_vllm_rocm.sh) retains a host bootstrap; the [production Compose definition](../../deploy/compose/docker-compose.yml) supplies an optional ROCm profile and the [MI300X Helm values](../../deploy/helm/civicops/values-amd-mi300x.yaml) describe GPU deployment settings. Host devices, drivers, model weights, licence review and protected endpoint access are owner prerequisites. The retained bootstrap's configurable image default is not a reproducible benchmark attestation; record the deployed digest on the actual host.

[amd_benchmark.py](../../scripts/amd_benchmark.py) fetches CivicOps' triage contract and measures synthetic patterns against the selected backend. `/api/ai/status` records runtime labels and measured decision latency, but an operator-entered AMD label cannot attest hardware. Explicit demo removes the backend endpoint and uses deterministic fallback. ElevenLabs' optional custom-LLM public URL is a separate exposure decision; municipal backend inference can stay private.

## Consequences

The workload can target AMD without a provider-specific workflow rewrite. Other compatible endpoints can exercise the contract, but do not establish AMD execution. Model/runtime upgrades need schema regression, safety and municipal-language checks. Network authentication, GPU resources, model loading and endpoint uptime add deployment responsibilities; fallback preserves intake during absence or failure.

No actual AMD latency, throughput, accuracy, cost or institutional benefit has been measured in this repository. A synthetic CPU/file/PostgreSQL load result is not GPU evidence. The owner must retain host/ROCm/model/image details and raw benchmark results before refreshing submission claims.

## Confirmation

Implemented and tested locally: [AiPipelineTests.cs](../../tests/CivicOps.Tests/AiPipelineTests.cs) checks exact requests, malformed replies and fallback; [ProviderReadinessTests.cs](../../tests/CivicOps.Tests/ProviderReadinessTests.cs) protects readiness labels. Deployment assets are statically validated by [validate_deployment.sh](../../scripts/validate_deployment.sh). Actual GPU/container runtime and inference measurement remain owner-pending. See [AMD measurement guide](../AMD.md) and [AMD vLLM operations](../operations/AMD_VLLM.md).
