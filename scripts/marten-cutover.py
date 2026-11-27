#!/usr/bin/env python3
"""Detached single-host Marten release helper; see docs/release-cutover.md."""

import argparse
import ctypes as C
from dataclasses import asdict, dataclass
import errno
import fcntl
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import secrets
import signal
import socket
import stat
import subprocess
import sys
import tempfile
import threading
import time

PORT = 7450
SHARED_LISTENER_GRACE = 2
DRAIN_PATH = '/api/release/drain'
DRAIN_LEASE_SECONDS = 120
DRAIN_TERM_MARGIN = 2
DRAIN_HTTP_BUDGET = 2
LEGACY_FIRST_INSTALL = 'fda0f931680b4ecec0942949a888282d82ebc634'
LOCK_DIR = Path('/private/tmp')
DLL = 'src/Marten/bin/Debug/net10.0/Marten.dll'
BUILD_DIR = str(Path(DLL).parent)
WEB_DIST = 'web/dist'
PATH_PREFIX = '/opt/homebrew/bin:/usr/local/share/dotnet:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin:'
UNSET = ('MSBuildSDKsPath', 'MSBuildExtensionsPath', 'DOTNET_HOST_PATH',
         'MSBUILDFAILONDRIVEENUMERATINGWILDCARD')


class CutoverError(Exception):
    pass


class DrainTimeout(CutoverError):
    pass


@dataclass(frozen=True)
class Birth:
    uid: int
    ruid: int
    seconds: int
    micros: int
    unique: int
    version: int


@dataclass(frozen=True)
class Generation:
    pid: int
    birth: Birth
    command: str
    cwd: str


# Darwin's proc_bsdinfowithuniqid (proc_info.h / proc_info_private.h, flavor 18).
# Read start time, credentials and pidversion in one call. A plain kill(pid)
# after a ps check has a PID-reuse race. Do not fall back to it on older hosts.
class ProcInfo(C.Structure):
    _fields_ = [('fields', C.c_uint32 * 12), ('comm', C.c_char * 16),
                ('name', C.c_char * 32), ('tail', C.c_uint32 * 6),
                ('seconds', C.c_uint64), ('micros', C.c_uint64),
                ('uuid', C.c_uint8 * 16), ('unique', C.c_uint64),
                ('parent_unique', C.c_uint64), ('version', C.c_uint32),
                ('parent_version', C.c_uint32), ('reserved', C.c_uint64 * 2)]


