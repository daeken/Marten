"""No test invokes the deployment CLI or interacts with port 7450."""

from dataclasses import asdict, replace
from http.server import BaseHTTPRequestHandler, HTTPServer
import json
import os
from pathlib import Path
import runpy
import signal
import socket
import subprocess
import sys
import tempfile
import threading
from types import SimpleNamespace
import unittest
from unittest.mock import patch


cutover = SimpleNamespace(**runpy.run_path(str(Path(__file__).resolve().parents[1] / 'scripts/marten-cutover.py')))
ROOT = Path('/scratch/Marten')
OLD = cutover.Generation(1234, cutover.Birth(os.getuid(), os.getuid(), 42, 17, 18, 19),
                         f'dotnet {cutover.DLL}', str(ROOT))


class Fake:
    def __init__(self, delay=None, advance=lambda host: None):
        self.now = 0
        self.term_at = None
        self.delay = delay
        self.generation = OLD
        self.sent = []
        self.extra = set()
        self.advance = advance

    def describe(self, pid):
        return self.generation

    def birth(self, pid):
        return self.generation.birth if self.generation else None

    def listeners(self, port):
        return self.extra | ({OLD.pid} if self.generation else set())

    def signal(self, old, sig):
        self.sent.append((old, sig, self.now))
        if sig == signal.SIGKILL:
            self.generation = None
        else:
            self.term_at = self.now
        return True

    def sleep(self, seconds):
        self.now += seconds
        if self.delay is not None and self.term_at is not None and self.now >= self.term_at + self.delay:
            self.generation = None
        self.advance(self)

    def stop(self, force=False):
        def wait(host, old, seconds):
            return cutover.wait_exit(host, old, seconds, lambda: self.now, self.sleep)

        with patch.dict(cutover.stop.__globals__, wait_exit=wait):
            cutover.stop(self, ROOT, 51000, OLD, 60, 30, force)


