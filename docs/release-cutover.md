# Planned macOS cutover on port 7450

This is the orchestrator's release procedure, not a supervisor or a generic
process killer. Run as the existing Marten login user on macOS (not root), with
Python 3.10+, `lsof`, Node and the .NET SDK. Do not use it to start a second
instance or run another deploy/start command alongside it.

1. Review and test the exact intended release in a separate worktree: run
   `npm ci && npm run build` in `web/`, `dotnet test tests/Marten.Tests`, and
   `python3 -B -m unittest discover -s tests -p test_marten_cutover.py`. Tests of
   the helper use fake processes or disposable children/OS-assigned ports.
   Never point unreleased Marten code at the live home or port. After the
   reviewed PR is merged, record the full **tested** `origin/main` SHA; retest
   if the merge result differs from what was tested. Don't substitute an
   untested newer SHA when the helper rejects a moved branch.
2. From a reviewed worktree, stage a copy of the helper in a new private job
   directory (keep that same copy and directory through the release). Run
   `inspect` and review `generation.json`: it records the sole listener's PID,
   kernel start time/identity, UID, command and cwd. Confirm it is the expected
   generation. Only the original `dotnet run` apphost or the exact detached
   launch below from this checkout is supported.

   ```sh
   job=$(mktemp -d /private/tmp/marten-cutover.XXXXXXXX)
   checkout=/Volumes/External2/projects/Marten
   mounted_home=/Volumes/External2/marten-home
   cp scripts/marten-cutover.py "$job/cutover.py"
   python3 "$job/cutover.py" inspect --job "$job" --checkout "$checkout"
   cat "$job/generation.json"
   ```

3. Check web dependencies before scheduling the job. If they are missing or
   the lockfile changed, fetch `origin/main` and confirm it is still the exact
   tested SHA. On tracked-clean `main` in the live checkout, fast-forward only
   to that SHA, then run `npm ci` in `web/`. The helper builds against the
   installed dependencies; it does not install them. If `origin/main` moved,
   stop and verify the new release first.

   Give a heads-up before launching the job: write a note to
   `~/marten-mailbox/to-openthedas/` following its README and message
   `client:openthedas`. No fixed waiting window is required. Submit the
   following command with **detached `process_start`** (substitute the actual
   paths and tested `sha`; shell variables from staging do not carry over).
   Do not execute it as an in-process PowerShell tool or wait for the result
   in a foreground task.

   ```sh
   python3 -u "$job/cutover.py" release --job "$job" --checkout "$checkout" \
     --expected-home "$mounted_home" --sha "$sha"
   ```

   To schedule the earliest TERM time explicitly, add `--not-before` with a
   Unix timestamp (seconds). The helper accepts timestamps up to one hour
   ahead or three minutes in the past. If prebuild finishes after the scheduled
   time, the helper proceeds without waiting. Omit the flag for no time gate.

   The helper verifies the mounted `~/.marten` symlink, the old generation,
   and tracked-clean `main`; fetches `origin/main`, checks the expected SHA
   **before** advancing via fast-forward, then runs `npm run build` in `web/`
   and `dotnet build src/Marten`. The .NET build uses an existing `global.json`
   or temporarily pins SDK `10.0.101` and removes only the pin it created.
   It records SHA-256 fingerprints of the built server and UI trees (paths,
   file modes and bytes) and checks them before TERM and startup. A
   `MARTEN_WEB_ROOT` override must point to this checkout's `web/dist`.
   Do not edit the checkout/build outputs, config, or generation snapshot
   midway through the job.
4. After prebuild, the optional time gate, and artifact verification, the
   helper requests a short pre-restart drain lease from the old generation.
   It polls until active operations and pending messages are both zero;
   `busy`, expiry, HTTP/auth failures and unexpected responses stop the job
   without TERM. It re-verifies the generation, fetches a fresh `ready`
   snapshot, and checks listener identity, process birth and remaining lease
   budget before signaling. There is no automatic legacy fallback.

   **After** TERM it waits 60 seconds by default, then keeps watching for
   another 60 seconds so an exit just past grace still proceeds through
   the same startup checks. Both waits can be
   set with `--grace` and `--late-wait` (seconds). **There is no KILL by
   default.** For an explicitly approved last resort, add `--kill-after-grace`
   at launch. It can lose data and will only send KILL after both waits, against
   the same re-verified generation, then wait at most another 10 seconds for
   exit. macOS validates the PID and PID version at signal delivery; if its
   generation API is unavailable the helper fails rather than using plain PID
   signals. A changed identity or unrelated listener is not a signal target.