class Darwin:
    def __init__(self):
        if sys.platform != 'darwin' or os.getuid() == 0 or os.getuid() != os.geteuid():
            raise CutoverError('Run as the Marten login user (not root) on macOS')
        self.lib = C.CDLL('/usr/lib/libSystem.B.dylib', use_errno=True)
        self.lib.proc_pidinfo.argtypes = [C.c_int, C.c_int, C.c_uint64, C.c_void_p, C.c_int]
        self.lib.proc_signal_with_audittoken.argtypes = [C.POINTER(C.c_uint32), C.c_int]

    def _proc_info(self, pid):
        info = ProcInfo()
        C.set_errno(0)
        n = self.lib.proc_pidinfo(pid, 18, 1, C.byref(info), C.sizeof(info))
        if n <= 0 and C.get_errno() == errno.ESRCH:
            return None
        if n != C.sizeof(info) or info.fields[3] != pid or not info.seconds or not info.unique:
            raise CutoverError(f'Cannot establish generation for PID {pid}')
        if info.fields[1] == 5:  # SZOMB: exited, not yet reaped
            return None
        return info

    def birth(self, pid):
        info = self._proc_info(pid)
        if info is None:
            return None
        return Birth(info.fields[5], info.fields[7], info.seconds, info.micros,
                     info.unique, info.version)

    def direct_children(self, parent_pid, pids):
        parent = self._proc_info(parent_pid)
        if parent is None or (parent.fields[5], parent.fields[7]) != (os.getuid(), os.getuid()):
            return False
        for pid in pids:
            info = self._proc_info(pid)
            if (info is None or info.fields[4] != parent_pid or info.parent_unique != parent.unique
                    or (info.fields[5], info.fields[7]) != (parent.fields[5], parent.fields[7])):
                return False
        return True

    def signal(self, old, sig):
        # XNU checks PID + pidversion in the kernel before delivering the signal.
        # Only these two slots are consumed by proc_find_audit_token for a target.
        token = (C.c_uint32 * 8)()
        token[5], token[7] = old.pid, old.birth.version
        C.set_errno(0)
        if self.lib.proc_signal_with_audittoken(token, sig) != 0:
            err = C.get_errno()
            if err == errno.ESRCH:
                return False
            raise CutoverError(f'{sig.name} refused for verified PID {old.pid}: errno {err}')
        return True

    @staticmethod
    def listeners(port):
        out = probe('/usr/sbin/lsof', '-nP', '-t', f'-iTCP:{port}', '-sTCP:LISTEN')
        if any(not re.fullmatch(r'[1-9][0-9]*', pid) for pid in out.splitlines()):
            raise CutoverError('Unexpected listener probe output')
        return {int(pid) for pid in out.splitlines()}

    def describe(self, pid):
        before = self.birth(pid)
        if before is None:
            return None
        cmd = probe('/bin/ps', '-ww', '-p', str(pid), '-o', 'command=')
        cwd = probe('/usr/sbin/lsof', '-nP', '-a', '-p', str(pid), '-d', 'cwd', '-Fn')
        after = self.birth(pid)
        if after is None:
            return None
        paths = [line[1:] for line in cwd.splitlines() if line.startswith('n')]
        if before != after or len(paths) != 1 or len(cmd.splitlines()) != 1:
            raise CutoverError(f'Ambiguous command, cwd, or generation for PID {pid}')
        return Generation(pid, before, cmd, paths[0])


def probe(*argv):
    p = subprocess.run(argv, capture_output=True, text=True, timeout=5)
    # Both ps and lsof can return 1 with no match; do not accept diagnostics as absence.
    if p.stderr or p.returncode not in (0, 1) or (p.returncode == 1 and p.stdout):
        raise CutoverError(f'{Path(argv[0]).name} probe failed; listener/identity is ambiguous')
    return p.stdout.rstrip('\n')


def launch_forms(root):
    return {(str(root / DLL.removesuffix('.dll')), str(root / 'src/Marten')),
            (f'dotnet {DLL}', str(root))}


def inspect(host, root, port):
    listeners = host.listeners(port)
    if len(listeners) != 1:
        raise CutoverError(f'Expected one listener on {port}; found {sorted(listeners)}')
    old = host.describe(next(iter(listeners)))
    if (not old or old.birth.uid != os.getuid() or old.birth.ruid != os.getuid()
            or (old.command, old.cwd) not in launch_forms(root) or host.listeners(port) != {old.pid}):
        raise CutoverError('Listener is not one unambiguous Marten generation from this checkout')
    return old


def exited(host, old):
    current = host.birth(old.pid)
    if current is None:
        return True
    if current != old.birth:
        raise CutoverError(f'Identity changed for PID {old.pid}; refusing further action')
    return False


def wait_exit(host, old, seconds, clock=time.monotonic, sleep=time.sleep):
    deadline = clock() + seconds
    while True:
        if exited(host, old):
            return True
        remaining = deadline - clock()
        if remaining <= 0:
            return False
        sleep(min(0.25, remaining))


