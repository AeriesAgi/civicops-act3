# 0004 — Bounded agents with deterministic fallback

- Status: accepted; live model quality and municipal-language review remain owner-pending.
- Date recorded: 2026-10-08 (SAST).
- Scope: extraction, triage, duplicate review, crew recommendations and communications drafts.

## Context and Problem Statement

Resident text is untrusted and providers can return malformed, unsafe, slow or unavailable output. The product needs useful intake in a zero-configuration demonstration and during provider outages. Agent names and declared permissions must not imply autonomous execution capabilities.

## Decision Drivers

- Keep accepted outputs within municipal catalogue and workflow constraints.
- Bound latency, response size and retries without blocking the entire service.
- Preserve deterministic safety rules independently of model output.
- Record the actual reasoning/fallback source so reviewers can assess it.

## Considered Options

- Open-ended agent loops with model-selected tools.
- Deterministic rules only.
- A fixed agent pipeline with schema validation and deterministic alternatives.

## Decision Outcome

Choose the fixed pipeline in [AgentPipeline.cs](../../products/civicops/Services/AgentPipeline.cs): IntakeAgent, TriageAgent, DuplicateAgent, DispatchAgent and CommsAgent. Agents draft or recommend; the service exposes no model-command executor for approval, assignment, calls or publication. `ToolPermissions` in provenance describe scope rather than granting executable capabilities.

[AgentSchemas.cs](../../products/civicops/Services/AgentSchemas.cs) constrains fields, types, bounds, coherent work-order/category/department pairs and currently eligible crew IDs. [AiReasoningClient.cs](../../products/civicops/Services/AiReasoningClient.cs) requests strict JSON-schema output, temperature zero and at most 800 tokens. It accepts one bounded response, caps provider bytes at 64 KiB and content at 8 KiB, rejects truncated/malformed/invalid output and uses at most two HTTP attempts within one shared **per-decision** timeout. Five sequential decisions can consume multiple such budgets; there is no claim of a single timeout for the entire pipeline.

Missing or invalid endpoint configuration, provider failure, open circuit and invalid output choose a validated deterministic fallback. Caller cancellation remains cancellation. [DeterministicClassifier.cs](../../products/civicops/Services/DeterministicClassifier.cs) and the pipeline preserve critical work-order qualifications and floor danger priority at Urgent; models cannot replace resident-supplied locations, automatically merge duplicates or assign crews. Every new case still waits for human approval. [ProviderCircuit.cs](../../products/civicops/Services/ProviderCircuit.cs) limits repeated transient failure and admits a bounded recovery probe.

Input/output redaction is best effort. [AgentRun.cs](../../products/civicops/Models/AgentRun.cs) retains input hash, actual model/provider, latency, confidence, rationale, output, fallback and generic error reason. The status window is bounded rather than a historical performance warehouse.

## Consequences

Intake remains available without a model and during outages. Schema validity does not prove factual correctness, calibrated confidence or language quality. Temperature zero does not guarantee identical live inference. Rules and synthetic tests need continuing municipal review; Afrikaans/isiZulu drafts require native-speaker review. Deterministic recommendations may be less nuanced and must retain their fallback labels.

## Confirmation

Implemented and tested locally: [AiPipelineTests.cs](../../tests/CivicOps.Tests/AiPipelineTests.cs), [MultilingualSafetyTests.cs](../../tests/CivicOps.Tests/MultilingualSafetyTests.cs) and [SafetyEvidenceTests.cs](../../tests/CivicOps.Tests/SafetyEvidenceTests.cs) cover validation, permission boundaries and safety floors. [ObservabilityResilienceTests.cs](../../tests/CivicOps.Tests/ObservabilityResilienceTests.cs) exercises unavailable/slow providers and fallback. [smoke_e2e.sh](../../scripts/smoke_e2e.sh) exercises the deterministic workflow. Live model accuracy, prompt-injection assessment on the selected model and operational latency remain unverified.
