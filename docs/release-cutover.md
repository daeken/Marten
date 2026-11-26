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

3. Give advance notice: write a note to `~/marten-mailbox/to-openthedas/`
   following its README and message `client:openthedas`. Set `not_before` to
   at least 180 seconds after the notice (`not_before=$(($(date +%s) + 180))`).
   Submit the following command with **detached `process_start`** (substitute
   the actual paths, tested `sha`, and timestamp into its command string;
   shell variables from the staging step do not carry over). Do not execute it
   as an in-process PowerShell tool or wait for the result in a foreground task.

   ```sh
   python3 -u "$job/cutover.py" release --job "$job" --checkout "$checkout" \
     --expected-home "$mounted_home" --sha "$sha" --not-before "$not_before"
   ```

   The helper verifies the mounted `~/.marten` symlink, the old generation,
   and tracked-clean `main`; fetches `origin/main`, checks the expected SHA
   **before** advancing via fast-forward, then runs `npm run build` in `web/`
   and `dotnet build src/Marten`. The .NET build uses an existing `global.json`
   or temporarily pins SDK `10.0.101` and removes only the pin it created.
   The helper won't signal before the supplied time or within three minutes of
   its own launch. Do not edit the checkout/build outputs, config, or generation
   snapshot midway through the job.
4. The helper re-verifies the generation before TERM. It waits 60 seconds by
   default, then keeps watching for another 60 seconds so an exit just past
   grace still proceeds through the same startup checks. Both waits can be
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
   respond and the new PID must be the only listener. Read the detached helper
   log and the mode-0600, atomically replaced `$job/result.json` (`running`,
   `ok`, `drain-timeout`, or `failed`). Report the merged changes, observed
   restart result and any follow-up to Sera. An abrupt helper interruption may
   leave `running`; inspect the host before taking any further action.

### If the drain or startup fails

- On `drain-timeout`, do not start over or send another signal. If the verified
  old generation exits after the bounded watch, use the same command with
  `resume` instead of `release`, the **same** job/checkout/home/SHA, and omit
  `--not-before` and escalation options. It checks the original timeout and
  generation, refuses while the old PID is alive or changed, checks the port,
  revalidates the same SHA and existing prebuild, and tries one startup without
  signaling. It does not fetch or adopt a newer release during recovery.
- On identity ambiguity, an occupied port, or an unhealthy new build, inspect
  the recorded stage, PID, listeners and logs before any manual recovery.
  The helper won't kill an unexpected process or auto-start a fallback. This
  in-place build does not preserve an independent known-good artifact, and an
  unhealthy process may still be alive. A speculative second launch could
  produce two runtimes touching the same state.