def stop(host, root, port, old, grace, late, kill_after_grace, drain):
    # `old` was already verified before prebuild. If it has since exited, the
    # caller may proceed through the normal port checks without sending TERM.
    if exited(host, old):
        return
    try:
        current = inspect(host, root, port)
    except CutoverError:
        if exited(host, old):
            return
        raise
    if current != old:
        raise CutoverError('Listener did not match the reviewed old generation; no signal sent')
    term_sent = False
    try:
        drain.wait_ready()
        if inspect(host, root, port) != old:
            raise CutoverError('Listener changed during pre-restart drain; no signal sent')
        # Check the live lease after the potentially slow process probes, then
        # check birth/TTL once more immediately before the kernel-bound signal.
        confirm_until = drain.confirm_ready()
        if (host.listeners(port) != {old.pid} or host.birth(old.pid) != old.birth
                or time.monotonic() >= confirm_until):
            raise CutoverError('Final readiness or generation could not be verified; no signal sent')
        term_sent = host.signal(old, signal.SIGTERM)
        if not term_sent:
            if exited(host, old):
                return
            raise CutoverError('TERM target could not be verified at delivery')
    finally:
        if not term_sent:
            drain.abort()
    if wait_exit(host, old, grace):
        return
    print(f'PID {old.pid} exceeded TERM grace; watching up to {late}s more', flush=True)
    if wait_exit(host, old, late):
        return
    if not kill_after_grace:
        raise DrainTimeout(f'PID {old.pid} still running after TERM; no KILL authorized')
    listeners = host.listeners(port)
    if listeners - {old.pid}:
        raise CutoverError(f'Port {port} occupied by another process; no KILL sent: {sorted(listeners)}')
    current = host.describe(old.pid)
    if current is None and exited(host, old):
        return
    if current != old:
        raise CutoverError('Old command, cwd, owner, or generation changed; no KILL sent')
    print(f'Escalating to SIGKILL for verified PID {old.pid} after grace and late-exit window', flush=True)
    if not host.signal(old, signal.SIGKILL) and not exited(host, old):
        raise CutoverError('KILL target could not be verified at delivery')
    if not wait_exit(host, old, 10):
        raise DrainTimeout(f'PID {old.pid} did not exit after KILL')


def check_port(host, port):
    listeners = host.listeners(port)
    if listeners:
        raise CutoverError(f'Port {port} occupied by listener(s) {sorted(listeners)}; not starting')
    # lsof can omit sockets the login user cannot see. On Darwin, REUSEADDR
    # permits a wildcard socket alongside a specific address (and vice versa).
    # Probe both forms, allowing connections left in TIME_WAIT by the old app.
    for family, addr in ((socket.AF_INET, '0.0.0.0'), (socket.AF_INET, '127.0.0.1'),
                         (socket.AF_INET6, '::'), (socket.AF_INET6, '::1')):
        with socket.socket(family, socket.SOCK_STREAM) as s:
            if family == socket.AF_INET6:
                s.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 1)
            s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            try:
                s.bind((addr, port))
                s.listen(1)
            except OSError as e:
                raise CutoverError(f'Cannot claim port {port}; not starting ({e.strerror})') from e


def drain_http(port, token, method, path, *, deadline, headers=None, body=None, limit=0):
    """Return only bounded, redaction-safe data; never follow a redirect."""
    def remaining():
        left = deadline - time.monotonic()
        if left <= 0:
            raise CutoverError('Pre-restart HTTP deadline elapsed')
        return left

    headers = {'Authorization': f'Bearer {token}', **(headers or {})}
    if body is not None:
        headers['Content-Type'] = 'application/json'
        body = json.dumps(body).encode('ascii')
    conn = http.client.HTTPConnection('127.0.0.1', port, timeout=remaining())
    timer = None
    try:
        conn.connect()
        # A socket timeout alone allows a trickle response to extend each read.
        sock = conn.sock

        def cancel_read():
            try:
                sock.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass

        timer = threading.Timer(remaining(), cancel_read)
        timer.daemon = True
        timer.start()
        conn.request(method, path, headers=headers, body=body)
        with conn.getresponse() as r:
            data = r.read(limit + 1) if r.status == 200 and limit else None
            remaining()
            if data is not None and len(data) > limit:
                raise CutoverError('Pre-restart HTTP response too large')
            return r.status, r.headers, data
    except (OSError, ValueError, http.client.HTTPException):
        # HTTP exceptions can include peer-controlled headers or body bytes.
        raise CutoverError('Pre-restart localhost HTTP request failed') from None
    finally:
        if timer:
            timer.cancel()
        conn.close()


