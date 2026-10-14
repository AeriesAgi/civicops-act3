#!/usr/bin/env bash
# Run on an AMD Instinct host with ROCm installed, not on the development workstation.
set -euo pipefail
model="${CIVICOPS_LLM_MODEL:-Qwen/Qwen3-8B}"
image="${CIVICOPS_VLLM_IMAGE:-vllm/vllm-openai-rocm:latest}"
port="${CIVICOPS_VLLM_PORT:-8000}"
cache="${CIVICOPS_MODEL_CACHE:-${TMPDIR:-/tmp}/civicops-model-cache}"
mkdir -p "$cache"
# Optional endpoint authentication is passed through the container environment, never printed.
args=(--model "$model" --host 0.0.0.0 --port 8000 --dtype auto
      --max-model-len 8192 --gpu-memory-utilization 0.85
      --tensor-parallel-size "${CIVICOPS_VLLM_TENSOR_PARALLEL:-1}")
if [[ -n "${CIVICOPS_LLM_API_KEY:-}" ]]; then
  export VLLM_API_KEY="$CIVICOPS_LLM_API_KEY"
fi
exec docker run --rm --name civicops-amd-vllm \
  --device=/dev/kfd --device=/dev/dri --group-add video --ipc=host \
  --publish "127.0.0.1:${port}:8000" \
  --volume "${cache}:/root/.cache/huggingface" \
  --env VLLM_API_KEY --env VLLM_NO_USAGE_STATS=1 \
  "$image" "${args[@]}"