class DrainTests(unittest.TestCase):
    def test_exit_and_late_exit_only_term_even_with_force(self):
        for delay in (1, 60, 60.25, 90):
            with self.subTest(delay=delay):
                h = Fake(delay)
                h.stop(force=True)
                self.assertIsNone(h.generation)
                self.assertEqual(h.sent, [(OLD, signal.SIGTERM, 0)])
                self.assertEqual(h.now, delay)

    def test_exited_after_initial_preflight(self):
        h = Fake()
        h.generation = None
        h.stop(force=True)
        self.assertEqual(h.sent, [])

    def test_stuck_without_opt_in(self):
        h = Fake()
        with self.assertRaises(cutover.DrainTimeout):
            h.stop()
        self.assertEqual(h.sent, [(OLD, signal.SIGTERM, 0)])
        self.assertEqual(h.now, 90)

    def test_stuck_opt_in_kill_after_both_windows(self):
        h = Fake()
        h.stop(force=True)
        self.assertEqual(h.sent, [(OLD, signal.SIGTERM, 0), (OLD, signal.SIGKILL, 90)])

    def test_wrong_command_uid_cwd_or_multiple_listeners_before_term(self):
        for g, extra in ((replace(OLD, cwd='/tmp'), set()), (replace(OLD, command='other'), set()),
                         (replace(OLD, birth=replace(OLD.birth, uid=os.getuid() + 1)), set()), (OLD, {9})):
            with self.subTest(g=g, extra=extra):
                h = Fake()
                h.generation, h.extra = g, extra
                with self.assertRaises(cutover.CutoverError):
                    h.stop(force=True)
                self.assertEqual(h.sent, [])

    def test_recheck_before_kill(self):
        for g, extra in ((replace(OLD, birth=replace(OLD.birth, version=20)), set()),
                         (replace(OLD, cwd='/tmp'), set()), (OLD, {9})):
            with self.subTest(g=g, extra=extra):
                def advance(h):
                    if h.now >= 90:
                        h.generation, h.extra = g, extra
                h = Fake(advance=advance)
                with self.assertRaises(cutover.CutoverError):
                    h.stop(force=True)
                self.assertEqual(h.sent, [(OLD, signal.SIGTERM, 0)])

    def test_exit_during_escalation_recheck(self):
        h = Fake()
        describe = h.describe

        def exit_during_probe(pid):
            if h.now == 90:
                h.generation = None
            return describe(pid)

        h.describe = exit_during_probe
        h.stop(force=True)
        self.assertEqual(h.sent, [(OLD, signal.SIGTERM, 0)])

    def test_visible_and_invisible_occupied_port(self):
        h = Fake()
        with self.assertRaisesRegex(cutover.CutoverError, 'occupied'):
            cutover.check_port(h, 51000)
        h.generation = None
        with socket.socket() as s:
            s.bind(('127.0.0.1', 0))
            port = s.getsockname()[1]
            s.listen()
            with self.assertRaisesRegex(cutover.CutoverError, 'Cannot claim port'):
                cutover.check_port(h, port)
        cutover.check_port(h, port)


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory()
        self.addCleanup(self.dir.cleanup)
        self.home = Path(self.dir.name)
        self.job = self.home / 'job'
        self.job.mkdir(mode=0o700)
        self.mounted = self.home / 'mounted'
        (self.mounted / 'secrets').mkdir(parents=True)
        (self.mounted / 'secrets/ui-token').write_text('fake-token')
        (self.home / '.marten').symlink_to(self.mounted)
        self.log = self.home / '.marten-server.log'
        self.log.write_text('old log')
        self.log.chmod(0o640)
        self.root = self.home / 'checkout'
        dll = self.root / cutover.DLL
        dll.parent.mkdir(parents=True)
        dll.touch()
        self.old = replace(OLD, cwd=str(self.root))
        cutover.atomic_json(self.job / 'generation.json', asdict(self.old))
        self.args = SimpleNamespace(mode='release', job=self.job, expected_home=self.mounted,
                                    sha='a' * 40, notice_deadline=0, grace=60, late_wait=60,
                                    kill_after_grace=False)
        self.h = Fake()
        self.h.generation = self.old
        self.events = []
        self.child = SimpleNamespace(pid=4321)
        self.port_error = None
        self.health_error = None
        self.prebuild_error = None
        self.timeout = False
        self.addCleanup(patch.stopall)
        patch.object(Path, 'home', return_value=self.home).start()
        patch.dict(os.environ, {'MARTEN_HOME': str(self.mounted), **dict.fromkeys(cutover.UNSET, 'stale')}).start()
        patch.dict(cutover.deploy.__globals__, LOCK_DIR=self.home, prebuild=self.prebuild, stop=self.stop,
                   check_tree=self.check_tree, check_port=self.check_port, healthy=self.healthy).start()
        patch.object(subprocess, 'Popen', side_effect=self.launch).start()

    def check_tree(self, root, sha):
        self.assertEqual(root, self.root)
        self.assertEqual(sha, self.args.sha)

    def prebuild(self, root, sha, env):
        self.events.append('prebuild')
        self.check_tree(root, sha)
        self.assertTrue(all(key not in env for key in cutover.UNSET))
        self.assertEqual(self.log.read_text(), 'old log')
        if self.prebuild_error:
            raise self.prebuild_error

    def stop(self, *args):
        self.events.append('stop')
        if self.timeout:
            raise cutover.DrainTimeout('timed out')
        self.h.generation = None

    def check_port(self, host, port):
        self.events.append('check_port')
        if self.port_error:
            raise self.port_error

    def launch(self, argv, **kw):
        self.events.append('launch')
        self.assertEqual(argv, ['nohup', 'dotnet', cutover.DLL])
        self.assertEqual(kw['cwd'], self.root)
        self.assertTrue(kw['start_new_session'])
        self.assertEqual(kw['stdin'], subprocess.DEVNULL)
        self.assertEqual(kw['stderr'], subprocess.STDOUT)
        self.assertTrue(all(key not in kw['env'] for key in cutover.UNSET))
        self.assertEqual(self.log.stat().st_mode & 0o777, 0o640)
        self.assertEqual(self.log.read_text(), '')
        return self.child

    def healthy(self, host, child, port, token):
        self.events.append('healthy')
        self.assertEqual(child, self.child)
        self.assertEqual(token, 'fake-token')
        if self.health_error:
            raise self.health_error

    def deploy(self):
        return cutover.deploy(self.args, self.h, self.root)

    def result(self):
        path = self.job / 'result.json'
        self.assertEqual(path.stat().st_mode & 0o777, 0o600)
        self.assertEqual(list(self.job.glob('.result.json-*')), [])
        return json.loads(path.read_text())

    def test_release_and_no_duplicate_job(self):
        self.deploy()
        self.assertEqual(self.events, ['prebuild', 'stop', 'check_port', 'launch', 'healthy'])
        self.assertEqual(self.result()['new_pid'], self.child.pid)
        self.assertEqual(self.result()['state'], 'ok')
        with self.assertRaisesRegex(cutover.CutoverError, 'already used'):
            self.deploy()
        self.assertEqual(self.events.count('launch'), 1)

    def test_prebuild_failure_no_term(self):
        self.prebuild_error = cutover.CutoverError('build failed')
        with self.assertRaises(cutover.CutoverError):
            self.deploy()
        self.assertEqual(self.events, ['prebuild'])
        self.assertEqual(self.result()['stage'], 'prebuild')

    def test_taken_port_no_launch(self):
        self.port_error = cutover.CutoverError('port taken')
        with self.assertRaises(cutover.CutoverError):
            self.deploy()
        self.assertEqual(self.events, ['prebuild', 'stop', 'check_port'])
        self.assertIn('port taken', self.result()['detail'])

    def test_unhealthy_no_second_launch(self):
        self.health_error = cutover.CutoverError('new Marten unhealthy')
        with self.assertRaises(cutover.CutoverError):
            self.deploy()
        self.assertEqual(self.events.count('launch'), 1)
        self.assertEqual(self.result()['new_pid'], self.child.pid)
        self.assertEqual(self.result()['state'], 'failed')

    def test_resume_only_same_snapshot_after_exit(self):
        self.timeout = True
        with self.assertRaises(cutover.DrainTimeout):
            self.deploy()
        self.args.mode = 'resume'
        with self.assertRaisesRegex(cutover.CutoverError, 'not exited'):
            self.deploy()
        self.assertEqual(self.result()['state'], 'drain-timeout')
        self.h.generation = None
        good_sha = self.args.sha
        self.args.sha = 'b' * 40
        with self.assertRaisesRegex(cutover.CutoverError, 'exact job'):
            self.deploy()
        self.args.sha = good_sha
        self.events.clear()
        self.deploy()
        self.assertEqual(self.events, ['check_port', 'check_port', 'launch', 'healthy'])
        self.assertEqual(self.result()['state'], 'ok')