def json_object(headers, data):
    if headers.get_content_type() == 'application/json':
        try:
            obj = json.loads(data, object_pairs_hook=unique_fields)
            if isinstance(obj, dict):
                return obj
        except (ValueError, RecursionError):
            pass
    raise CutoverError('Unexpected pre-restart JSON response')


def unique_fields(pairs):
    obj = dict(pairs)
    if len(obj) != len(pairs):
        raise ValueError('Duplicate JSON fields')
    return obj


class LeaseDrain:
    def __init__(self, host, root, port, old, token):
        self.host, self.root, self.port, self.old, self.token = host, root, port, old, token
        self.id = secrets.token_hex(16)
        self.headers = {'X-Marten-Drain-Id': self.id, 'X-Marten-Drain-Pid': str(old.pid)}
        self.attempted = False
        self.until = None

    def observe(self, method, body=None):
        start = time.monotonic()
        status, headers, data = drain_http(self.port, self.token, method, DRAIN_PATH,
                                           deadline=min(start + DRAIN_HTTP_BUDGET, self.until),
                                           headers=self.headers if method == 'GET' else None,
                                           body=body, limit=4096)
        if status != 200:
            if method == 'POST' and status in (400, 403, 404, 409):
                self.attempted = False  # explicit rejection, not an ambiguous begin
            raise CutoverError(f'Pre-restart drain {method} refused (HTTP {status})')
        obj = json_object(headers, data)
        if ('no-store' not in (v.strip().lower() for v in headers.get('Cache-Control', '').split(','))
                or obj.get('id') != self.id or type(obj.get('pid')) is not int or obj['pid'] != self.old.pid
                or obj.get('state') not in ('draining', 'ready', 'busy')
                or any(type(obj.get(k)) is not int or obj[k] < 0
                       for k in ('remainingMs', 'activeOperations', 'pendingMessages'))
                or obj['remainingMs'] > DRAIN_LEASE_SECONDS * 1000):
            raise CutoverError('Unexpected pre-restart drain response')
        expected = ('busy' if obj['pendingMessages'] else
                    'draining' if obj['activeOperations'] else 'ready')
        if obj['state'] != expected:
            raise CutoverError('Inconsistent pre-restart drain state and counters')
        # Anchor at request start, not response receipt, and never extend it.
        self.until = min(self.until, start + obj['remainingMs'] / 1000)
        if obj['state'] == 'busy':
            raise CutoverError('Pre-restart drain is busy; no TERM sent')
        if self.until - time.monotonic() <= DRAIN_TERM_MARGIN:
            raise CutoverError('Pre-restart drain lease expired or too close to expiry')
        return obj['state']

    def wait_ready(self):
        self.until = time.monotonic() + DRAIN_LEASE_SECONDS
        body = {'id': self.id, 'pid': self.old.pid, 'leaseSeconds': DRAIN_LEASE_SECONDS}
        self.attempted = True  # POST may take effect even when its response is lost
        state = self.observe('POST', body)
        while state != 'ready':
            time.sleep(min(0.25, max(0, self.until - time.monotonic() - DRAIN_TERM_MARGIN)))
            if self.until - time.monotonic() <= DRAIN_TERM_MARGIN:
                raise CutoverError('Timed out waiting for pre-restart drain; no TERM sent')
            state = self.observe('GET')

    def confirm_ready(self):
        if self.observe('GET') != 'ready':
            raise CutoverError('Pre-restart drain no longer ready; no TERM sent')
        return self.until - DRAIN_TERM_MARGIN

    def abort(self):
        if not self.attempted:
            return
        self.attempted = False
        try:
            if inspect(self.host, self.root, self.port) == self.old:
                status, _, _ = drain_http(self.port, self.token, 'DELETE', DRAIN_PATH, headers=self.headers,
                                          deadline=time.monotonic() + DRAIN_HTTP_BUDGET)
                if status == 204:
                    return
        except (CutoverError, OSError, subprocess.SubprocessError):
            pass
        print('Could not confirm drain release; any lease expires automatically', flush=True)