5. The helper checks for listeners and probes binds immediately before startup.
   Its per-user/port lock serializes helpers, not manual launches. The probes
   are released for Marten to bind; Kestrel owns the final bind, and a competing
   listener or failed bind is reported rather than launching again. From the
   checkout root the launch is equivalent to:

   ```sh
   env -u MSBuildSDKsPath -u MSBuildExtensionsPath -u DOTNET_HOST_PATH \
     -u MSBUILDFAILONDRIVEENUMERATINGWILDCARD \
     nohup dotnet src/Marten/bin/Debug/net10.0/Marten.dll \
     > ~/.marten-server.log 2>&1 < /dev/null &
   ```

   This keeps existing server log permissions, with normal umask semantics if
   the log is newly created. The UI and authenticated status endpoint must
   respond and the new PID must be the only listener. If `lsof` reports the
   new PID alongside other PIDs, the helper tolerates only verified same-user
   direct children, such as a subprocess that inherited the socket. It allows
   at most two seconds from the first shared observation, within the existing
   startup budget, and does not probe HTTP while ownership is shared. A
   listener without the new PID, an unrelated listener, or persistent sharing
   fails startup. Read the detached helper log and the mode-0600, atomically
   replaced `$job/result.json` (`running`,
   `ok`, `drain-timeout`, or `failed`). Report the merged changes, observed
   restart result and any follow-up to Sera. An abrupt helper interruption may
   leave `running`; inspect the host before taking any further action.

### Drain protocol used by the detached helper

The helper talks directly to `127.0.0.1:7450` (no proxies or redirects) using
`Authorization: Bearer …` from the mounted home's `secrets/ui-token`. It keeps
both the bearer and its randomly generated lease ID in memory, not in command
arguments, logs, or job files. The API is for the local owner only.

- `POST /api/release/drain` sends JSON `{id,pid,leaseSeconds}`. `id` is a fresh
  32-character lowercase hex string (128 random bits); `pid` is the verified
  old PID. The API accepts a nonrenewable lease of 1–300 seconds; the helper
  requests 120 seconds. It never renews or takes over another lease.
- `GET /api/release/drain` and `DELETE /api/release/drain` carry
  `X-Marten-Drain-Id` and `X-Marten-Drain-Pid` (decimal PID). POST and GET
  return a `200` no-store snapshot `{id,pid,state,remainingMs,activeOperations,pendingMessages}`.
  `draining` waits; `busy` is not readiness and aborts the cutover. `ready`
  requires both counters to be zero. The API rejects malformed requests with
  `400` and stale, wrong-PID or competing leases with `409`.
- Before TERM, a failure after acquiring a lease (or after an ambiguous POST)
  triggers one best-effort DELETE for that same ID and PID if the old
  generation still verifies. Successful release is `204`. If the helper
  crashes or cannot confirm release, the lease expires on the old server;
  nothing is carried to the replacement. After TERM the helper does not
  release the lease; it leaves shutdown to the old server.
- Polling is bounded by the original lease budget. Each poll can only shorten
  the local deadline (request-start monotonic time plus the reported remaining
  milliseconds). The helper requires more than two seconds of conservative
  lifetime left at signal time. The kernel binds delivery to the verified
  PID generation, not just the numeric PID.

### One-time first install from `fda0f931`

The currently deployed `fda0f931680b4ecec0942949a888282d82ebc634` predates the
lease endpoint. Only for the first cutover from that version, append:

```sh
--legacy-first-install fda0f931680b4ecec0942949a888282d82ebc634
```

This flag is the operator's affirmation that the reviewed `generation.json`
is the previously verified live SHA/PID and that the maintenance window is
quiet, with no separately known ingress producers or active standalone work
(e.g. schedules/replays). The old status API cannot attest its running SHA or
report all work. Do not use the flag on a drain-capable server, with
`--kill-after-grace`, or to retry a failed modern drain. The helper independently
requires authenticated GET of the lease endpoint to return `404`. Any other
status, timeout, or malformed status snapshot stops the job without TERM.

The helper observes the full agent inventory from authenticated `GET /api/status`
twice a second apart and once more just before TERM. Every agent (including
closed and effectively paused agents) must report `status: "idle"`, exact zero
`pending`, and no `modelCall`; the agent IDs must be unchanged. `pending` is
full in-memory inbox depth, not just the user-authored entries visible in
`pending-messages`. Closed and effective pause are separate flags and can be
idle; `status: "paused"` can be an unfinished turn held at a dispatch boundary
and is not proof of quiet. Error, running, waiting, or unknown states also
block the first install. **These are observations, not an atomic admission
barrier.** New work can arrive after any check; quiet snapshots alone do not
establish external silence. There is no force override for this mode. Omit
this flag for future releases.

### If the drain or startup fails

- A failed pre-restart drain sends no TERM and is not resumable. Inspect the
  failure and old generation; resolve any outstanding work or let a possibly
  unreleased lease expire before deciding whether to try a fresh job. Do not
  use first-install mode as an override.
- On `drain-timeout` (after TERM), do not start over or send another signal. If the verified
  old generation exits after the bounded watch, use the same command with
  `resume` instead of `release`, the **same** job/checkout/home/SHA, and omit
  `--not-before` and escalation options. It checks the original timeout and
  generation, refuses while the old PID is alive or changed, checks the port,
  revalidates the same SHA and stored artifact fingerprints, and tries one
  startup without signaling. It does not fetch or adopt a newer release during
  recovery. Missing or changed artifacts, including a timeout result that
  predates artifact fingerprints, require manual review rather than a launch.
- On identity ambiguity, an occupied port, or an unhealthy new build, inspect
  the recorded stage, PID, listeners and logs before any manual recovery.
  The helper won't kill an unexpected process or auto-start a fallback. This
  in-place build does not preserve an independent known-good artifact, and an
  unhealthy process may still be alive. A speculative second launch could
  produce two runtimes touching the same state.
