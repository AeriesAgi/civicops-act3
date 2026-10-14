#!/usr/bin/env python3
"""Synthetic stopped-app recovery drill, with exact durable artifact and HTTP count checks."""
import hashlib
from datetime import datetime, timedelta, timezone
from http.cookies import SimpleCookie
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

import backup_restore


def main(mode):
    root = Path(__file__).resolve().parent.parent
    with tempfile.TemporaryDirectory(prefix='civicops-restore-smoke-', dir=os.environ['TMPDIR']) as temporary:
        work = Path(temporary)
        env = {key: value for key, value in os.environ.items() if not key.startswith(('CIVICOPS_', 'ELEVENLABS_', 'ZENDESK_', 'OTEL_'))}
        env.update(CIVICOPS_DEMO_MODE='true', CIVICOPS_STORAGE=mode, CIVICOPS_DATA_DIR=str(work / 'data'),
                   CIVICOPS_SECRET_PROVIDER='env', CIVICOPS_EMAIL_MODE='outbox', CIVICOPS_TELEMETRY='off',
                   CIVICOPS_INTAKE_LIMIT='1000', CIVICOPS_DISPATCH_LIMIT='1000', CIVICOPS_AUTH_LIMIT='1000',
                   CIVICOPS_TRAINING_LIMIT='1000')
        if mode == 'postgres':
            # Wrapper provides a disposable database; never touch an arbitrary owner database.
            if not os.environ.get('CIVICOPS_EPHEMERAL_PG'):
                raise RuntimeError('Postgres drill requires with_ephemeral_pg.sh')
            env['CIVICOPS_DATABASE_URL'] = os.environ.get('CIVICOPS_TEST_DATABASE_URL') or os.environ['CIVICOPS_DATABASE_URL']
        encrypted = os.environ.get('CIVICOPS_SMOKE_ENCRYPTION') == 'age'
        if encrypted:
            keygen = shutil.which('age-keygen')
            if not keygen or not shutil.which('age'):
                raise RuntimeError('Selected age drill requires age and age-keygen')
            identity = work / 'synthetic-recovery-identity'
            subprocess.run([keygen, '--output', str(identity)], check=True, capture_output=True)
            recipient = subprocess.run([keygen, '-y', str(identity)], check=True, capture_output=True, text=True).stdout.strip()
            env.update(CIVICOPS_BACKUP_AGE_RECIPIENT=recipient, CIVICOPS_BACKUP_AGE_IDENTITY_FILE=str(identity))
        listener = socket.socket()
        listener.bind(('127.0.0.1', 0))
        port = listener.getsockname()[1]
        listener.close()
        env['ASPNETCORE_URLS'] = f'http://127.0.0.1:{port}'
        base = env['ASPNETCORE_URLS']
        cookies = {}
        csrf = None
        checks = 0

        def require(value, message):
            nonlocal checks
            if not value and (work / 'create.log').exists():
                print((work / 'create.log').read_text()[-6000:], file=sys.stderr)
            assert value, message
            checks += 1

        def request(path, body=None):
            headers = {'Content-Type': 'application/json', 'Cookie': '; '.join(k + '=' + v for k, v in cookies.items())}
            if csrf:
                headers['X-CivicOps-CSRF'] = csrf
            data = None if body is None else json.dumps(body).encode()
            req = urllib.request.Request(base + path, data=data, headers=headers)
            with urllib.request.urlopen(req, timeout=15) as response:
                for value in response.headers.get_all('Set-Cookie', []):
                    parsed = SimpleCookie()
                    parsed.load(value)
                    cookies.update({key: item.value for key, item in parsed.items()})
                return json.load(response)

        def start(label):
            log = (work / (label + '.log')).open('w')
            process = subprocess.Popen(['dotnet', str(root / 'products/civicops/bin/Release/net10.0/CivicOps.dll'),
                                        '--contentRoot', str(root / 'products/civicops')], env=env, stdout=log, stderr=subprocess.STDOUT)
            for _ in range(100):
                if process.poll() is not None:
                    raise RuntimeError('Restore smoke application failed to start; temporary server log retained during exception')
                try:
                    request('/healthz')
                    return process, log
                except (urllib.error.URLError, ConnectionError):
                    time.sleep(0.1)
            process.terminate()
            process.wait(timeout=15)
            raise RuntimeError('Restore smoke application startup timeout')

        def stop(process, log):
            process.terminate()
            process.wait(timeout=20)
            log.close()

        def snapshot():
            return {name: request(path) for name, path in {
                'incidents': '/api/incidents', 'audit': '/api/audit', 'verify': '/api/audit/verify',
                'tenants': '/api/admin/tenants', 'crews': '/api/admin/crews', 'workers': '/api/admin/workers',
                'accounts': '/api/admin/service-accounts', 'privacy': '/api/admin/privacy/requests',
                'outbox': '/api/integrations/outbox'}.items()}

        def login():
            nonlocal csrf
            csrf = request('/auth/status')['csrfToken']
            request('/auth/demo-login', {'role': 'admin'})
            csrf = request('/auth/status')['csrfToken']

        process, log = start('create')
        try:
            login()
            cases = []
            for description in ('Synthetic pipe leak at demo library', 'Synthetic damaged road at demo hall'):
                cases.append(request('/api/incidents', {'description': description, 'area': 'Demo Recovery',
                    'contactEmail': 'synthetic@example.invalid', 'consentToContact': True,
                    'privacyConsentAccepted': True, 'privacyConsentVersion': 'civicops-popia-v1'})['incident'])
            request('/api/admin/privacy/requests', {'referenceNumber': cases[0]['referenceNumber'], 'action': 'access',
                                                   'requestNote': 'Synthetic restore drill request'})
            request('/api/admin/service-accounts', {'name': 'Synthetic restore account', 'scopes': ['audit:read'],
                                                     'roles': ['auditor'], 'expiresAt': (datetime.now(timezone.utc) + timedelta(days=1)).isoformat()})
            for _ in range(100):
                queue = request('/api/integrations/outbox')
                items = queue if isinstance(queue, list) else queue.get('items', [])
                if items and all(item['state'] == 'Delivered' for item in items):
                    break
                time.sleep(.1)
            before = snapshot()
            require(before['verify']['valid'], 'Initial audit chain must verify')
            require(len(before['incidents']) == 2, 'Two synthetic cases must persist')
            require(len(before['privacy']) == 1, 'Privacy record must persist')
            require(len(before['accounts']) == 1, 'Service account must persist')
            cluster = os.environ.get('CIVICOPS_EPHEMERAL_PG_DIR') if mode == 'postgres' else None
            if cluster:
                cluster_path = Path(cluster).resolve()
                require(cluster_path.parent == Path(os.environ['TMPDIR']).resolve() and cluster_path.name.startswith('civicops-pg.'),
                        'Runtime outage drill must use the wrapper-owned cluster')
                ctl = backup_restore.pg_binary('pg_ctl')
                subprocess.run([ctl, '-D', str(cluster_path / 'data'), '-m', 'fast', '-w', 'stop'], check=True, capture_output=True)
                try:
                    # Let background polling observe the outage. Process must continue serving liveness.
                    time.sleep(3)
                    require(process.poll() is None, 'A database outage must not stop the application host')
                    require(request('/health/live')['status'] == 'healthy', 'Database outage must preserve liveness')
                    try:
                        request('/health/ready')
                        require(False, 'Database outage must fail readiness')
                    except urllib.error.HTTPError as error:
                        require(error.code == 503, 'Database outage must report 503 readiness')
                finally:
                    options = f'-p {os.environ["PGPORT"]} -h 127.0.0.1 -k {cluster_path / "socket"} -c max_connections=30 -c shared_buffers=32MB'
                    subprocess.run([ctl, '-D', str(cluster_path / 'data'), '-l', str(cluster_path / 'server.log'), '-o', options, '-w', 'start'],
                                   check=True, capture_output=True)
                ready = {'ready': False}
                for _ in range(50):
                    try:
                        ready = request('/health/ready')
                        if ready['ready']:
                            break
                    except urllib.error.HTTPError:
                        pass
                    time.sleep(.1)
                require(ready['ready'], 'Readiness must recover after database restart')
            # File backups must reject running application (actual .NET lease interoperability).
            if mode == 'file':
                running = subprocess.run([str(root / 'scripts/backup.sh'), str(work / 'running')], env=env, capture_output=True)
                require(running.returncode != 0, 'Running file store backup must fail closed')
        finally:
            stop(process, log)
        backup = work / 'backup'
        subprocess.run([str(root / 'scripts/backup.sh'), str(backup)], env=env, check=True)
        if mode == 'file':
            hashes = {str(path.relative_to(work / 'data')): hashlib.sha256(path.read_bytes()).hexdigest()
                      for path in (work / 'data').rglob('*') if path.is_file() and path.name != '.civicops.lock'}
            shutil.rmtree(work / 'data')
        else:
            # Empty disposable database. Do not invoke this path without wrapper marker above.
            saved = os.environ.copy()
            try:
                os.environ.update(env)
                backup_restore.run_pg('psql', ['--no-psqlrc', '--set', 'ON_ERROR_STOP=1', '--command',
                                               'DROP SCHEMA IF EXISTS civicops CASCADE; DROP SCHEMA public CASCADE; CREATE SCHEMA public;'])
            finally:
                os.environ.clear()
                os.environ.update(saved)
        # Detect a corrupt payload before mutating the empty target.
        manifest = json.loads((backup / 'manifest.json').read_text())
        require(manifest['encryption'] == ('age' if encrypted else 'none'), 'Requested encryption mode must be honest')
        payload = backup / manifest['file']
        original = payload.read_bytes()
        payload.write_bytes(original + b'corruption')
        failed = subprocess.run([str(root / 'scripts/restore.sh'), str(backup)], env=env, capture_output=True)
        require(failed.returncode != 0, 'Corrupt backup must reject restore')
        payload.write_bytes(original)
        if encrypted:
            wrong_identity = work / 'synthetic-wrong-identity'
            subprocess.run([keygen, '--output', str(wrong_identity)], check=True, capture_output=True)
            wrong_env = env | {'CIVICOPS_BACKUP_AGE_IDENTITY_FILE': str(wrong_identity)}
            failed = subprocess.run([str(root / 'scripts/restore.sh'), str(backup)], env=wrong_env, capture_output=True)
            require(failed.returncode != 0, 'Wrong recovery identity must reject decryption')
        subprocess.run([str(root / 'scripts/restore.sh'), str(backup)], env=env, check=True)
        if mode == 'file':
            restored = {str(path.relative_to(work / 'data')): hashlib.sha256(path.read_bytes()).hexdigest()
                        for path in (work / 'data').rglob('*') if path.is_file() and path.name != '.civicops.lock'}
            require(hashes == restored, 'Every durable file must restore exactly')
        cookies.clear()
        csrf = None
        process, log = start('restored')
        try:
            login()
            after = snapshot()
            require(after['verify']['valid'], 'Restored audit chain must verify')
            for name in before:
                require(before[name] == after[name], name + ' must restore exactly')
        finally:
            stop(process, log)
        print(f'PASS: {mode} create → backup → wipe → checksum rejection → restore → restart; {checks} checks; '
              f"{len(after['incidents'])} cases, {after['verify']['entries']} verified audit entries")


if __name__ == '__main__':
    main(sys.argv[1])
