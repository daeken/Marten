"""No test invokes the deployment CLI or interacts with port 7450."""

from contextlib import contextmanager
from dataclasses import asdict, replace
from http.server import BaseHTTPRequestHandler, HTTPServer, ThreadingHTTPServer
import json
import os
from pathlib import Path
import runpy
import select
import signal
import socket
import subprocess
import sys
import tempfile
from textwrap import dedent
import threading
import time
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
            drain = SimpleNamespace(wait_ready=lambda: None, confirm_ready=lambda: float('inf'), abort=lambda: None)
            cutover.stop(self, ROOT, 51000, OLD, 60, 30, force, drain)


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


class NoticeCliTests(unittest.TestCase):
    def invoke(self, mode, *extra):
        argv = ['cutover', mode, '--checkout', os.getcwd(), '--job', os.getcwd(),
                '--expected-home', os.getcwd(), '--sha', 'a' * 40, *extra]
        args_out = []
        with (
            patch.object(sys, 'argv', argv),
            patch.dict(os.environ),
            patch.object(time, 'time', return_value=10_000),
            patch.object(time, 'monotonic', return_value=400),
            patch.dict(cutover.main.__globals__, Darwin=lambda: None, deploy=lambda args, *_: args_out.append(args)),
        ):
            cutover.main()
        return args_out[0].notice_deadline

    def test_optional_or_explicit_deadline(self):
        self.assertIsNone(self.invoke('release'))
        self.assertEqual(self.invoke('release', '--not-before', '10015'), 415)
        self.assertEqual(self.invoke('release', '--not-before', '9995'), 395)

    def test_invalid_deadline_rejected_before_deploy(self):
        for mode, timestamp in (('release', '9819'), ('release', '13601'), ('resume', '10015')):
            with self.subTest(mode=mode, timestamp=timestamp), \
                 patch.object(sys, 'stderr'), self.assertRaises(SystemExit) as raised:
                self.invoke(mode, '--not-before', timestamp)
            self.assertEqual(raised.exception.code, 2)


    def test_legacy_pin_and_release_only_no_kill_gate(self):
        pinned = ('--legacy-first-install', cutover.LEGACY_FIRST_INSTALL)
        self.invoke('release', *pinned)
        for mode, flags in (('release', ('--legacy-first-install', 'a' * 40)),
                            ('resume', pinned), ('inspect', pinned),
                            ('release', (*pinned, '--kill-after-grace')),
                            ('release', (*pinned, '--sha', cutover.LEGACY_FIRST_INSTALL))):
            with self.subTest(mode=mode, flags=flags), \
                 patch.object(sys, 'stderr'), self.assertRaises(SystemExit) as raised:
                self.invoke(mode, *flags)
            self.assertEqual(raised.exception.code, 2)


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
        dll.write_bytes(b'compiled server')
        (dll.parent / 'Marten.runtimeconfig.json').write_text('{}')
        (dll.parent / 'Marten.deps.json').write_text('{}')
        (dll.parent / 'dependency.dll').write_bytes(b'compiled dependency')
        self.ui = self.root / cutover.WEB_DIST
        (self.ui / 'assets').mkdir(parents=True)
        (self.ui / 'index.html').write_text('<html>scratch</html>')
        (self.ui / 'assets/app.js').write_bytes(b'built UI')
        self.old = replace(OLD, cwd=str(self.root))
        cutover.atomic_json(self.job / 'generation.json', asdict(self.old))
        self.args = SimpleNamespace(mode='release', job=self.job, expected_home=self.mounted,
                                    sha='a' * 40, notice_deadline=None, grace=60, late_wait=60,
                                    kill_after_grace=False, legacy_first_install=None)
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
        patch.dict(os.environ, {'MARTEN_HOME': str(self.mounted), 'MARTEN_WEB_ROOT': '',
                                **dict.fromkeys(cutover.UNSET, 'stale')}).start()
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

    def test_release_without_deadline_and_no_duplicate_job(self):
        with patch.object(time, 'sleep') as sleep:
            self.deploy()
        sleep.assert_not_called()
        self.assertEqual(self.events, ['prebuild', 'stop', 'check_port', 'launch', 'healthy'])
        self.assertEqual(self.result()['new_pid'], self.child.pid)
        self.assertEqual(self.result()['state'], 'ok')
        with self.assertRaisesRegex(cutover.CutoverError, 'already used'):
            self.deploy()
        self.assertEqual(self.events.count('launch'), 1)

    def test_scheduled_release_waits_only_remaining_time_after_prebuild(self):
        self.args.notice_deadline = 415

        def wait(seconds):
            self.assertEqual(seconds, 5)
            self.events.append('wait')

        with patch.object(time, 'monotonic', return_value=410), patch.object(time, 'sleep', side_effect=wait):
            self.deploy()
        self.assertEqual(self.events, ['prebuild', 'wait', 'stop', 'check_port', 'launch', 'healthy'])

    def test_no_wait_after_schedule_has_elapsed(self):
        self.args.notice_deadline = 415
        with patch.object(time, 'monotonic', return_value=420), patch.object(time, 'sleep') as sleep:
            self.deploy()
        sleep.assert_not_called()
        self.assertEqual(self.result()['state'], 'ok')

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

    def test_resume_rejects_modified_or_missing_server_and_ui(self):
        changes = ((cutover.DLL, False), (f'{cutover.BUILD_DIR}/dependency.dll', False),
                   (f'{cutover.WEB_DIST}/assets/app.js', False),
                   (f'{cutover.BUILD_DIR}/Marten.runtimeconfig.json', True),
                   (f'{cutover.WEB_DIST}/index.html', True))
        for relative, missing in changes:
            with self.subTest(relative=relative, missing=missing):
                self.args.mode = 'release'
                self.h.generation = self.old
                self.timeout = True
                with self.assertRaises(cutover.DrainTimeout):
                    self.deploy()
                self.assertEqual(self.result()['artifacts'], cutover.launch_artifacts(self.root))
                self.args.mode = 'resume'
                self.h.generation = None
                self.events.clear()
                artifact = self.root / relative
                original = artifact.read_bytes()
                if missing:
                    artifact.unlink()
                else:
                    artifact.write_bytes(b'_' + original[1:])
                with self.assertRaisesRegex(cutover.CutoverError, 'artifacts (changed|missing)'):
                    self.deploy()
                self.assertEqual(self.events, ['check_port'])
                self.assertEqual(self.result()['state'], 'failed')
                self.assertEqual(self.log.read_text(), 'old log')
                artifact.write_bytes(original)
                (self.job / 'result.json').unlink()

    def test_resume_without_artifact_seal_refuses_legacy_timeout(self):
        self.timeout = True
        with self.assertRaises(cutover.DrainTimeout):
            self.deploy()
        prior = self.result()
        del prior['artifacts']
        cutover.atomic_json(self.job / 'result.json', prior)
        self.args.mode = 'resume'
        self.h.generation = None
        self.events.clear()
        with self.assertRaisesRegex(cutover.CutoverError, 'Missing artifact seal'):
            self.deploy()
        self.assertEqual(self.events, ['check_port'])

    def test_prebuild_rejects_symlink_artifact_before_term(self):
        (self.ui / 'assets/untracked.js').symlink_to(self.ui / 'index.html')
        with self.assertRaisesRegex(cutover.CutoverError, 'not a regular file'):
            self.deploy()
        self.assertEqual(self.events, ['prebuild'])


