#!/usr/bin/env bash
# Render the pinned public widget with synthetic, intercepted provider replies; no provider request is sent.
set -euo pipefail
repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
export PATH="$HOME/.dotnet:$HOME/.local/bin:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export TMPDIR="${TMPDIR:-/tmp}"
mkdir -p "$TMPDIR"
widget_dir=$(mktemp -d "$TMPDIR/civicops-widget-csp.XXXXXX")
widget_pid=''
cleanup_widget() { if [[ -n "$widget_pid" ]]; then kill "$widget_pid" 2>/dev/null || true; wait "$widget_pid" 2>/dev/null || true; fi; rm -rf "$widget_dir"; }
trap cleanup_widget EXIT
widget_dll="$repo_root/products/civicops/bin/Release/net10.0/CivicOps.dll"
[[ -f "$widget_dll" ]] || { echo 'Build Release before running the widget CSP smoke.' >&2; exit 1; }
# This is a pinned public package download, never a provider API call. Verify the npm published archive integrity.
curl --fail --silent --show-error --location --connect-timeout 10 --max-time 30 --retry 1 \
  'https://registry.npmjs.org/@elevenlabs/convai-widget-embed/-/convai-widget-embed-0.19.0.tgz' -o "$widget_dir/widget.tgz"
python3 - "$widget_dir/widget.tgz" "$widget_dir" <<'PY'
import base64,hashlib,pathlib,sys,tarfile
archive=pathlib.Path(sys.argv[1]); root=pathlib.Path(sys.argv[2])
expected='thhJfRgiOl8GaJZDhv/wViPK+IM7nKNC9onAiKtllBRJR2q92cXNqgAM2Yil+GVwUm459M79b083SydcpvdDyA=='
assert base64.b64encode(hashlib.sha512(archive.read_bytes()).digest()).decode()==expected,'Pinned widget archive checksum mismatch.'
with tarfile.open(archive) as package:
    bundle=package.extractfile('package/dist/index.js')
    assert bundle is not None
    (root/'widget.js').write_bytes(bundle.read())
PY
python3 - "$widget_dir" "$widget_dll" "$repo_root" > "$widget_dir/server.log" 2>&1 <<'PY' &
import os,pathlib,socket,sys
root=pathlib.Path(sys.argv[1]); env=os.environ.copy()
# Nothing configured in the owner's shell may enter this synthetic provider-rendering fixture.
for key in list(env):
    if key.startswith(('CIVICOPS_','ELEVENLABS_','ZENDESK_','Logging__','ASPNETCORE_')) or key=='PUBLIC_BASE_URL':env.pop(key)
with socket.socket() as listener:listener.bind(('127.0.0.1',0));port=listener.getsockname()[1]
base=f'http://127.0.0.1:{port}'
env.update({'ASPNETCORE_URLS':base,'ASPNETCORE_ENVIRONMENT':'Testing','CIVICOPS_DATA_DIR':str(root/'data'),
'CIVICOPS_DEMO_MODE':'false','CIVICOPS_EMAIL_MODE':'outbox','CIVICOPS_SECRET_PROVIDER':'env',
'ELEVENLABS_API_KEY':'synthetic-widget-csp-test','ELEVENLABS_AGENT_ID':'synthetic-widget',
'ELEVENLABS_SESSION_MODE':'public','Logging__LogLevel__Default':'Warning'})
(root/'base-url').write_text(base)
os.execvpe('dotnet',['dotnet',sys.argv[2],'--contentRoot',str(pathlib.Path(sys.argv[3])/'products/civicops')],env)
PY
widget_pid=$!
export CIVICOPS_BROWSER_BASE_URL
for attempt in $(seq 1 60); do
  if [[ -f "$widget_dir/base-url" ]]; then
    CIVICOPS_BROWSER_BASE_URL=$(cat "$widget_dir/base-url")
    if curl --fail --silent "$CIVICOPS_BROWSER_BASE_URL/healthz" > /dev/null; then break; fi
  fi
  if ! kill -0 "$widget_pid" 2>/dev/null; then cat "$widget_dir/server.log"; exit 1; fi
  sleep .2
done
curl --fail --silent "$CIVICOPS_BROWSER_BASE_URL/healthz" > /dev/null
cd "$repo_root"
node scripts/widget_csp_audit.mjs "$widget_dir/widget.js"
