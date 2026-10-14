#!/usr/bin/env python3
"""Offline recovery for CivicOps; checksums are integrity checks, not authentication."""
import argparse
import contextlib
import datetime
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import urllib.parse


def secret(name):
    filename = os.environ.get(name + '_FILE')
    if filename:
        path = Path(filename)
        if not path.is_absolute() or not 0 < path.stat().st_size <= 16384:
            raise ValueError('Invalid secret file')
        value = path.read_text().rstrip('\r\n')
    else:
        value = os.environ.get(name, '')
    if not value or len(value.encode()) > 16384 or any(ord(c) < 32 for c in value):
        raise ValueError('Required secret is absent or invalid')
    return value


def postgres_env():
    value = secret('CIVICOPS_DATABASE_URL')
    env = os.environ.copy()
    # Clear inherited libpq connection settings to avoid accidentally selecting a system database.
    for name in list(env):
        if name.startswith('PG'):
            env.pop(name)
    env.update(PGCONNECT_TIMEOUT='10', PGAPPNAME='civicops-recovery')
    if value.startswith(('postgres://', 'postgresql://')):
        uri = urllib.parse.urlsplit(value)
        if not uri.hostname or not uri.path.strip('/') or uri.fragment:
            raise ValueError('Invalid PostgreSQL URL')
        env.update(PGHOST=uri.hostname, PGPORT=str(uri.port or 5432),
                   PGDATABASE=urllib.parse.unquote(uri.path[1:]), PGUSER=urllib.parse.unquote(uri.username or ''))
        if uri.password:
            env['PGPASSWORD'] = urllib.parse.unquote(uri.password)
        for key, val in urllib.parse.parse_qsl(uri.query):
            if key == 'sslmode':
                env['PGSSLMODE'] = val
            else:
                raise ValueError('Unsupported PostgreSQL URL query setting')
    else:
        # Parse Npgsql key=value syntax including quoted values and embedded semicolons.
        pattern = re.compile(r'\s*([^=;]+)\s*=\s*(?:"((?:[^"]|"")*)"|\'((?:[^\']|\'\')*)\'|([^;]*))\s*(?:;|$)')
        offset = 0
        allowed = {'host': 'PGHOST', 'port': 'PGPORT', 'database': 'PGDATABASE', 'username': 'PGUSER',
                   'user id': 'PGUSER', 'user': 'PGUSER', 'password': 'PGPASSWORD', 'ssl mode': 'PGSSLMODE',
                   'sslmode': 'PGSSLMODE', 'root certificate': 'PGSSLROOTCERT'}
        ignored = {'pooling', 'timeout', 'command timeout', 'include error detail', 'maximum pool size',
                   'minimum pool size', 'application name', 'search path', 'no reset on close'}
        while offset < len(value.rstrip()):
            match = pattern.match(value, offset)
            if not match:
                raise ValueError('Unsupported database connection syntax')
            offset = match.end()
            key = match[1].strip().lower()
            val = next(part for part in match.groups()[1:] if part is not None).strip()
            val = val.replace('""', '"').replace("''", "'")
            if key in allowed:
                env[allowed[key]] = val.lower() if allowed[key] == 'PGSSLMODE' else val
            elif key not in ignored:
                raise ValueError('Unsupported database connection option')
        if not all(env.get(name) for name in ('PGHOST', 'PGDATABASE', 'PGUSER')):
            raise ValueError('Explicit host, database and username are required')
        if env.get('PGSSLMODE') == 'verifyfull':
            env['PGSSLMODE'] = 'verify-full'
        if env.get('PGSSLMODE') == 'verifyca':
            env['PGSSLMODE'] = 'verify-ca'
    return env


def pg_binary(name):
    configured = Path(os.environ.get('CIVICOPS_PG_BIN', '/usr/lib/postgresql/17/bin')) / name
    resolved = str(configured) if configured.is_file() else shutil.which(name)
    if not resolved:
        raise ValueError('PostgreSQL client binaries are required')
    return resolved


def run_pg(name, args, capture=False, use_database=False):
    connection = postgres_env()
    if use_database:
        # Use the same validated connection snapshot for arguments and libpq environment.
        args = ['--dbname', connection['PGDATABASE'], *args]
    result = subprocess.run([pg_binary(name), *args], env=connection, capture_output=True, text=True)
    if result.returncode:
        # Never print libpq stderr, which may contain connection values.
        raise ValueError('PostgreSQL recovery command failed; inspect database operator logs')
    return result.stdout if capture else None


@contextlib.contextmanager
def file_lock(directory):
    if directory.is_symlink():
        raise ValueError('Data directory must not be a symlink')
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / '.civicops.lock'
    if path.is_symlink():
        raise ValueError('Data lock must not be a symlink')
    with path.open('a+b') as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise ValueError('Stop CivicOps before file backup/restore') from None
        yield


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def data_path():
    value = os.environ.get('CIVICOPS_DATA_DIR')
    if not value:
        raise ValueError('CIVICOPS_DATA_DIR must be explicit for file recovery')
    return Path(value).absolute()


def backend():
    selected = os.environ.get('CIVICOPS_STORAGE', 'file') or 'file'
    if selected not in ('file', 'postgres'):
        raise ValueError('CIVICOPS_STORAGE must be file or postgres')
    return selected


