#!/usr/bin/env bash
# Static validation tools only; no Docker daemon, cluster or privileged installation.
set -euo pipefail
task_tmp="${TMPDIR:-/tmp}"
tool_dir="$task_tmp/bin"
download_dir="$task_tmp/civicops-deployment-tools"
mkdir -p "$tool_dir" "$download_dir"
[[ "$(uname -s)-$(uname -m)" == Linux-x86_64 ]] || { echo "Pinned deployment validators currently target Linux x86_64." >&2; exit 1; }
fetch() {
  local name="$1" url="$2" checksum="$3"
  if [[ ! -f "$download_dir/$name" ]]; then
    curl --fail --silent --show-error --location --retry 2 --connect-timeout 10 --max-time 60 "$url" --output "$download_dir/$name"
  fi
  echo "$checksum  $download_dir/$name" | sha256sum --check --strict
}
if [[ ! -x "$tool_dir/helm" ]] || [[ "$($tool_dir/helm version --short)" != v3.19.0+* ]]; then
  fetch helm.tar.gz https://get.helm.sh/helm-v3.19.0-linux-amd64.tar.gz a7f81ce08007091b86d8bd696eb4d86b8d0f2e1b9f6c714be62f82f96a594496
  tar --extract --gzip --file "$download_dir/helm.tar.gz" --directory "$download_dir" linux-amd64/helm
  install -m 755 "$download_dir/linux-amd64/helm" "$tool_dir/helm"
fi
if [[ ! -x "$tool_dir/kubeconform" ]] || [[ "$($tool_dir/kubeconform -v)" != v0.7.0 ]]; then
  fetch kubeconform.tar.gz https://github.com/yannh/kubeconform/releases/download/v0.7.0/kubeconform-linux-amd64.tar.gz c31518ddd122663b3f3aa874cfe8178cb0988de944f29c74a0b9260920d115d3
  tar --extract --gzip --file "$download_dir/kubeconform.tar.gz" --directory "$tool_dir" kubeconform
fi
if [[ ! -x "$tool_dir/hadolint" ]] || [[ "$($tool_dir/hadolint --version)" != "Haskell Dockerfile Linter 2.14.0" ]]; then
  fetch hadolint https://github.com/hadolint/hadolint/releases/download/v2.14.0/hadolint-linux-x86_64 6bf226944684f56c84dd014e8b979d27425c0148f61b3bd99bcc6f39e9dc5a47
  install -m 755 "$download_dir/hadolint" "$tool_dir/hadolint"
fi
# YAML parsing and official ServiceMonitor CRD conversion are tooling-only dependencies.
yaml_python="$download_dir/yaml/bin/python"
if [[ ! -x "$yaml_python" ]] || ! "$yaml_python" -c 'import yaml; assert yaml.__version__ == "6.0.3"' 2>/dev/null; then
  python3 -m venv "$download_dir/yaml"
  "$yaml_python" -m pip install --disable-pip-version-check --quiet PyYAML==6.0.3
fi
echo "Deployment validators installed under $tool_dir (Helm 3.19.0, kubeconform 0.7.0, hadolint 2.14.0; tooling PyYAML 6.0.3)."