class LegacyDrain:
    """Explicit first install only. Old status is an observation, not a fence."""
    def __init__(self, port, token):
        self.port, self.token = port, token
        self.ids = None

    def probe(self, path, limit=0):
        return drain_http(self.port, self.token, 'GET', path, limit=limit,
                          deadline=time.monotonic() + DRAIN_HTTP_BUDGET)

    def wait_ready(self):
        status, _, _ = self.probe(DRAIN_PATH)
        if status != 404:
            raise CutoverError('Legacy first install requires HTTP 404 from the drain endpoint')
        self.observe()
        time.sleep(1)
        self.observe()

    def observe(self):
        start = time.monotonic()
        status, headers, data = self.probe('/api/status', limit=8 * 1024 * 1024)
        if status != 200:
            raise CutoverError(f'Legacy status request refused (HTTP {status})')
        agents = json_object(headers, data).get('agents')
        if not isinstance(agents, list) or not agents:
            raise CutoverError('Legacy status lacks a nonempty agent inventory')
        ids = set()
        for agent in agents:
            if (not isinstance(agent, dict) or not isinstance(agent.get('id'), str)
                    or not agent['id'] or agent['id'] in ids or type(agent.get('closed')) is not bool
                    or type(agent.get('pausedEffective')) is not bool
                    or type(agent.get('pending')) is not int or agent['pending'] < 0):
                raise CutoverError('Unexpected legacy agent status response')
            if agent.get('status') != 'idle' or agent['pending'] or agent.get('modelCall') is not None:
                raise CutoverError('Legacy agent status is not quiet; no TERM sent')
            ids.add(agent['id'])
        if self.ids is not None and ids != self.ids:
            raise CutoverError('Legacy agent inventory changed; no TERM sent')
        self.ids = ids
        return start + DRAIN_HTTP_BUDGET

    def confirm_ready(self):
        return self.observe()

    def abort(self):
        pass  # No lease ever acquired in legacy mode.


def http_check(port, token, timeout):
    # No redirects or proxy environment: the token only goes to local Marten.
    for path, headers in (('/', {}), ('/api/status', {'Authorization': f'Bearer {token}'})):
        conn = http.client.HTTPConnection('127.0.0.1', port, timeout=timeout)
        try:
            conn.request('GET', path, headers=headers)
            with conn.getresponse() as r:
                if r.status != 200 or (path != '/' and not isinstance(json.load(r), dict)):
                    return False
        finally:
            conn.close()
    return True


def healthy(host, child, port, token):
    deadline = time.monotonic() + 30
    expected = {child.pid}
    shared_until = None

    def observe():
        nonlocal shared_until
        listeners = host.listeners(port)
        extras = listeners - expected
        if extras:
            if child.pid not in listeners or not host.direct_children(child.pid, extras):
                raise CutoverError(f'Port {port} occupied by unexpected listener(s) {sorted(listeners)}')
            now = time.monotonic()
            if shared_until is None:
                shared_until = min(deadline, now + SHARED_LISTENER_GRACE)
            if now >= shared_until:
                raise CutoverError(f'Port {port} still has additional listener PIDs {sorted(extras)}; '
                                   f'new Marten PID {child.pid} was not the sole listener within startup budget')
        return listeners

    listeners = set()
    while time.monotonic() < deadline:
        if child.poll() is not None:
            raise CutoverError(f'New Marten PID {child.pid} exited during startup ({child.returncode})')
        # lsof may list both Marten and a subprocess that briefly inherited its
        # socket. Wait for exclusive ownership; do not probe HTTP while shared.
        listeners = observe()
        if listeners == expected:
            try:
                ready = http_check(port, token, 1)
            except (OSError, ValueError, http.client.HTTPException):
                ready = False
            if ready:
                listeners = observe()
                if listeners == expected and child.poll() is None:
                    return
        time.sleep(0.25)
    if listeners - expected:
        raise CutoverError(f'Port {port} still has additional listener PIDs {sorted(listeners - expected)}; '
                           f'new Marten PID {child.pid} was not the sole listener within startup budget')
    raise CutoverError(f'New Marten PID {child.pid} unhealthy: UI/authenticated status did not become ready')


