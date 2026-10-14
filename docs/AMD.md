# The AMD workload in CivicOps

CivicOps sends its backend agents' reasoning to an open model served by vLLM on ROCm on an AMD Instinct host. This is the product workload: intake extraction, triage, optional duplicate confirmation, crew recommendation, and resident update drafting. The .NET API validates every response and records its provider, model, endpoint label, confidence, rationale, latency, fallback flag and error. Human dispatchers approve and assign every response.

The default deployment model is `Qwen/Qwen3-8B`; the model name is configurable. Qwen3 thinking is disabled in requests to keep the small structured-output budget predictable. A GPU is not required for development. With no model base URL, every agent runs deterministic rules labelled `deterministic-fallback`. Missing configuration, simulated calls and fake-server test results do not prove that a workload ran on AMD.

An ElevenLabs resident agent can also use the AMD endpoint as its brain: provision with `--llm amd`, provide `CIVICOPS_LLM_PUBLIC_URL`, and use a protected HTTPS endpoint reachable by ElevenLabs. Voice synthesis and conversation transport remain ElevenLabs workloads. Zendesk hosts tickets; CivicOps approval, persistence, audit, outboxes and the dashboard run in the .NET container. No inference hardware claim is made for those services.

## Configure the backend

Set these in the private runtime environment:

```text
CIVICOPS_LLM_BASE_URL=http://127.0.0.1:8000
CIVICOPS_LLM_MODEL=Qwen/Qwen3-8B
CIVICOPS_LLM_PROVIDER_LABEL=vLLM on AMD Instinct MI300X, AMD Developer Cloud
CIVICOPS_LLM_TIMEOUT_MS=10000
```

`CIVICOPS_LLM_API_KEY` is optional when a protected local endpoint needs no bearer token. Put the key in the process environment, never an agent JSON file, command line, committed `.env`, report or screenshot. `CIVICOPS_LLM_PUBLIC_URL` is a separate, protected public URL for the ElevenLabs custom LLM setting. The backend base URL may include `/v1`; CivicOps adds `/v1/chat/completions` correctly in either case.

The client uses `response_format.type=json_schema`, strict required fields and no extra properties. vLLM documents this compatibility in its [structured output guide](https://docs.vllm.ai/en/latest/features/structured_outputs/). App-side validation remains necessary: category/department pairs must agree, confidence must be 0–1, and a crew must belong to the eligible department. One decision has a shared 250–60,000 ms budget and at most two attempts; only network and transient HTTP failures retry. Invalid or oversized model JSON falls back without silently approving anything.

Danger language always floors priority at Urgent. Low confidence and disagreement with deterministic triage require review. Duplicate reports stay separate. Comms drafts require review; this agent cannot send messages. Raw prompts treat resident descriptions as untrusted data and expose no callable assignment or communication tools. Every agent records its declared permissions.

## Measure and show the working AMD path

1. Start vLLM on the AMD host using [deploy/amd/README.md](../deploy/amd/README.md).
2. Start CivicOps configured against that host and create synthetic reports.
3. Show `/api/ai/status` and the dispatcher case timeline. The status includes model/provider labels, decision and HTTP request counts, p50/p95 decision latency over the last 512 decisions, and lifetime fallback rate. These labels are operator configuration, not automatic hardware attestation.
4. Benchmark the exact production prompt/schema exposed by `/api/ai/triage-contract`:

```bash
python3 scripts/amd_benchmark.py --civicops-url http://localhost:5080 --repetitions 3
```

The script reads `CIVICOPS_LLM_BASE_URL`, `CIVICOPS_LLM_MODEL`, optional `CIVICOPS_LLM_API_KEY`, `CIVICOPS_LLM_PROVIDER_LABEL` and `CIVICOPS_LLM_TIMEOUT_MS`. It performs one excluded warmup, then measures 15 synthetic cases per repetition, writes `reports/amd_benchmark.md`, and reports latency, completion tokens per complete request second, strict JSON validity and raw/safety-floor label agreement. The dataset includes mixed descriptions and urgent hazards. Failed calls count in the reported denominators. `--dry-run` prints one production request without invoking the model; `--contract-file` accepts a previously exported contract.

Before submission, retain the measured report with the model version, image digest, AMD device/ROCm evidence and the short video showing a live agent decision. Record the actual Instinct model from the allocated host. The script itself never verifies the GPU type. Round 1 has no AMD host or credentials, so hardware throughput and accuracy are **not measured**. Do not fill the benchmark with estimated numbers or relabel deterministic results as AMD inference.
