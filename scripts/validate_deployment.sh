#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"
./scripts/install_deployment_tools.sh
export PATH="${TMPDIR:-/tmp}/bin:$PATH"
evidence_dir="${TMPDIR:-/tmp}/deployment-validation"
yaml_python="${TMPDIR:-/tmp}/civicops-deployment-tools/yaml/bin/python"
mkdir -p "$evidence_dir/schema-cache"
hadolint Dockerfile
helm lint deploy/helm/civicops --strict
helm lint deploy/helm/civicops --strict -f deploy/helm/civicops/values-demo.yaml
helm lint deploy/helm/civicops --strict -f deploy/helm/civicops/values-amd-mi300x.yaml
render() {
  local variant="$1"
  shift
  helm template civicops deploy/helm/civicops --namespace civicops --kube-version 1.34.0 "$@" > "$evidence_dir/$variant.yaml"
  kubeconform -strict -summary -kubernetes-version 1.34.0 \
    -cache "$evidence_dir/schema-cache" \
    -schema-location default \
    -schema-location "$repo_root/deploy/helm/schemas/{{.ResourceKind}}.json" \
    "$evidence_dir/$variant.yaml"
}
render postgres
render file -f deploy/helm/civicops/values-demo.yaml
render features --set ingress.enabled=true --set config.CIVICOPS_TRUSTED_PROXY_NETWORKS=10.244.0.0/16 \
  --set config.AllowedHosts=civicops.example.invalid --set autoscaling.enabled=true --set serviceMonitor.enabled=true
render amd -f deploy/helm/civicops/values-amd-mi300x.yaml
"$yaml_python" scripts/validate_deployment.py "$evidence_dir"
expect_rejected() {
  local scenario="$1"
  shift
  if helm template civicops deploy/helm/civicops "$@" > "$evidence_dir/rejected-$scenario.log" 2>&1; then
    echo "Unsafe chart scenario accepted: $scenario" >&2
    exit 1
  fi
}
expect_rejected postgres-replicas --set replicaCount=2
expect_rejected file-replicas -f deploy/helm/civicops/values-demo.yaml --set replicaCount=2
expect_rejected autoscaling --set autoscaling.enabled=true --set autoscaling.maxReplicas=2
expect_rejected ephemeral-file --set storage.mode=file
expect_rejected missing-database-secret --set existingSecret=
expect_rejected untrusted-ingress --set ingress.enabled=true
expect_rejected gpu-mismatch -f deploy/helm/civicops/values-amd-mi300x.yaml --set vllm.tensorParallelSize=2
expect_rejected reserved-storage-config --set config.CIVICOPS_STORAGE=file
dotnet build deploy/container/HealthProbe/HealthProbe.csproj -c Release --nologo
"$yaml_python" scripts/smoke_health_probe.py deploy/container/HealthProbe/bin/Release/net10.0/HealthProbe.dll
echo "Deployment validation passed: Docker lint; 3 chart lints; 4 strict Kubernetes renders; 8 unsafe configurations rejected; Compose/YAML/security checks; 5 loopback probe checks. Docker and cluster execution remain owner-pending."
