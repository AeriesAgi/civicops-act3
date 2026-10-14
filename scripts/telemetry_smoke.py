#!/usr/bin/env python3
"""Use real OTLP/HTTP exporters with a disposable loopback-only sink."""
import http.server
import argparse
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request


def main(mode):
    root = Path(__file__).resolve().parent.parent
    database = None
    if mode == 'postgres':
        database = os.environ.get('CIVICOPS_TEST_DATABASE_URL')
        if os.environ.get('CIVICOPS_EPHEMERAL_PG') != '1' or not database:
            raise RuntimeError('Postgres telemetry drill requires with_ephemeral_pg.sh and its test database URL')
        settings = dict(part.split('=', 1) for part in database.split(';') if part)
        if settings.get('Host') != '127.0.0.1' or settings.get('Port') != os.environ.get('PGPORT') or settings.get('Database') != 'civicops_test':
            raise RuntimeError('Postgres telemetry drill is restricted to the wrapper-owned loopback test database')
        # Trust authentication ignores this synthetic password; its only purpose is leak detection.
        database += ';Password=synthetic-telemetry-db-password-sentinel'
    received = {}
    gate = threading.Lock()

    class Collector(http.server.BaseHTTPRequestHandler):
        def do_POST(self):
            assert self.path in ('/v1/traces', '/v1/metrics', '/v1/logs'), self.path
            content = self.rfile.read(int(self.headers['Content-Length']))
            with gate:
                received.setdefault(self.path, []).append(content)
            self.send_response(200)
            self.send_header('Content-Type', 'application/x-protobuf')
            self.send_header('Content-Length', '0')
            self.end_headers()

        def log_message(self, *args):
            pass

    collector = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Collector)
    threading.Thread(target=collector.serve_forever, daemon=True).start()
    try:
        with tempfile.TemporaryDirectory(prefix='civicops-telemetry-smoke-', dir=os.environ['TMPDIR']) as temporary:
            work = Path(temporary)
            env = {key: value for key, value in os.environ.items() if not key.startswith(('CIVICOPS_', 'ELEVENLABS_', 'ZENDESK_', 'OTEL_'))}
            env.update(CIVICOPS_DEMO_MODE='false', CIVICOPS_STORAGE=mode, CIVICOPS_DATA_DIR=str(work / 'data'),
                       CIVICOPS_SECRET_PROVIDER='env', CIVICOPS_EMAIL_MODE='outbox', CIVICOPS_TELEMETRY='otlp',
                       OTEL_EXPORTER_OTLP_ENDPOINT=f'http://127.0.0.1:{collector.server_port}', OTEL_EXPORTER_OTLP_PROTOCOL='http/protobuf',
                       OTEL_BSP_SCHEDULE_DELAY='100', OTEL_BLRP_SCHEDULE_DELAY='100', OTEL_METRIC_EXPORT_INTERVAL='500',
                       OTEL_SERVICE_NAME='civicops-synthetic-telemetry', CIVICOPS_INFORMATION_OFFICER_CONTACT='',
                       CIVICOPS_LLM_API_KEY='synthetic-telemetry-sensitive-sentinel')
            if database:
                env['CIVICOPS_DATABASE_URL'] = database
            listener = socket.socket()
            listener.bind(('127.0.0.1', 0))
            port = listener.getsockname()[1]
            listener.close()
            base = f'http://127.0.0.1:{port}'
            env['ASPNETCORE_URLS'] = base

            def request(path, body=None):
                data = None if body is None else json.dumps(body).encode()
                req = urllib.request.Request(base + path, data=data, headers={'Content-Type': 'application/json',
                    'X-Correlation-ID': 'synthetic-telemetry-correlation',
                    'traceparent': '00-1234567890abcdef1234567890abcdef-1234567890abcdef-01'})
                try:
                    response = urllib.request.urlopen(req, timeout=15)
                except urllib.error.HTTPError as error:
                    response = error
                with response:
                    return response.status, json.load(response), response.headers

            with (work / 'server.log').open('w') as log:
                process = subprocess.Popen(['dotnet', str(root / 'products/civicops/bin/Release/net10.0/CivicOps.dll'),
                    '--contentRoot', str(root / 'products/civicops')], env=env, stdout=log, stderr=subprocess.STDOUT)
                try:
                    for _ in range(100):
                        if process.poll() is not None:
                            raise RuntimeError('Telemetry smoke application failed to start')
                        try:
                            request('/health/live')
                            break
                        except (urllib.error.URLError, ConnectionError):
                            time.sleep(.1)
                    status, body, headers = request('/api/incidents', {'description':
                        'Synthetic pipe leak; email resident@example.invalid; phone 082 555 0100',
                        'area': 'Demo Telemetry', 'privacyConsentAccepted': True, 'privacyConsentVersion': 'civicops-popia-v1'})
                    assert status == 201, (status, body)
                    assert headers['X-Correlation-ID'] == 'synthetic-telemetry-correlation'
                    assert '1234567890abcdef1234567890abcdef' in headers['traceparent']
                    status, problem, _ = request('/api/incidents/synthetic-missing?probe=synthetic-telemetry-sensitive-sentinel&email=resident@example.invalid')
                    assert status == 404 and problem['correlationId'] == 'synthetic-telemetry-correlation'
                    assert problem['traceId'] == '1234567890abcdef1234567890abcdef'
                    status, ready, _ = request('/health/ready')
                    assert status == 200 and ready['status'] == 'degraded'
                    for _ in range(100):
                        with gate:
                            complete = all(received.get(path) for path in ('/v1/traces', '/v1/metrics', '/v1/logs'))
                            if complete and mode == 'postgres':
                                traces = b'\n'.join(received['/v1/traces'])
                                metrics = b'\n'.join(received['/v1/metrics'])
                                complete = b'database.command' in traces and b'Npgsql' in traces and b'db.client.operation.duration' in metrics and b'db.client.connection.count' in metrics
                        if complete:
                            break
                        time.sleep(.1)
                finally:
                    process.terminate()
                    process.wait(timeout=20)
            with gate:
                assert all(received.get(path) for path in ('/v1/traces', '/v1/metrics', '/v1/logs')), list(received)
                data = b'\n'.join(payload for batches in received.values() for payload in batches)
                traces = b'\n'.join(received['/v1/traces'])
                metrics = b'\n'.join(received['/v1/metrics'])
                logs = b'\n'.join(received['/v1/logs'])
            for sentinel in (b'resident@example.invalid', b'082 555 0100', b'synthetic-telemetry-sensitive-sentinel', b'synthetic-telemetry-db-password-sentinel'):
                assert sentinel not in data and sentinel.decode() not in (work / 'server.log').read_text(), 'Sensitive sentinel reached telemetry/log output'
            assert b'agent.IntakeAgent' in traces and b'agent.TriageAgent' in traces, 'Agent spans absent'
            assert b'civicops.intake.total' in metrics and b'civicops.ai.fallbacks' in metrics, 'Custom metrics absent'
            assert b'synthetic-telemetry-correlation' in logs, 'Correlation scope absent from OTLP logs'
            assert b'civicops-synthetic-telemetry' in data, 'Standard service resource absent'
            for attribute in (b'url.full', b'url.path', b'url.query', b'http.url', b'http.target', b'db.statement', b'db.query.text'):
                assert attribute not in traces, 'Raw URL/SQL span attribute reached collector'
            if mode == 'postgres':
                assert b'Npgsql' in traces and b'database.command' in traces, 'Native Npgsql spans or fixed SQL command names absent'
                assert b'Npgsql' in metrics and b'db.client.operation.duration' in metrics and b'db.client.connection.count' in metrics, 'Native Npgsql operation/pool metrics absent'
                assert b'civicops-storage' in metrics, 'Safe explicit database pool label absent'
                assert database.encode() not in data, 'Database connection configuration reached collector'
                for sql in (b'SELECT ', b'INSERT INTO ', b'UPDATE civicops_', b'civicops_documents'):
                    assert sql not in traces, 'Raw CivicOps SQL reached trace collector'
            database_signals = '; native Npgsql traces/metrics, fixed SQL names and database-secret redaction' if mode == 'postgres' else ''
            print('PASS: ' + mode + ' real OTLP/HTTP traces, metrics and logs; agent spans, intake/fallback metrics, correlation, '
                  'ProblemDetails, degraded readiness and PII/secret sentinel redaction' + database_signals + '; loopback collector only')
    finally:
        collector.shutdown()
        collector.server_close()


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('storage', nargs='?', default='file', choices=('file', 'postgres'))
    main(parser.parse_args().storage)