class HealthTests(unittest.TestCase):
    def test_loopback_ui_and_authenticated_status(self):
        paths = []

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                paths.append(self.path)
                ok = self.path == '/' or self.headers.get('Authorization') == 'Bearer scratch-token'
                self.send_response(200 if ok else 401)
                self.end_headers()
                if ok:
                    self.wfile.write(b'{}')

            def log_message(self, *args):
                pass

        with HTTPServer(('127.0.0.1', 0), Handler) as server:
            worker = threading.Thread(target=server.serve_forever, kwargs={'poll_interval': 0.01})
            worker.start()
            try:
                self.assertTrue(cutover.http_check(server.server_port, 'scratch-token', 1))
                self.assertFalse(cutover.http_check(server.server_port, 'wrong-token', 1))
            finally:
                server.shutdown()
                worker.join(2)
        self.assertEqual(paths, ['/', '/api/status', '/', '/api/status'])

    def test_unexpected_listener_does_not_get_health_request(self):
        host = Fake()
        child = SimpleNamespace(pid=4321, poll=lambda: None)
        with patch.dict(cutover.healthy.__globals__, http_check=lambda *args: self.fail('wrong listener')):
            with self.assertRaisesRegex(cutover.CutoverError, 'unexpected listener'):
                cutover.healthy(host, child, 51000, 'scratch-token')


class PrebuildTests(unittest.TestCase):
    def test_moved_remote_is_rejected_before_merge_or_build(self):
        calls = []

        def command(root, *args, **kwargs):
            calls.append(args)
            return {'symbolic-ref': 'main\n', 'rev-parse': 'b' * 40}.get(args[1])

        with patch.dict(cutover.prebuild.__globals__, command=command):
            with self.assertRaisesRegex(cutover.CutoverError, 'did not match the tested SHA'):
                cutover.prebuild(ROOT, 'a' * 40, {})
        self.assertEqual([args[1] for args in calls], ['symbolic-ref', 'diff', 'diff', 'fetch', 'rev-parse'])


@unittest.skipUnless(sys.platform == 'darwin' and os.getuid() != 0, 'requires unprivileged macOS')
class DarwinTests(unittest.TestCase):
    def test_kernel_refuses_mismatched_pidversion(self):
        h = cutover.Darwin()
        with subprocess.Popen(['/bin/sleep', '10']) as p:
            try:
                old = h.describe(p.pid)
                self.assertEqual(old.command, '/bin/sleep 10')
                self.assertEqual(old.cwd, os.getcwd())
                self.assertEqual(old.birth.uid, os.getuid())
                reused = replace(old, birth=replace(old.birth, version=old.birth.version ^ 1))
                self.assertFalse(h.signal(reused, signal.SIGKILL))
                self.assertIsNone(p.poll())
                self.assertTrue(h.signal(old, signal.SIGKILL))
                self.assertEqual(p.wait(3), -signal.SIGKILL)
                self.assertIsNone(h.birth(p.pid))
            finally:
                if p.poll() is None:
                    p.kill()  # only our unreaped child


if __name__ == '__main__':
    unittest.main()