class PreRestartTests(unittest.TestCase):
    @contextmanager
    def endpoint(self, respond):
        calls = []
        lease = {}
        test = self

        class Handler(BaseHTTPRequestHandler):
            def handle_request(self):
                test.assertEqual(self.headers.get('Authorization'), 'Bearer scratch-token')
                if self.command == 'POST':
                    lease.update(json.loads(self.rfile.read(int(self.headers['Content-Length']))))
                    test.assertEqual(set(lease), {'id', 'pid', 'leaseSeconds'})
                    test.assertEqual(lease['pid'], OLD.pid)
                    test.assertEqual(lease['leaseSeconds'], 120)
                    test.assertRegex(lease['id'], r'^[a-f0-9]{32}$')
                elif lease:
                    test.assertEqual(self.headers.get('X-Marten-Drain-Id'), lease['id'])
                    test.assertEqual(self.headers.get('X-Marten-Drain-Pid'), str(OLD.pid))
                calls.append((self.command, self.path))
                status, fields = respond(calls)
                if status is None:  # Accepted POST or other request with lost response
                    return
                self.send_response(status)
                self.send_header('Content-Type', 'application/json')
                self.send_header('Cache-Control', 'no-store')
                self.end_headers()
                if status == 200:
                    if self.path == cutover.DRAIN_PATH and lease:
                        fields = dict(dict(id=lease['id'], pid=OLD.pid, remainingMs=100_000,
                                           state='ready', activeOperations=0, pendingMessages=0), **fields)
                    self.wfile.write(json.dumps(fields).encode())

            do_POST = do_DELETE = do_GET = handle_request

            def log_message(self, *_):
                pass

        with ThreadingHTTPServer(('127.0.0.1', 0), Handler) as server:
            worker = threading.Thread(target=server.serve_forever, kwargs={'poll_interval': 0.01})
            worker.start()
            try:
                yield server.server_port, calls
            finally:
                server.shutdown()
                worker.join(2)

    def stop(self, host, port, *, legacy=False):
        with patch.dict(cutover.stop.__globals__, wait_exit=lambda *_: True):
            drain = (cutover.LegacyDrain(port, 'scratch-token') if legacy else
                     cutover.LeaseDrain(host, ROOT, port, OLD, 'scratch-token'))
            cutover.stop(host, ROOT, port, OLD, 60, 60, False, drain)

    def test_drains_and_rechecks_ready_immediately_before_term(self):
        host = Fake()

        def respond(calls):
            self.assertEqual(host.sent, [])
            return ((200, dict(state='draining', activeOperations=1))
                    if len(calls) == 1 else (200, {}))

        with self.endpoint(respond) as (port, calls):
            self.stop(host, port)
        self.assertEqual(calls, [(method, cutover.DRAIN_PATH) for method in ('POST', 'GET', 'GET')])
        self.assertEqual(host.sent, [(OLD, signal.SIGTERM, 0)])

    def test_busy_bad_shape_stale_expiry_and_lost_begin_never_term(self):
        scenarios = (
            ((200, dict(state='busy', pendingMessages=1)),),
            ((200, dict(state='ready', pendingMessages=1)),),
            ((200, dict(state='draining', activeOperations=1, pendingMessages=1)),),
            ((200, dict(state='ready', activeOperations=True)),),
            ((200, dict(id='different')),),
            ((200, dict(pid=12345)),),
            ((200, {}), (200, dict(state='draining', activeOperations=1))),
            ((200, {}), (409, {})),
            ((200, {}), (200, dict(remainingMs=1000))),
            ((None, {}),),
        )
        for responses in scenarios:
            with self.subTest(responses=responses):
                host = Fake()

                def respond(calls):
                    self.assertEqual(host.sent, [])
                    return (204, {}) if calls[-1][0] == 'DELETE' else responses[len(calls) - 1]

                with self.endpoint(respond) as (port, calls):
                    with self.assertRaises(cutover.CutoverError) as error:
                        self.stop(host, port)
                self.assertNotIn('scratch-token', str(error.exception))
                self.assertEqual(host.sent, [])
                self.assertEqual([method for method, _ in calls],
                                 ['POST'] + ['GET'] * (len(responses) - 1) + ['DELETE'])

    def test_timed_out_begin_aborts_once_without_term_or_legacy(self):
        host = Fake()
        release_fixture = threading.Event()

        def respond(calls):
            if calls[-1][0] == 'POST':
                release_fixture.wait(2)
                return None, {}
            return 204, {}

        with patch.dict(cutover.LeaseDrain.observe.__globals__, DRAIN_HTTP_BUDGET=0.25):
            with self.endpoint(respond) as (port, calls):
                try:
                    with self.assertRaisesRegex(cutover.CutoverError, 'HTTP'):
                        self.stop(host, port)
                finally:
                    release_fixture.set()
        self.assertEqual(calls, [(method, cutover.DRAIN_PATH) for method in ('POST', 'DELETE')])
        self.assertEqual(host.sent, [])

    def test_rejected_begin_does_not_fall_back(self):
        for status in (302, 403, 404, 409):
            with self.subTest(status=status):
                host = Fake()
                with self.endpoint(lambda _: (status, {})) as (port, calls):
                    with self.assertRaisesRegex(cutover.CutoverError, f'HTTP {status}'):
                        self.stop(host, port)
                self.assertEqual(host.sent, [])
                self.assertEqual(calls, [('POST', cutover.DRAIN_PATH)] +
                                 ([('DELETE', cutover.DRAIN_PATH)] if status == 302 else []))

    def test_generation_changes_after_final_http_no_term_or_abort_to_replacement(self):
        host = Fake()

        def respond(calls):
            if calls[-1][0] == 'GET':
                host.generation = replace(OLD, birth=replace(OLD.birth, version=20))
            return 200, {}

        with self.endpoint(respond) as (port, calls):
            with self.assertRaisesRegex(cutover.CutoverError, 'generation'):
                self.stop(host, port)
        self.assertEqual([method for method, _ in calls], ['POST', 'GET'])
        self.assertEqual(host.sent, [])

    def test_ttl_is_anchored_before_response_and_rechecked_at_signal(self):
        host = Fake()

        def respond(calls):
            if calls[-1][0] == 'GET':
                host.now += 0.5
                return 200, dict(remainingMs=2400)  # unsafe after transit, despite 2.4s in body
            return (204, {}) if calls[-1][0] == 'DELETE' else (200, {})

        with patch.object(time, 'monotonic', side_effect=lambda: host.now):
            with self.endpoint(respond) as (port, calls):
                with self.assertRaisesRegex(cutover.CutoverError, 'expiry'):
                    self.stop(host, port)
        self.assertEqual([method for method, _ in calls], ['POST', 'GET', 'DELETE'])
        self.assertEqual(host.sent, [])

    def test_final_identity_probe_consumes_ttl_margin(self):
        host = Fake()
        final_response_sent = threading.Event()
        listeners = host.listeners

        def slow_identity_probe(port):
            if final_response_sent.is_set():
                host.now += 0.5
            return listeners(port)

        host.listeners = slow_identity_probe

        def respond(calls):
            if calls[-1][0] == 'GET':
                final_response_sent.set()
                return 200, dict(remainingMs=2400)
            return (204, {}) if calls[-1][0] == 'DELETE' else (200, {})

        with patch.object(time, 'monotonic', side_effect=lambda: host.now):
            with self.endpoint(respond) as (port, calls):
                with self.assertRaisesRegex(cutover.CutoverError, 'Final readiness'):
                    self.stop(host, port)
        self.assertEqual([method for method, _ in calls], ['POST', 'GET', 'DELETE'])
        self.assertEqual(host.sent, [])

    def test_wait_budget_does_not_renew_even_if_server_reports_constant_remaining(self):
        host = Fake()

        def respond(calls):
            return ((204, {}) if calls[-1][0] == 'DELETE' else
                    (200, dict(state='draining', activeOperations=1, remainingMs=2500)))

        with (patch.object(time, 'monotonic', side_effect=lambda: host.now),
              patch.object(time, 'sleep', side_effect=host.sleep)):
            with self.endpoint(respond) as (port, calls):
                with self.assertRaisesRegex(cutover.CutoverError, 'Timed out'):
                    self.stop(host, port)
        self.assertEqual([method for method, _ in calls], ['POST', 'GET', 'DELETE'])
        self.assertEqual(host.sent, [])
        self.assertEqual(host.now, 0.5)

    def test_legacy_only_absent_and_three_quiet_snapshots(self):
        quiet = [dict(id='closed', status='idle', pending=0, closed=True, pausedEffective=False, modelCall=None),
                 dict(id='paused', status='idle', pending=0, closed=False, pausedEffective=True)]
        for absent, final_change in ((404, {}), (200, {}), (403, {}),
                                     (404, dict(pending=1)), (404, dict(status='paused')),
                                     (404, dict(modelCall={'active': True})),
                                     (404, dict(id='replacement')), (404, dict(id='closed')),
                                     (404, dict(pending=False))):
            with self.subTest(absent=absent, final_change=final_change):
                host = Fake()

                def respond(calls):
                    self.assertEqual(host.sent, [])
                    if calls[-1][1] == cutover.DRAIN_PATH:
                        return absent, {}
                    agents = [quiet[0], dict(quiet[1], **final_change)] if len(calls) == 4 else quiet
                    return 200, dict(agents=agents)

                with patch.object(time, 'sleep') as sleep:
                    with self.endpoint(respond) as (port, calls):
                        if absent == 404 and not final_change:
                            self.stop(host, port, legacy=True)
                            self.assertEqual(host.sent, [(OLD, signal.SIGTERM, 0)])
                        else:
                            with self.assertRaises(cutover.CutoverError):
                                self.stop(host, port, legacy=True)
                            self.assertEqual(host.sent, [])
                    if absent == 404:
                        sleep.assert_called_once_with(1)
                    else:
                        sleep.assert_not_called()
                self.assertEqual(calls, [('GET', cutover.DRAIN_PATH)] +
                                 ([('GET', '/api/status')] * 3 if absent == 404 else []))


    def test_legacy_retained_terminal_attempts_pass_but_inflight_or_malformed_refuse(self):
        # Exact fda0f931 ModelCallStatus wire fields, System.Text.Json DateTimeOffset
        # and snake_case lowercase enums. Deliberately reverse the wall-clock times.
        completed = dict(id=37, model='gpt-5.2', kind='reply',
                         startedAt='2026-11-27T19:14:40.1234567+00:00',
                         endedAt='2026-11-27T19:14:38.1234+00:00', outcome='succeeded')
        invalid = (dict(endedAt=None), dict(outcome=None), dict(outcome='Succeeded'),
                   dict(startedAt='2026-11-27T19:14:40'), dict(endedAt='2026-02-30T19:14:40Z'),
                   dict(endedAt='2026-11-27T19:14:40+00:60'), dict(startedAt=45),
                   dict(id=True), dict(id=0), dict(kind='unknown'), dict(model=' '))
        cases = [(completed, True), (dict(completed, kind='compaction', outcome='failed'), True),
                 (dict(completed, outcome='cancelled'), True),
                 ({k: v for k, v in completed.items() if k not in ('endedAt', 'outcome')}, False)]
        cases.extend((dict(completed, **change), False) for change in invalid)
        for model_call, allowed in cases:
            with self.subTest(model_call=model_call):
                host = Fake()

                def respond(calls):
                    self.assertEqual(host.sent, [])
                    if calls[-1][1] == cutover.DRAIN_PATH:
                        return 404, {}
                    return 200, dict(agents=[dict(id='scratch-closed', status='idle', pending=0,
                        closed=True, pausedEffective=True,
                        modelCall=model_call if len(calls) == 4 else completed)])

                with patch.object(time, 'sleep'), self.endpoint(respond) as (port, calls):
                    if allowed:
                        self.stop(host, port, legacy=True)
                    else:
                        with self.assertRaisesRegex(cutover.CutoverError, 'not quiet'):
                            self.stop(host, port, legacy=True)
                self.assertEqual(bool(host.sent), allowed)
                self.assertEqual(calls, [('GET', cutover.DRAIN_PATH)] + [('GET', '/api/status')] * 3)


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


    def test_shared_listener_is_not_probed_or_accepted(self):
        child = SimpleNamespace(pid=4321, poll=lambda: None)
        shared = {child.pid, 4322}
        sole = {child.pid}
        # Shared initially, then a subprocess appears during the first probe.
        observations = iter((shared, sole, shared, shared, sole, sole))
        latest = None

        def listeners(port):
            nonlocal latest
            latest = next(observations)
            return latest

        def probe(*args):
            self.assertEqual(latest, sole)
            return True

        with patch.dict(cutover.healthy.__globals__, http_check=probe), patch.object(time, 'sleep') as sleep:
            host = SimpleNamespace(listeners=listeners, direct_children=lambda pid, pids: pid == 4321 and pids == {4322})
            cutover.healthy(host, child, 51000, 'scratch-token')
        self.assertEqual(sleep.call_count, 3)
        self.assertEqual(list(observations), [])

    def test_persistent_shared_listener_times_out_without_http_probe(self):
        child = SimpleNamespace(pid=4321, poll=lambda: None)
        host = SimpleNamespace(listeners=lambda port: {child.pid, 4322}, direct_children=lambda *_: True)
        with (
            patch.dict(cutover.healthy.__globals__, http_check=lambda *args: self.fail('shared listener')),
            patch.object(time, 'monotonic', side_effect=(0, 0, 0, 2, 2)),
            patch.object(time, 'sleep') as sleep,
        ):
            with self.assertRaisesRegex(cutover.CutoverError, r'additional listener PIDs \[4322\]'):
                cutover.healthy(host, child, 51000, 'scratch-token')
        sleep.assert_called_once_with(0.25)


    def test_shared_listener_window_is_not_renewed(self):
        child = SimpleNamespace(pid=4321, poll=lambda: None)
        observations = iter(({4321, 4322}, set(), {4321, 4323}))
        host = SimpleNamespace(listeners=lambda port: next(observations), direct_children=lambda *_: True)
        with (
            patch.dict(cutover.healthy.__globals__, http_check=lambda *args: self.fail('not sole listener')),
            patch.object(time, 'monotonic', side_effect=(0, 0, 0, 1, 2, 2)),
            patch.object(time, 'sleep') as sleep,
        ):
            with self.assertRaisesRegex(cutover.CutoverError, r'additional listener PIDs \[4323\]'):
                cutover.healthy(host, child, 51000, 'scratch-token')
        self.assertEqual(sleep.call_count, 2)

    def test_unrelated_co_listener_is_rejected_immediately(self):
        child = SimpleNamespace(pid=4321, poll=lambda: None)
        host = SimpleNamespace(listeners=lambda port: {4321, 4322}, direct_children=lambda *_: False)
        with (
            patch.dict(cutover.healthy.__globals__, http_check=lambda *args: self.fail('unrelated listener')),
            patch.object(time, 'sleep') as sleep,
        ):
            with self.assertRaisesRegex(cutover.CutoverError, r'unexpected listener.*\[4321, 4322\]'):
                cutover.healthy(host, child, 51000, 'scratch-token')
        sleep.assert_not_called()


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
    def test_real_handoff_survives_helper_exit(self):
        # The same nohup/dotnet argv and detached Popen path, but a scratch
        # dotnet shim and a Python listener with an OS-assigned loopback port.
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp).resolve()
            fixture = root / cutover.DLL
            fixture.parent.mkdir(parents=True)
            fixture.write_text(dedent('''\
                import json, os, signal, subprocess, sys, time
                from pathlib import Path
                from http.server import BaseHTTPRequestHandler, HTTPServer

                class Handler(BaseHTTPRequestHandler):
                    drain_id = None

                    def drain_ready(self, accepted):
                        self.send_response(200 if accepted else 409)
                        self.send_header('Content-Type', 'application/json')
                        self.send_header('Cache-Control', 'no-store')
                        self.end_headers()
                        if accepted:
                            self.wfile.write(json.dumps(dict(id=self.drain_id, pid=os.getpid(), state='ready',
                                activeOperations=0, pendingMessages=0, remainingMs=100_000)).encode())

                    def do_POST(self):
                        begin = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
                        ok = (self.path == '/api/release/drain' and begin['pid'] == os.getpid()
                              and self.headers.get('Authorization') == 'Bearer scratch-token')
                        if ok:
                            type(self).drain_id = begin['id']
                        self.drain_ready(ok)

                    def do_GET(self):
                        ok = self.path == '/' or self.headers.get('Authorization') == 'Bearer scratch-token'
                        if self.path == '/api/release/drain':
                            self.drain_ready(ok and self.drain_id is not None
                                and self.headers.get('X-Marten-Drain-Id') == self.drain_id
                                and self.headers.get('X-Marten-Drain-Pid') == str(os.getpid()))
                            return
                        self.send_response(200 if ok else 401)
                        self.end_headers()
                        if ok:
                            self.wfile.write(b'{}')

                port = int(os.environ.get('SCRATCH_PORT', '0'))
                with HTTPServer(('127.0.0.1', port), Handler) as server:
                    server.timeout = 0.1
                    if port == 0:
                        def delay_term(sig, frame):
                            time.sleep(1.35)
                            raise SystemExit(0)
                        signal.signal(signal.SIGTERM, delay_term)
                        print(server.server_port, flush=True)
                    else:
                        # Keep an actual duplicate of the listening fd open
                        # until the deploy helper's lsof has seen both holders.
                        fd = server.fileno()
                        holder = subprocess.Popen([sys.executable, 'hold.py'], pass_fds=(fd,))
                    print('scratch server ready', flush=True)
                    deadline = time.monotonic() + 12
                    try:
                        while time.monotonic() < deadline and not Path('fixture.stop').exists():
                            server.handle_request()
                    finally:
                        if port and holder.poll() is None:
                            holder.terminate()
                            holder.wait(timeout=2)
                '''))
            (root / 'hold.py').write_text(dedent('''\
                import time
                from pathlib import Path

                end = time.monotonic() + 5
                while time.monotonic() < end and not Path('release-holder').exists():
                    time.sleep(0.05)
                '''))
            shim = root / 'dotnet'
            shim.write_text('#!/bin/bash\nexec -a dotnet "$SCRATCH_PYTHON" "$@"\n')
            shim.chmod(0o700)
            env = dict(os.environ, PATH=f'{root}:/usr/bin:/bin', SCRATCH_PYTHON=sys.executable)
            env.pop('SCRATCH_PORT', None)
            home = root / 'scratch-home'
            mounted = root / 'scratch-mounted'
            job = root / 'job'
            job.mkdir(mode=0o700)
            home.mkdir()
            (home / '.marten').symlink_to(mounted)
            (mounted / 'secrets').mkdir(parents=True)
            (mounted / 'secrets/ui-token').write_text('scratch-token')
            env.update(HOME=str(home), MARTEN_HOME=str(mounted), MARTEN_WEB_ROOT='')
            for name in ('Marten.runtimeconfig.json', 'Marten.deps.json'):
                (fixture.parent / name).write_text('{}')
            (root / cutover.WEB_DIST).mkdir(parents=True)
            (root / cutover.WEB_DIST / 'index.html').write_text('<html>scratch</html>')
            log = home / '.marten-server.log'
            log.touch(mode=0o640)
            initial_log_mode = log.stat().st_mode
            helper = Path(cutover.launch.__code__.co_filename).resolve()
            runner = dedent('''\
                import os, runpy, sys
                from dataclasses import asdict
                from pathlib import Path
                from types import SimpleNamespace

                helper, old_pid, port = sys.argv[1:]
                m = runpy.run_path(helper)
                class ScratchDarwin(m['Darwin']):
                    def direct_children(self, parent_pid, pids):
                        related = super().direct_children(parent_pid, pids)
                        if related:
                            Path('release-holder').touch()
                        return related

                host = ScratchDarwin()
                root = Path.cwd()
                port = int(port)
                pid = int(old_pid)
                assert host.listeners(port) == {pid}
                old = host.describe(pid)
                assert old and old.cwd == str(root) and old.birth.uid == os.getuid(), old
                assert old.command.endswith(' ' + m['DLL']), old.command
                job = root / 'job'
                m['atomic_json'](job / 'generation.json', asdict(old))
                # Only substitute the scratch fixture's build and command
                # form, and keep all native identity/port/handoff checks.
                m['deploy'].__globals__.update(
                    PORT=port, LOCK_DIR=root, launch_forms=lambda _: {(old.command, str(root))},
                    prebuild=lambda *_: None, check_tree=lambda *_: None,
                )
                os.environ['SCRATCH_PORT'] = str(port)
                args = SimpleNamespace(mode='release', job=job, expected_home=root / 'scratch-mounted',
                                       sha='a' * 40, notice_deadline=None, grace=1, late_wait=3,
                                       kill_after_grace=False, legacy_first_install=None)
                m['deploy'](args, host, root)
                ''')
            with subprocess.Popen([str(shim), cutover.DLL], cwd=root, env=env, text=True,
                                  stdout=subprocess.PIPE, stderr=subprocess.DEVNULL) as old:
                try:
                    self.assertTrue(select.select([old.stdout], [], [], 3)[0], 'scratch old not ready')
                    port = int(old.stdout.readline())
                    finished = subprocess.run([sys.executable, '-B', '-c', runner, str(helper), str(old.pid), str(port)],
                                              cwd=root, env=env, text=True, capture_output=True, timeout=10)
                    self.assertEqual(finished.returncode, 0, finished.stderr)
                    self.assertEqual(old.wait(2), 0)
                    self.assertIn('exceeded TERM grace', finished.stdout)
                    self.assertTrue((root / 'release-holder').exists(), 'lsof did not observe the inherited socket')
                    result_text = (job / 'result.json').read_text()
                    self.assertNotIn('scratch-token', result_text)
                    result = json.loads(result_text)
                    self.assertEqual(result['state'], 'ok')
                    self.assertEqual(result['artifacts'], cutover.launch_artifacts(root))
                    new_pid = result['new_pid']
                    host = cutover.Darwin()
                    self.assertNotEqual(new_pid, old.pid)
                    self.assertEqual(host.listeners(port), {new_pid})
                    self.assertEqual(os.getsid(new_pid), new_pid)
                    self.assertTrue(cutover.http_check(port, 'scratch-token', 1))
                    self.assertEqual(log.stat().st_mode, initial_log_mode)
                    self.assertIn('scratch server ready', log.read_text())
                finally:
                    (root / 'fixture.stop').touch()
                    if old.poll() is None:
                        old.terminate()  # only our unreaped child
                    if 'port' in locals():
                        deadline = time.monotonic() + 2
                        while time.monotonic() < deadline and cutover.Darwin().listeners(port):
                            time.sleep(0.05)

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