def backup(destination):
    mode = backend()
    if destination.exists() or destination.is_symlink():
        raise ValueError('Backup destination must be new')
    destination.mkdir(parents=True, mode=0o700)
    try:
        payload = destination / ('data.tar.gz' if mode == 'file' else 'database.dump')
        if mode == 'file':
            source = data_path()
            if destination.resolve().is_relative_to(source.resolve()):
                raise ValueError('Backup destination must be outside the data directory')
            if not source.is_dir():
                raise ValueError('Data directory does not exist')
            with file_lock(source), tarfile.open(payload, 'w:gz', dereference=False) as archive:
                for path in sorted(source.rglob('*')):
                    if path.name == '.civicops.lock' or path.name.endswith('.tmp'):
                        continue
                    if path.is_symlink() or not (path.is_file() or path.is_dir()):
                        raise ValueError('Data contains unsupported links or special files')
                    if path.is_file():
                        archive.add(path, arcname=path.relative_to(source), recursive=False)
        else:
            run_pg('pg_dump', ['--format=custom', '--no-owner', '--no-acl', '--file', str(payload)])
        recipient = os.environ.get('CIVICOPS_BACKUP_AGE_RECIPIENT')
        encryption = 'none'
        if recipient:
            age = shutil.which('age')
            if not age:
                raise ValueError('age is required when encryption is configured')
            encrypted = payload.with_name(payload.name + '.age')
            result = subprocess.run([age, '--recipient', recipient, '--output', str(encrypted), str(payload)], capture_output=True)
            if result.returncode:
                raise ValueError('Backup encryption failed')
            payload.unlink()
            payload = encrypted
            encryption = 'age'
        payload.chmod(0o600)
        manifest = {'version': 1, 'backend': mode, 'createdAt': datetime.datetime.now(datetime.timezone.utc).isoformat(),
                    'file': payload.name, 'bytes': payload.stat().st_size, 'sha256': digest(payload), 'encryption': encryption,
                    'consistency': 'application-stopped file lock' if mode == 'file' else 'pg_dump MVCC snapshot',
                    'integrity': 'checksum only; protect manifest and backup together'}
        (destination / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
        (destination / 'manifest.json').chmod(0o600)
    except BaseException:
        shutil.rmtree(destination)
        raise
    print('PASS: backup created with SHA-256 manifest (' + mode + ')')


def restore_file(payload):
    target = data_path()
    with file_lock(target):
        if any(path.name != '.civicops.lock' for path in target.iterdir()):
            raise ValueError('Restore requires an empty data directory')
        with tarfile.open(payload, 'r:gz') as archive:
            members = archive.getmembers()
            names = set()
            for member in members:
                path = Path(member.name)
                if not member.isfile() or path.is_absolute() or '..' in path.parts or path.name == '.civicops.lock' \
                        or path.as_posix() != member.name or member.name in names:
                    raise ValueError('Backup contains unsafe archive entry')
                names.add(member.name)
            # Extraction to staging means a corrupt tar cannot leave half-restored data.
            with tempfile.TemporaryDirectory(prefix='.civicops-restore-', dir=target.parent) as staging:
                archive.extractall(staging, members=members, filter='data')
                for child in Path(staging).iterdir():
                    shutil.move(str(child), target / child.name)


def restore(source):
    mode = backend()
    manifest_path = source / 'manifest.json'
    if manifest_path.is_symlink() or manifest_path.stat().st_size > 65536:
        raise ValueError('Invalid backup manifest')
    manifest = json.loads(manifest_path.read_text())
    if manifest.get('version') != 1 or manifest.get('backend') != mode:
        raise ValueError('Backup version/backend mismatch')
    name = manifest.get('file', '')
    allowed = ('data.tar.gz', 'database.dump', 'data.tar.gz.age', 'database.dump.age')
    if name not in allowed:
        raise ValueError('Invalid backup payload name')
    payload = source / name
    if payload.is_symlink() or not payload.is_file() or payload.stat().st_size != manifest.get('bytes') or digest(payload) != manifest.get('sha256'):
        raise ValueError('Backup checksum verification failed')
    with tempfile.TemporaryDirectory(prefix='civicops-decrypt-', dir=os.environ.get('TMPDIR')) as temporary:
        if manifest.get('encryption') == 'age':
            age = shutil.which('age')
            identity = os.environ.get('CIVICOPS_BACKUP_AGE_IDENTITY_FILE')
            if not age or not identity or not Path(identity).is_absolute():
                raise ValueError('age and an absolute identity file are required')
            plain = Path(temporary) / 'payload'
            result = subprocess.run([age, '--decrypt', '--identity', identity, '--output', str(plain), str(payload)], capture_output=True)
            if result.returncode:
                raise ValueError('Backup decryption failed')
            payload = plain
        elif manifest.get('encryption') != 'none':
            raise ValueError('Unsupported backup encryption')
        if mode == 'file':
            restore_file(payload)
        else:
            count = run_pg('psql', ['--no-psqlrc', '--tuples-only', '--no-align', '--command',
                                    "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_toast%' AND c.relkind IN ('r','p','v','m','S');"], capture=True)
            if count.strip() != '0':
                raise ValueError('Restore requires an empty database; stop all CivicOps instances first')
            run_pg('pg_restore', ['--exit-on-error', '--single-transaction', '--no-owner', '--no-acl', str(payload)], use_database=True)
    print('PASS: checksum verified and backup restored (' + mode + ')')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('operation', choices=('backup', 'restore'))
    parser.add_argument('directory', type=Path)
    args = parser.parse_args()
    try:
        if args.operation == 'backup':
            backup(args.directory.absolute())
        else:
            restore(args.directory.absolute())
    except (ValueError, OSError, tarfile.TarError, json.JSONDecodeError):
        # Exception messages from filesystem/database libraries may include sensitive paths/values.
        print('FAIL: recovery precondition, payload integrity or tool execution failed. See operations guide.', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