def command(root, *argv, capture=False, env=None):
    print('+', *argv, flush=True)
    return subprocess.run(argv, cwd=root, check=True, text=True, env=env,
                          stdout=subprocess.PIPE if capture else None).stdout


def check_tree(root, sha=None):
    if command(root, 'git', 'symbolic-ref', '--short', 'HEAD', capture=True).strip() != 'main':
        raise CutoverError('Production checkout is not on main')
    command(root, 'git', 'diff', '--quiet', '--')
    command(root, 'git', 'diff', '--cached', '--quiet', '--')
    if sha and command(root, 'git', 'rev-parse', 'HEAD', capture=True).strip() != sha:
        raise CutoverError('Checkout no longer matches the tested SHA')


def prebuild(root, sha, env):
    check_tree(root)
    command(root, 'git', 'fetch', 'origin', 'main')
    if command(root, 'git', 'rev-parse', 'FETCH_HEAD', capture=True).strip() != sha:
        raise CutoverError('Fetched origin/main did not match the tested SHA; not updating or stopping')
    command(root, 'git', 'merge', '--ff-only', sha)
    check_tree(root, sha)
    command(root / 'web', 'npm', 'run', 'build')
    pin = root / 'global.json'
    created = False
    try:
        if not os.path.lexists(pin):
            with pin.open('x') as f:
                created = True
                f.write('{"sdk":{"version":"10.0.101","rollForward":"disable"}}\n')
        command(root, 'dotnet', 'build', 'src/Marten', env=env)
    finally:
        if created:
            pin.unlink()
    check_tree(root, sha)


def launch_artifacts(root):
    """Hash paths, permissions and bytes of the prebuilt server and UI files."""
    required = {DLL, f'{BUILD_DIR}/Marten.deps.json', f'{BUILD_DIR}/Marten.runtimeconfig.json',
                f'{WEB_DIST}/index.html'}
    hashes = {}
    for tree in (BUILD_DIR, WEB_DIST):
        directory = root
        for part in Path(tree).parts:
            directory = directory / part
            if directory.is_symlink() or not directory.is_dir():
                raise CutoverError(f'Prebuilt artifact directory missing or unsafe: {tree}')
        digest = hashlib.sha256()
        pending = [directory]
        while pending:
            current = pending.pop()
            for path in sorted(current.iterdir()):
                rel = path.relative_to(root).as_posix()
                mode = path.lstat().st_mode
                if stat.S_ISDIR(mode):
                    pending.append(path)
                    continue
                if not stat.S_ISREG(mode):
                    raise CutoverError(f'Prebuilt artifact is not a regular file: {rel}')
                content = hashlib.sha256()
                with path.open('rb') as f:
                    for chunk in iter(lambda: f.read(1024 * 1024), b''):
                        content.update(chunk)
                required.discard(rel)
                digest.update(os.fsencode(rel) + b'\0')
                digest.update(f'{stat.S_IMODE(mode):o}:{content.hexdigest()}\n'.encode())
        hashes[tree] = digest.hexdigest()
    if required:
        raise CutoverError(f'Required prebuilt artifacts missing: {", ".join(sorted(required))}')
    return hashes


def verify_artifacts(root, expected):
    if not expected:
        raise CutoverError('Missing artifact seal; cannot verify the original prebuild')
    actual = launch_artifacts(root)
    if actual != expected:
        raise CutoverError('Prebuilt launch artifacts changed since prebuild; not starting')


def launch(root, log, env):
    with log.open('w') as output:
        return subprocess.Popen(['nohup', 'dotnet', DLL], cwd=root, env=env,
                                stdin=subprocess.DEVNULL, stdout=output,
                                stderr=subprocess.STDOUT, start_new_session=True)


