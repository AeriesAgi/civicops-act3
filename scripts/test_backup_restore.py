"""Recovery safety checks; no database, external provider or real credentials."""
import contextlib
import io
import json
import os
from pathlib import Path
import subprocess
import tarfile
import tempfile
import unittest
from unittest.mock import patch

import backup_restore as recovery


class RecoverySafetyTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(dir=os.environ.get('TMPDIR'))
        self.root = Path(self.temporary.name)
        self.target = self.root / 'target'
        self.environment = patch.dict(os.environ, {'CIVICOPS_STORAGE': 'file', 'CIVICOPS_DATA_DIR': str(self.target)}, clear=True)
        self.environment.start()

    def tearDown(self):
        self.environment.stop()
        self.temporary.cleanup()

    def archive(self, entries):
        payload = self.root / 'data.tar.gz'
        with tarfile.open(payload, 'w:gz') as archive:
            for name, kind in entries:
                info = tarfile.TarInfo(name)
                if kind == 'file':
                    info.size = 9
                    archive.addfile(info, io.BytesIO(b'synthetic'))
                else:
                    info.type = tarfile.SYMTYPE if kind == 'symlink' else tarfile.LNKTYPE
                    info.linkname = '../outside'
                    archive.addfile(info)
        return payload

    def test_archive_traversal_rejected_before_extraction(self):
        with self.assertRaises(ValueError):
            recovery.restore_file(self.archive([('good.json', 'file'), ('../outside', 'file')]))
        self.assertFalse((self.target / 'good.json').exists())
        self.assertFalse((self.root / 'outside').exists())

    def test_absolute_archive_entry_rejected(self):
        with self.assertRaises(ValueError):
            recovery.restore_file(self.archive([('/outside', 'file')]))

    def test_duplicate_archive_entry_rejected(self):
        with self.assertRaises(ValueError):
            recovery.restore_file(self.archive([('record.json', 'file'), ('record.json', 'file')]))

    def test_archive_dot_alias_rejected_before_any_extraction(self):
        with self.assertRaises(ValueError):
            recovery.restore_file(self.archive([('record.json', 'file'), ('./record.json', 'file')]))
        self.assertFalse((self.target / 'record.json').exists())

    def test_archive_repeated_separator_alias_rejected_before_any_extraction(self):
        with self.assertRaises(ValueError):
            recovery.restore_file(self.archive([('nested/record.json', 'file'), ('nested//record.json', 'file')]))
        self.assertFalse((self.target / 'nested' / 'record.json').exists())

    def test_archive_symlink_rejected(self):
        with self.assertRaises(ValueError):
            recovery.restore_file(self.archive([('link', 'symlink')]))

    def test_archive_hardlink_rejected(self):
        with self.assertRaises(ValueError):
            recovery.restore_file(self.archive([('link', 'hardlink')]))

    def test_nonempty_target_untouched(self):
        self.target.mkdir()
        existing = self.target / 'existing.json'
        existing.write_text('existing synthetic state')
        with self.assertRaises(ValueError):
            recovery.restore_file(self.archive([('record.json', 'file')]))
        self.assertEqual(existing.read_text(), 'existing synthetic state')
        self.assertFalse((self.target / 'record.json').exists())

    def test_checksum_failure_does_not_create_target(self):
        source = self.root / 'backup'
        source.mkdir()
        payload = source / 'data.tar.gz'
        payload.write_bytes(b'synthetic corruption')
        (source / 'manifest.json').write_text(json.dumps({'version': 1, 'backend': 'file', 'file': payload.name,
                                                        'sha256': 'incorrect', 'bytes': payload.stat().st_size, 'encryption': 'none'}))
        with self.assertRaises(ValueError):
            recovery.restore(source)
        self.assertFalse(self.target.exists())

    def test_manifest_cannot_select_external_payload(self):
        source = self.root / 'backup'
        source.mkdir()
        (source / 'manifest.json').write_text(json.dumps({'version': 1, 'backend': 'file', 'file': '../outside'}))
        with self.assertRaises(ValueError):
            recovery.restore(source)

    def test_secret_file_precedence_and_bounded_read(self):
        filename = self.root / 'mounted-secret'
        filename.write_text('synthetic mounted value\n')
        os.environ.update(CIVICOPS_DATABASE_URL='synthetic fallback', CIVICOPS_DATABASE_URL_FILE=str(filename))
        self.assertEqual(recovery.secret('CIVICOPS_DATABASE_URL'), 'synthetic mounted value')
        filename.write_text('x' * 16385)
        with self.assertRaises(ValueError):
            recovery.secret('CIVICOPS_DATABASE_URL')

    def test_url_credentials_are_libpq_environment_not_arguments(self):
        os.environ.update(CIVICOPS_DATABASE_URL='postgresql://synthetic:synthetic%3Bpassword@127.0.0.1:6543/synthetic?sslmode=require',
                          PGHOST='inherited-owner-host', PGPASSWORD='inherited-owner-value')
        env = recovery.postgres_env()
        self.assertEqual(env['PGHOST'], '127.0.0.1')
        self.assertEqual(env['PGPORT'], '6543')
        self.assertEqual(env['PGPASSWORD'], 'synthetic;password')
        self.assertEqual(env['PGSSLMODE'], 'require')

    def test_npgsql_quoted_password_and_tls_mapping(self):
        os.environ['CIVICOPS_DATABASE_URL'] = 'Host=127.0.0.1;Database=synthetic;Username=synthetic;Password="synthetic;value";SSL Mode=VerifyFull;Maximum Pool Size=3'
        env = recovery.postgres_env()
        self.assertEqual(env['PGPASSWORD'], 'synthetic;value')
        self.assertEqual(env['PGSSLMODE'], 'verify-full')

    def test_postgres_restore_ignores_inherited_database_in_arguments_and_environment(self):
        os.environ.update(CIVICOPS_STORAGE='postgres', CIVICOPS_DATABASE_URL='Host=127.0.0.1;Database=synthetic-configured;Username=synthetic',
                          PGDATABASE='synthetic-inherited-system', PGHOST='inherited-owner-host')
        source = self.root / 'backup'
        source.mkdir()
        payload = source / 'database.dump'
        payload.write_bytes(b'synthetic custom dump fixture')
        (source / 'manifest.json').write_text(json.dumps({'version': 1, 'backend': 'postgres', 'file': payload.name,
            'sha256': recovery.digest(payload), 'bytes': payload.stat().st_size, 'encryption': 'none'}))
        results = [subprocess.CompletedProcess(['psql'], 0, stdout='0\n'),
                   subprocess.CompletedProcess(['pg_restore'], 0, stdout='')]
        with patch.object(recovery, 'pg_binary', side_effect=lambda name: name), \
                patch.object(recovery.subprocess, 'run', side_effect=results) as commands, \
                contextlib.redirect_stdout(io.StringIO()):
            recovery.restore(source)
        self.assertEqual(commands.call_count, 2)
        restore = commands.call_args_list[1]
        command = restore.args[0]
        self.assertEqual(command[command.index('--dbname') + 1], 'synthetic-configured')
        for call in commands.call_args_list:
            self.assertEqual(call.kwargs['env']['PGDATABASE'], 'synthetic-configured')
            self.assertEqual(call.kwargs['env']['PGHOST'], '127.0.0.1')
            self.assertNotIn('synthetic-inherited-system', call.args[0])

    def test_backup_symlink_rejected_and_incomplete_destination_removed(self):
        self.target.mkdir()
        outside = self.root / 'outside.json'
        outside.write_text('synthetic outside value')
        (self.target / 'link.json').symlink_to(outside)
        destination = self.root / 'backup'
        with self.assertRaises(ValueError), contextlib.redirect_stdout(io.StringIO()):
            recovery.backup(destination)
        self.assertFalse(destination.exists())


if __name__ == '__main__':
    unittest.main()
