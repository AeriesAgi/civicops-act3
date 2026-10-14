# vLLM on AMD Instinct

Run this deployment on an AMD Developer Cloud Instinct host with ROCm and Docker access. The development box cannot run Docker. The script is supplied and syntax-checked; this repository makes no claim that it was executed on AMD in round 1.

The [official vLLM ROCm installation guide](https://docs.vllm.ai/en/latest/getting_started/installation/gpu/) supplies the `vllm/vllm-openai-rocm` image and `/dev/kfd`, `/dev/dri`, video-group and shared IPC mappings. Use a release image/digest validated on the allocated host for the final submission; the configurable `latest` default is a bootstrap choice, not a reproducible benchmark pin.

```bash
export CIVICOPS_LLM_MODEL=Qwen/Qwen3-8B
export CIVICOPS_VLLM_IMAGE=vllm/vllm-openai-rocm:latest
export CIVICOPS_MODEL_CACHE=/path/on/amd-host/model-cache
./deploy/amd/serve_vllm_rocm.sh
```

The script publishes port 8000 on the host loopback interface. Run CivicOps on the same host or use a private tunnel/network. For another host port set `CIVICOPS_VLLM_PORT`; set `CIVICOPS_VLLM_TENSOR_PARALLEL` to the allocated GPU count when deliberately splitting a model across devices. The default is one GPU, 8,192 context tokens, automatic dtype and 85% memory utilization. Review memory against the allocated device before increasing model size. The open default model avoids a gated model download and needs no Hugging Face credential.

If endpoint authentication is needed, set `CIVICOPS_LLM_API_KEY` privately before starting the script. It passes the corresponding `VLLM_API_KEY` as an environment variable without printing its value. Do not put a key in shell history. The container disables vLLM usage reporting. No `trust_remote_code` flag is enabled.

Check readiness with read-only calls on the AMD host:

```bash
curl --fail http://127.0.0.1:8000/health
curl --fail http://127.0.0.1:8000/v1/models
```

For an authenticated endpoint use a client that reads the bearer value from the environment without logging it. Protect any public endpoint with HTTPS, access control and network allowlists. ElevenLabs requires a reachable URL only if its optional custom LLM brain is enabled; backend inference can stay private.

Set `CIVICOPS_LLM_BASE_URL`, model and the provider label on CivicOps, then follow the [measurement instructions](../../docs/AMD.md). Capture the host's AMD Instinct and ROCm versions plus the deployed image digest separately. The benchmark uses exact app triage prompts and writes no credentials or real resident records.

The optional Compose profile runs the same ROCm devices beside CivicOps on an AMD host:

```bash
CIVICOPS_LLM_BASE_URL=http://amd-vllm:8000 \
  CIVICOPS_LLM_PROVIDER_LABEL="vLLM on AMD Instinct; verify allocated device" \
  docker compose --profile amd-vllm up --build
```

Wait for the model server's health endpoint before demonstrating inference. During loading, CivicOps remains available and records fallback decisions. This profile requires the actual AMD devices; it is not a CPU emulator.