def private_dir(path):
    st = path.stat()
    if not stat.S_ISDIR(st.st_mode) or st.st_uid != os.getuid() or st.st_mode & 0o077:
        raise CutoverError('Use an existing mode-0700 job directory owned by the invoking user')


def atomic_json(path, value):
    fd, tmp = tempfile.mkstemp(dir=path.parent, prefix=f'.{path.name}-')
    try:
        with os.fdopen(fd, 'w') as f:
            json.dump(value, f, indent=2)
            f.write('\n')
            f.flush()
            os.fsync(f.fileno())
        os.replace(tmp, path)
    finally:
        Path(tmp).unlink(missing_ok=True)


def deploy(args, host, root):
    job = args.job.resolve()
    private_dir(job)
    expected_home = args.expected_home.resolve()
    home = Path.home()
    actual_home = home / '.marten'
    token_file = actual_home / 'secrets/ui-token'
    log = home / '.marten-server.log'
    snapshot = json.loads((job / 'generation.json').read_text())
    old = Generation(snapshot['pid'], Birth(**snapshot['birth']), snapshot['command'], snapshot['cwd'])
    result = job / 'result.json'
    base = {'sha': args.sha, 'checkout': str(root), 'home': str(expected_home), 'old': asdict(old)}
    env = {k: v for k, v in os.environ.items() if k not in UNSET}
    # Serialize helpers across jobs/checkouts. Never unlink the lock inode.
    fd = os.open(LOCK_DIR / f'.marten-cutover-{os.getuid()}-{PORT}.lock',
                 os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW | os.O_NONBLOCK, 0o600)
    with os.fdopen(fd, 'r+') as lock:
        st = os.fstat(fd)
        if not stat.S_ISREG(st.st_mode) or st.st_uid != os.getuid() or st.st_mode & 0o077:
            raise CutoverError('Deployment lock is not a private file owned by the invoking user')
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as e:
            raise CutoverError('Another cutover helper owns the deployment lock') from e
        if args.mode == 'resume':
            prior = json.loads(result.read_text())
            if prior.get('state') != 'drain-timeout' or any(prior.get(k) != v for k, v in base.items()):
                raise CutoverError('Resume requires this exact job and snapshot to have timed out draining')
            if not exited(host, old):
                raise CutoverError('Old generation has not exited; leaving timeout result intact')
            base['artifacts'] = prior.get('artifacts')
        elif result.exists():
            raise CutoverError('Job already used; create a new job or resume a drain timeout')
        stage = 'preflight'
        child = None
        try:
            atomic_json(result, dict(base, state='running'))
            if not actual_home.is_symlink() or not expected_home.is_dir() or actual_home.resolve() != expected_home:
                raise CutoverError('Marten home is not the expected mounted symlink target')
            if 'MARTEN_HOME' in os.environ and Path(os.environ['MARTEN_HOME']).resolve() != expected_home:
                raise CutoverError('Inherited MARTEN_HOME would launch against different state')
            web_override = env.get('MARTEN_WEB_ROOT')
            if web_override and (root / web_override).resolve() != root / WEB_DIST:
                raise CutoverError('MARTEN_WEB_ROOT would bypass the prebuilt UI')
            if args.mode == 'release' and inspect(host, root, PORT) != old:
                raise CutoverError('Listener did not match the reviewed old generation')
            if args.mode == 'resume':
                check_port(host, PORT)
            token = token_file.read_text().strip()
            if not token or not re.fullmatch(r'[!-~]+', token):
                raise CutoverError('UI token missing or invalid')
            # Do not truncate or change permissions while the old server is running.
            if os.path.lexists(log) and (not log.is_file() or not os.access(log, os.W_OK)):
                raise CutoverError('Existing server log is not a writable regular file')
            stage = 'prebuild'
            if args.mode == 'release':
                prebuild(root, args.sha, env)
                base['artifacts'] = launch_artifacts(root)
            stage = 'drain'
            if args.mode == 'release':
                if args.notice_deadline is not None:
                    wait = args.notice_deadline - time.monotonic()
                    if wait > 0:
                        print(f'Waiting {wait:.0f}s for scheduled cutover', flush=True)
                        time.sleep(wait)
                check_tree(root, args.sha)
                verify_artifacts(root, base['artifacts'])
                drain = (LegacyDrain(PORT, token) if args.legacy_first_install
                         else LeaseDrain(host, root, PORT, old, token))
                stop(host, root, PORT, old, args.grace, args.late_wait, args.kill_after_grace, drain)
            stage = 'startup'
            if not exited(host, old):
                raise CutoverError('Old generation still running; not starting')
            # For resume, use the original build even if origin/main has moved.
            check_tree(root, args.sha)
            verify_artifacts(root, base.get('artifacts'))
            check_port(host, PORT)
            child = launch(root, log, env)
            healthy(host, child, PORT, token)
        except Exception as e:
            state = 'drain-timeout' if isinstance(e, DrainTimeout) else 'failed'
            atomic_json(result, dict(base, state=state, stage=stage, detail=str(e),
                                     new_pid=child.pid if child else None))
            raise
        atomic_json(result, dict(base, state='ok', new_pid=child.pid,
                                 detail='UI and authenticated status API healthy'))
        print(f'OK: {args.sha}, old PID {old.pid}, new PID {child.pid}', flush=True)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('mode', choices=('inspect', 'release', 'resume'))
    p.add_argument('--checkout', required=True, type=Path)
    p.add_argument('--job', required=True, type=Path)
    p.add_argument('--expected-home', type=Path)
    p.add_argument('--sha')
    p.add_argument('--not-before', type=int, help='optional earliest TERM time for release (Unix seconds)')
    p.add_argument('--grace', type=int, choices=range(1, 601), default=60, metavar='1..600')
    p.add_argument('--late-wait', type=int, choices=range(1, 601), default=60, metavar='1..600')
    p.add_argument('--kill-after-grace', action='store_true', help='allow same-generation KILL after both waits')
    p.add_argument('--legacy-first-install', choices=(LEGACY_FIRST_INSTALL,), metavar='LIVE_SHA',
                   help='attest the pinned old runtime is under a quiet maintenance window; no KILL')
    args = p.parse_args()
    if args.mode != 'inspect' and (not args.expected_home or not re.fullmatch(r'[0-9a-f]{40}', args.sha or '')):
        p.error('release/resume requires --expected-home and the full tested --sha')
    args.notice_deadline = None
    if args.not_before is not None:
        if args.mode != 'release':
            p.error('--not-before is only valid for release')
        now = time.time()
        if not now - 180 <= args.not_before <= now + 3600:
            p.error('--not-before must be within 180 seconds in the past or one hour in the future')
        args.notice_deadline = time.monotonic() + (args.not_before - now)
    if args.mode != 'release' and args.kill_after_grace:
        p.error('only release may escalate')
    if args.legacy_first_install and (args.mode != 'release' or args.kill_after_grace
                                      or args.sha == LEGACY_FIRST_INSTALL):
        p.error('legacy first install requires a new release SHA and disallows KILL')
    os.environ['PATH'] = PATH_PREFIX + os.environ.get('PATH', '')
    try:
        host = Darwin()
        root = args.checkout.resolve(strict=True)
        if args.mode == 'inspect':
            job = args.job.resolve()
            private_dir(job)
            if (job / 'result.json').exists() or (job / 'generation.json').exists():
                raise CutoverError('Use a fresh job directory')
            atomic_json(job / 'generation.json', asdict(inspect(host, root, PORT)))
            print(f'Inspect {job / "generation.json"} before announcing the release')
        else:
            deploy(args, host, root)
    except (CutoverError, OSError, ValueError, KeyError, subprocess.SubprocessError) as e:
        p.exit(1, f'Cutover failed: {e}\n')


if __name__ == '__main__':
    main()
