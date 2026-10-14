# AMD Instinct MI300X vLLM deployment guide

This repository supplies an AMD inference deployment path and synthetic benchmark. **No GPU exists on the shared development VM; no AMD/vLLM container was executed, no model request was sent and no AMD performance measurement was made in round 7.** Operator labels, deployment manifests and simulated decisions do not attest AMD hardware. Retain actual host/device/ROCm evidence and measured synthetic results before making that claim in the ACT III submission.

## Owner infrastructure and image selection

[AMD Developer Cloud](https://www.amd.com/en/developer/resources/cloud-access.html) offers AMD Instinct MI300X access through a third-party provider, with approval-based credits or paid usage. Account creation, allocation, payment, credential custody and approved spend are owner actions. No account was created here. Confirm the allocated GPU count/partition, instance CPU/RAM, disk quota and network restrictions; preserve output from `amd-smi list`, `rocminfo` and driver/ROCm version commands on the allocated host. Verify the exact ROCm driver/userspace compatibility for the selected image before loading a model.

The official [vLLM GPU/ROCm installation guide](https://docs.vllm.ai/en/stable/getting_started/installation/gpu/index.html) publishes `vllm/vllm-openai-rocm` and describes AMD device access/shared memory. New Compose/Helm assets pin upstream's stable manifest resolved read-only on 8 October 2026:

```text
vllm/vllm-openai-rocm@sha256:749f6f3f944f12af49966ac523c1f8573e4b229594b541954bd5f879b4496b1f
```

This digest establishes reproducibility, not compatibility or a vulnerability-free image. Owners must inspect its package/ROCm versions, scan it and validate on their host; use an owner-tested immutable replacement if necessary. Existing [AMD shell files](../../deploy/amd/README.md) are retained. Their older `latest` default is a bootstrap path: explicitly export the pinned `CIVICOPS_VLLM_IMAGE` before using that script. Never benchmark a mutable image tag without recording its resolved digest.

## Model and device sizing

Start with the repository's ungated **Qwen/Qwen3-8B** structured-output model and review its [official model card/licence](https://huggingface.co/Qwen/Qwen3-8B) for the intended municipal use. Record the model revision/hash after downloading; the deployment model name alone does not pin weights. CivicOps disables Qwen thinking in requests, bounds output/schema, redacts detectable PII and preserves deterministic safety floors plus human approval. Unconfigured/unavailable/invalid model output remains labelled fallback. Model quality in English/Afrikaans/isiZulu and harmful/sensitive report handling require a reviewed local synthetic evaluation before production.

One MI300X is the initial sizing target for the 8B model, **subject to measurement**. It is not a memory, throughput or concurrency guarantee. New assets default to one GPU, tensor parallel size **1**, 8,192 context tokens and 85% GPU memory utilization. Increase `CIVICOPS_VLLM_TENSOR_PARALLEL` or Helm `vllm.tensorParallelSize` only when that many GPUs are actually allocated and the model/collective support is verified. Helm requires tensor-parallel size to equal `vllm.gpuCount`; the latter requests/limits `amd.com/gpu`. Measure whether multi-GPU communication improves the municipal workload before paying for additional devices. [vLLM parallelism guidance](https://docs.vllm.ai/en/stable/serving/parallelism_scaling.html) explains the supported configurations.

## Compose on an AMD host

The optional `amd-vllm` profile in [the deployment Compose file](../../deploy/compose/docker-compose.yml) maps `/dev/kfd` and `/dev/dri`, adds the video group and supplies 2 GiB shared memory. It caches model downloads in a named volume, disables usage reporting and request logging, and publishes **no host model port**. CivicOps reaches it through the private backend network. Download egress exists separately for public model weights. Device permissions/video-render group IDs and any image-specific privilege requirement need actual owner validation; this template does not grant blanket privileged mode, seccomp bypass or SYS_PTRACE. The retained legacy shell command has host-IPC mappings.

Privately configure `CIVICOPS_LLM_BASE_URL=http://amd-vllm:8000`, `CIVICOPS_LLM_MODEL=Qwen/Qwen3-8B`, an honest provider label and `CIVICOPS_LLM_API_KEY` (also supplied to `VLLM_API_KEY`). The endpoint stays on an internal network even if the optional key is blank; use a key before sharing it beyond that boundary. Model loading may take minutes. Check the model's `/health` and authenticated `/v1/models` using a read-only client that reads the bearer from a secret source and never prints it. Wait for readiness before testing. During loading/failure CivicOps remains available using labelled deterministic fallback.

Owner execution on compatible hardware:

```bash
docker compose --env-file "$CIVICOPS_COMPOSE_ENV_FILE" -f deploy/compose/docker-compose.yml \
  --profile amd-vllm up --build -d
```

Backend inference can stay private. `CIVICOPS_LLM_PUBLIC_URL` is only needed for an owner-approved optional ElevenLabs custom LLM integration; exposing it requires TLS, bearer enforcement, provider network/access controls and a POPIA operator agreement. None of that integration is activated by the deployment templates or this round.

## Kubernetes on MI300X

Install a host-compatible AMD driver/ROCm stack and the official [AMD GPU device plugin](https://github.com/ROCm/k8s-device-plugin) or GPU Operator under the owner's cluster lifecycle. Pin its release/images rather than applying mutable remote `master` manifests. Verify the actual node advertises available `amd.com/gpu` resources; add the owner-controlled `civicops.amd.com/accelerator=mi300x` label only after inspection. Set scheduling labels/tolerations and actual supplemental video/render group IDs for that cluster. The application chart does not install the plugin or verify GPU identity.

The optional [values-amd-mi300x.yaml](../../deploy/helm/civicops/values-amd-mi300x.yaml) enables a separate Recreate model Deployment, private ClusterIP Service and NetworkPolicy. It requests/limits one `amd.com/gpu`, configures tensor parallel one, runs non-root UID 1000 with a read-only root, and mounts writable model cache, `/tmp` and shared memory. Provision its `vllm.modelCacheClaim` and Secret `vllm.existingSecret` with key **CIVICOPS_LLM_API_KEY**, also available to the app's runtime Secret. The app model URL is generated from the Helm release name. Owner-reviewed HTTPS egress CIDRs are required for downloads; an offline/prepopulated cache avoids them. The 30-minute startup probe allows model loading without false restarts. Rootless ROCm/image cache compatibility is **not exercised** and must be verified on hardware before relaxing any security setting.

```bash
helm lint deploy/helm/civicops --strict -f deploy/helm/civicops/values-amd-mi300x.yaml
helm template civicops deploy/helm/civicops -n civicops \
  -f deploy/helm/civicops/values-amd-mi300x.yaml -f /run/civicops/values.yaml
```

No public vLLM Ingress is created. NetworkPolicy allows only the selected CivicOps pods to port 8000, with bounded DNS/download egress. CNI/device/plugin enforcement, model readiness under the chosen pod permissions and resource limits, GPU health/temperature and restart/cache recovery remain owner validation.

## Endpoint security and benchmark evidence

Keep `/v1/chat/completions` private behind TLS or a private network/tunnel; restrict access to the application/approved benchmark host and require endpoint authentication when crossing a trust boundary. Disable prompt/request logging, protect caches and collector access, cap concurrent requests/output/contexts, monitor timeouts, and never put API keys in command arguments, reports, screenshots or committed env files. CivicOps has no authority to apply model suggestions to assignments or send resident communications without a human decision. Its intake text is untrusted and model responses undergo schema/catalogue/safety validation.

After the owner approves **synthetic LLM requests on actual hardware**, run the repository benchmark from a host with access to CivicOps and the configured endpoint:

```bash
python3 scripts/amd_benchmark.py --civicops-url http://localhost:5080 --dry-run
python3 scripts/amd_benchmark.py --civicops-url http://localhost:5080 --repetitions 3
```

The dry run makes no model request. The measured command sends synthetic requests; it was not executed here. It fetches the app's production triage contract, excludes one warmup, runs 15 synthetic patterns per repetition and writes ignored `reports/amd_benchmark.md`. Retain latency percentiles, strict schema validity, raw/safety-floor agreement, failures and completion-token rate with the model revision, server flags, image digest, device evidence, allocated GPUs, ROCm/driver versions, UTC time and concurrent load. Token rate is completion tokens per **complete request second**, not separately measured decode throughput or time to first token. Compare a common model/prompt/budget and include warm/cold startup conditions; do not compare fallback latency to GPU inference as equivalent work. [AMD.md](../AMD.md) explains provenance and the exact workload.

The app-only [load results](LOAD_TEST_RESULTS.md) on the shared eight-core VM have **no GPU and no live inference**; they establish neither MI300X performance nor municipal capacity. Production GPU load/quality, streaming time to first token, sustained concurrency, energy, per-language quality and real provider behavior are pending measurements. Benchmark requests must remain synthetic and authorized; no real resident data belongs in GPU experiments.

## Cost and operational limits

Record the provider's actual current per-GPU/per-instance price, minimum billing interval, attached disk/storage, network egress and idle/model-warmup time before allocating. AMD's published access program includes conditional complimentary credits and pay-as-you-go; availability, approval, expiry and terms may change. No price or cost saving is attested by this repository. A simple owner worksheet is `allocated GPUs × billed hours × quoted GPU-hour rate + CPU/RAM/disk/egress charges`; record both successful reviewed decisions and failed/retried requests when deriving a cost per useful decision. Stop/release idle GPU instances and preserve only approved synthetic measurement artifacts. HA model replicas and extra tensor-parallel GPUs increase the bill; measure the benefit rather than assume it. South African municipal procurement, exchange rate, data location/operator terms and approved spend remain owner-pending.
