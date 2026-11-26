# Marten v0 spec

Last updated 2026-11-18.

## Stance

Marten is a small agent runtime that replaces all of Sera's agentic coding. It is greenfield and stays tiny: build what is needed now, not what agents might need later.

- **Wide open by default.** Agents are trusted: no sandbox, and with no `rules` and `reviewTools` off, no approvals. Sera's filter is opt-in. `rules` in config.json is an ordered list of `{tool, match, agents, projects, action, reason}`. `tool` is a name with `*` wildcards; `match` is a regex tried against the call's string arguments, such as a PowerShell command. `action` is `allow`, `deny` or `ask`, and the first matching rule wins. `ask` puts an Allow once / Deny card in Sera's inbox and fails the call; after she allows it, the identical call goes through once. The filter is a guardrail against mistakes, not a sandbox: it sees tool calls, not what a script does once running.
- **Not reproducible, on purpose.** Agents may mutate process state. Reproducibility is a non-goal for now.
- **Restart to change the core.** New C# tools are compiled in. There is no C# hot reloading. On a normal stop (SIGTERM or Ctrl-C), Marten stops admitting model calls and cancels model requests and retry waits. Active tools get `stopGraceSeconds` (default 5) to finish and save results. After that, Marten cancels remaining work, makes a bounded wait for cleanup, and kills tracked foreground process trees, including those shared by overlapping PowerShell runs. It does not stop detached `process_start` jobs.
- **Startup recovery.** Existing agents get a restart notice in their transcript, visible in their next model context. An open agent with interrupted work can resume from the recorded history, respecting pause; merely announcing a restart does not wake an idle agent. Unanswered tool calls receive an interruption result, and interrupted schedule runs and manual notebook replays are reported when there is a durable record. Old invocations are never re-executed by recovery; recurring schedules can still run at their next due time.
- **Abrupt stops.** SIGKILL, power loss and host reboots bypass the drain. Recovery can only use what made it to disk; streamed text, pending in-memory messages and a tool's result can be lost even if the tool had side effects. Work that escaped tracking (for example, a child that reparented itself) can outlive the runtime, and a host reboot terminates detached jobs too. Marten does not kill old PIDs from disk or relaunch jobs on startup. Check actual state before retrying.
- **What a turn is.** For steering, a turn is one model call: immediate messages land between model calls, at a tool boundary. Everywhere else, a turn runs from a wake until the agent goes idle. A recorded tool call without a saved result is reported to the agent as interrupted on restart, and is never automatically re-run.

## Shape and storage

Marten is a C# monolith with a separate web frontend, and all of its state lives in `~/.marten`.

- **Backend:** a single C# process.
- **Exposure:** the backend listens on localhost only. Every request carries a random token, and the Host and Origin headers are checked, so a web page Sera visits cannot drive an agent. The backend serves the built frontend, so the UI is same-origin. The startup banner prints the UI URL without the token; users enter the token from `secrets/ui-token`.
- **Platforms:** macOS first, Linux a close second. Windows is not a target.
- **Frontend:** a simple, clean React web app, separate from the backend. Mobile and desktop apps come later.
- **Storage:** everything lives in `~/.marten`: config, secrets, prompts, tools, transcripts, agent state, screenshots and full tool output. It is plain files, not a git repo. Durability comes from snapshots: every `backup.intervalMinutes` (60), when anything changed, Marten writes `~/.marten-backups/marten-<utc stamp>.tar.gz`, leaving out secrets, browser profiles and worktrees. It keeps the newest 48 plus one a day for 30 days. With a passphrase saved (in the UI, stored in `secrets/backup-passphrase`), snapshots are AES-256-GCM encrypted. `marten backup` takes one now; `marten restore <file> [<dir>]` extracts one into a new folder, never over a live home.
- **Extending it:** tools written in PowerShell at runtime, or tools written in C# and built into the runtime.

## Agents and projects

Marten is project-centric: each project has one orchestrator, which spins up task agents to own a task or a portion of the work.

| Kind | Lives in | Role |
| --- | --- | --- |
| Chat agent | No project | Someone to talk to outside any project |
| Orchestrator | One project | Runs the project and spawns task agents |
| Task agent | One project | Owns one task or portion |

- **A project is abstract.** It has a place in `~/.marten` and is bound to a repo, a directory, or whatever else it works on.
- **Binding means two things.** Agents know about the bound locations, and AGENTS.md files are picked up from the usual places in them.
- **System prompt.** The Marten-wide prompt comes first, then a short section for the agent's kind (chat, orchestrator, task), then the project prompt. The runtime captures these three sources separately when it builds the system prompt at spawn, on reload, during compaction, or when starting fresh context. It records the same snapshot it used to build `system.md`, so a later edit is not treated as already applied. At the start of a turn, changed sources (including cleared or removed text) trigger a rebuild and a note to the agent without dropping its conversation. Without a saved baseline, the agent rebuilds once on its next turn. Otherwise the prompt stays stable for caching between rebuilds. AGENTS.md, project memory and task notes are refreshed when the prompt is rebuilt, but do not independently trigger a prompt reload.
- **Shared tree by default.** Worktrees are supported as a per-spawn option, and the default is a project setting. Orchestrator rule: concurrent writers get worktrees.
- **Closed task agents keep their history.** Their orchestrator can use `close_agent` even while a turn is active. Close cancels that turn, including model and slot waits; it discards late model output and does not dispatch the rest of the tool batch. Unanswered tool calls get interruption results. Cancellation cannot undo a tool that has already started; check for partial effects before retrying.
- **Reopening is not resuming.** A new message from the task's orchestrator or Sera reopens a closed agent. Sera can also explicitly reopen it: undelivered messages can wake a fresh turn, but the cancelled turn cannot continue, even if close and reopen happen back-to-back. Automatic notifications can queue while closed but cannot reopen it.

## Project memory

Each project has one shared memory that every agent in it reads, and it survives every compaction. It holds what an agent would otherwise have to ask Sera again.

- **An index and optional topics.** `MEMORY.md` in the project's directory in `~/.marten` holds standing rules and pointers. Link details as `[Topic](memory/topic.md)` in the index. Topics are direct Markdown files in the sibling `memory/` directory, not nested files or symlinks.
- **Index in context, topic bodies on demand.** When the system prompt is built, agents get the index and a sorted list of topic paths. The runtime never injects topic bodies; use `read_file` on the relevant file for its latest contents.
- **A size cap on the index.** The `memoryCapBytes` setting defaults to 16 KiB. An edit cannot grow the index beyond the cap; prune or move details to topics. Topic files do not count toward the index cap.
- **Edited with the atomic file editor.** Use `edit_file` on either the index or a topic. If the file changed since the agent read it, re-read and retry. Use `create` for a missing file. To initialize a zero-byte file such as a new `MEMORY.md`, read it first, then pass a single `replace` with an empty string anchor.
- **Fresh without invalidating the cached prefix.** At the start of a turn agents see the index's changed lines and, if the inventory changed, the current topic paths. An agent without a saved topic inventory gets the list at its next turn (or in its rebuilt system prompt if a rebuild happens first). Topic edits do not inject diffs into agent context: re-read the topic when needed.
- **Audited.** Edits to both the index and topics use one audit log with the agent's id, reason, relative path and changed lines. Old records without a path refer to `MEMORY.md`.

### Entry shape

A memory entry in the index or a topic occupies one line, leads with a short CAPS tag, then says what it is about, and cites its source when Sera said it:

```
- WINE PAUSED (Sera, 2026-11-18 02:54Z, msg_…): stop running DA:O in Wine until she says go.
```

- **Sera's words are cited.** An entry that records something Sera decided carries the time and the message id, and quotes her where the wording matters. Agents treat cited entries as Sera's standing rules and uncited entries as notes.
- **Only orchestrators and Sera delete cited entries.** A task agent may add a cited entry, or edit an uncited one, but only an orchestrator or Sera may remove a cited one. Removing one tells Sera through the UI.
- **Pointers, not payloads.** Long material lives in files in the bound repo or in `~/.marten`, and memory links to them, for example "Merge rule: see CLAUDE.md" or "Accepted parity list: docs/ACCEPTED.md".

### Suggested sections

These are conventions for the project prompt, not enforced by the runtime:

1. **Standing asks and north star.** Sera's goal for the project and how continuously to work.
2. **Rules.** Hard constraints, such as forbidden code, secrets, the merge rule and machine etiquette.
3. **Roster.** Each long-lived task agent, what it owns and what it may do, such as merge or post externally.
4. **Live state.** What's pinned, deployed or parked right now, and what waits on Sera.
5. **Settled.** Things that were decided or accepted, so nobody re-files them.

### Agent notes

Beside the shared memory, each task agent keeps a `NOTES.md` in its own state. It is injected only into that agent's context and also survives compaction. It holds the agent's working knowledge: traces, open leads, how its tools behave. When a fact matters beyond one agent, the agent promotes it to the shared memory.

### Not day 0

- Structured or queryable memory, embeddings or search.
- Locking finer than one file.
- Memory shared across projects. Facts about Sera that hold in every project go in the Marten-wide prompt.

## PowerShell and tools

PowerShell is embedded in-process and is the default way agents act on the world.

**Running commands**

- **In-process on purpose.** It lets agents introspect the core. The trust this requires is accepted.
- **A `$Marten` object in every runspace.** It exposes the project, the agent roster and the message bus, for introspection and scripting. Messaging, elision, model changes and spawning are direct tools, and scripts can reach them through it.
- **One-off commands.** Each command runs in a fresh runspace. No PowerShell variables carry from one run to the next. Process-wide state, such as environment variables and loaded assemblies, is shared by every agent and the core; that is accepted for now.
- **One command at a time.** PowerShell commands for one agent never overlap, so the state variable has one writer. A scheduled command runs as its owner and waits its turn.
- **Named command slots.** Config's `slots` maps case-sensitive names to positive capacities shared by all agents. `$Marten.WithSlot(name, { ... })` runs foreground work under one slot, releasing it when the script block exits, including on failure. The queue uses the command's cancellation and timeout; scheduled runs use their command timeout. Nested slots are rejected to prevent self-deadlock and inconsistent acquisition order. A hung, abandoned script block retains its slot until it actually exits, so live work does not exceed the configured cap.
- **One state variable per agent.** A single named global is serialized to JSON and stored with the agent's state. It may hold only JSON-serializable types; anything else is a loud error.
- **Timeouts.** Every command has a timeout, set in config and overridable per call. On timeout the runtime stops the pipeline and kills any child process the command started.
- **Hung commands are abandoned.** If the stop does not return within a few seconds, the runspace is left behind and flagged in the UI, and the agent gets a timeout error. The runtime never restarts itself; that is Sera's call.
- **Long-running work.** Work that finishes, like a long test run, runs in the foreground with a longer timeout. Work that never finishes, like a dev server, goes through one built-in that starts it detached, logs to a file, and records it with the agent. Agents can list and stop what they started, and the UI shows it. Closing an agent stops its processes; a runtime restart leaves them running.
- **Command output.** Strings pass through as written. Other objects are rendered as text at a fixed width, and errors are included.

**Tools**

- **Direct tool calls.** Every tool can be called either directly or from PowerShell, MCP tools included. Agents never have to go through PowerShell to read a file, say, but they can. PowerShell is overkill for many calls, confuses models and costs context. On day 0 the set of exposed tools stays minimal.
- **Notebook.** Every PowerShell run is recorded in the agent's `notebook.jsonl` as a cell: command, working directory, `$State` in and out, output, and duration. Because each run gets a fresh runspace, those are everything it depended on inside Marten. The UI's Notebook view lists the cells. A cell can be re-run with its recorded inputs, which leaves the agent's own `$State` untouched and reports whether the output and state came out the same. The whole notebook exports as a Jupyter (.NET Interactive PowerShell) notebook in which every cell restores its own directory and state. Agents read theirs with `$Marten.Notebook()`.
- **Runtime tools.** Agents can write new tools in PowerShell, scoped to a project or available to every agent. With `reviewTools` on, a tool loads only once Sera approves its exact text: `define_tool` puts the script on a card for her, and an edit made any other way drops the tool until she approves again, from the card or the project's Memory page. Tools that exist when review is turned on are grandfathered.
- **Built-in tools.** Tools written in C# are built into the runtime.
- **MCP tools** are callable from PowerShell like any other command.
- **Images.** When an agent calls an MCP tool directly and it returns an image, the image is fed back to the model. From PowerShell, a `View-Image` cmdlet (name not final) shows an image to the model.
- **File reader.** A base tool reads a whole file or a range of lines from it.
- **Atomic file editor.** A base tool takes a list of insertions, replacements and deletions for one file. It applies them only if every edit matches. Matches are exact and each anchor must match once. All edits are checked against the file as it stood. The edit fails if the file changed since the agent last read it. A failure names the edit that missed.

## Context management

Each kind of agent compacts differently, and all of them shed old tool results.

| Agent | Compaction |
| --- | --- |
| Chat agent, orchestrator | Rollover: the first half of the context is compacted and the rest stays intact |
| Task agent | The whole context is compacted |

- **Append-only log.** Each agent's transcript is an append-only log. What the model sees is computed from it, so compaction, elision and starting fresh context destroy nothing.
- **Start fresh context.** After confirmation in the UI (`POST /api/agents/{id}/start-fresh-context`, UI token only; no outside action), Marten appends an explicit, durable boundary. Future model requests and compactions use only entries after the latest boundary, excluding all older history and compaction summaries. The boundary is visible in the transcript and paging still reaches earlier entries. The prompt is rebuilt from current sources, including AGENTS.md, project memory and task notes, and the new baselines replace the old ones without replaying their diffs. Context estimates start over; identity, children, tasks, schedules and queued input stay intact. The action does not wake the agent. Admission is serialized with turns; an active turn (even while parked or waiting), an effective pause, or shutdown prevents admission with HTTP 409.
- **Elision.** Old tool results are replaced by a note saying how to see the full result. Elision triggers on a token budget and runs in batches, because a window that slides every turn breaks prompt caching.
- **Truncation on arrival.** An oversized result is truncated when it arrives, with a pointer to the full text.
- **Elide now.** Every agent has a tool to elide its last N results at once, for when one of them floods the context.

## Messaging

Agents message within a project and across orchestrators, but task agents never message across projects.

| From | To | Modes |
| --- | --- | --- |
| Sera | Any agent | Immediate or queued |
| Orchestrator | Its own task agents | Immediate or queued |
| Chat agent | Any orchestrator | Immediate or queued |
| Task agent | Agents in its own project | Queued |
| Orchestrator | Other orchestrators, chat agents | Queued |

- **Immediate** injects the message right away, as if steering. It lands at the recipient's next tool boundary.
- **Queued** holds the message until the recipient goes idle, and wakes a recipient that already is.
- **Loop breaker.** When two agents exchange `loopMessages` (20) messages within `loopWindowMinutes` (10) without Sera, with at least a quarter going each way, the send that tips it is refused, both agents are paused, and Sera gets an inbox message. A message from Sera to either agent clears the count; `loopMessages: 0` turns it off.

## Tasks and wake-ups

Besides messages, two things can wake an agent: an unfinished task and a schedule. All three go through one wake path that records the reason.

**Task lists**

- Each agent has a task list that lives outside its context, so it survives compaction.
- An orchestrator can add tasks to its task agents' lists.
- A task is open, waiting, blocked or done. Waiting and blocked carry a reason.
  - **Waiting** is on another agent or a schedule. It does not trigger open-task nudges or reach Sera.
  - **Blocked** is on Sera.
- An agent that goes idle with an open task is nudged awake. To stop on purpose, it marks the task waiting, blocked or done.
- After three nudges in a row with no task changing state, nudging stops and the agent is flagged in the UI.
- If the orchestrator still has waiting tasks when it and all its non-closed workers are idle, it gets a project-idle notice listing those tasks. This is a heads-up, not a state change or a claim that a prerequisite is done. Going idle again after reading the notice does not by itself trigger another.
- When a task turns done, waiting or blocked, the agent that assigned it is woken.
- **Task dependencies.** `task_wait_for(id, agent, task_id)` puts the caller's task `id` on hold until task `task_id` on `agent`'s list is done. Choose yourself or another agent in your project by id or unambiguous name. Use `$Marten.Agents()` to find agent ids and `$Marten.Tasks('name-or-id')` to inspect that owner's task ids, states and `WaitingOn` references; `$Marten.Tasks()` still lists your own. A dependency uses ordinary waiting notifications, not a blocked alert. Completion reopens the dependent task rather than marking it done. An already-done prerequisite leaves the dependent task open immediately. Use plain `task_update` waiting with a reason for waits that aren't tied to a task.
- **Completion contracts.** Whoever assigns a task may attach a check: a bash command run in the owner's working directory (timeout `checkTimeoutSeconds`, 900). Marking the task done runs it; unless it exits 0 the task stays as it was and the agent gets the output. The UI shows each check and its last result. Only the assigner sets the check.
- An orchestrator with a blocked task tells Sera by sending her a message.

**Schedules**

- A schedule is a PowerShell command plus a one-shot time or an interval. The agent that creates it owns it.
- The command runs without the model. Output is delivered to the owner as a queued message; no output means nothing happens.
- A run is skipped if the previous run of the same schedule is still going or still waiting.
- A one-shot missed while the runtime was down runs at startup. Missed interval runs are skipped.
- A closed agent is never nudged, and no new schedules are admitted while it is closed or paused. A scheduled command already admitted is independent of the turn: it can finish, but its output will not reopen a closed owner.

Both run unattended, so pausing must stop nudges and schedules as well as agents.

## Models and MCP

Four model backends are supported, and model choice is a setting rather than something agents reason about.

| Backend | Access |
| --- | --- |
| OpenAI | ChatGPT subscription over OAuth. Port the client from Primer or Cosmurgy. |
| Anthropic | API |
| z.ai | Subscription |
| DeepSeek | API |

- **Who sets model and effort.** An agent can change its own when asked. An orchestrator can change them for its task agents.
- **Defaults and exceptions** live in config and belong to Sera. Agents do not weigh cost or context windows.
- **Rate limits.** When a backend rate-limits a request or a subscription quota runs out, the agent waits and retries. It does not fail or switch models. This is a default Sera can adjust.
- **Policy refusals.** When a provider blocks the request itself rather than failing it — an OpenAI content filter or policy code, a refusal stop reason, Anthropic's content-filtering error — retrying the same model cannot help. Marten moves the agent to `refusalFallbackModel` (default `deepseek-flash`) and appends one runtime message saying what refused it, why, where it is now, and that it can stay there, hand the work to a fresh agent with a fuller brief, or surface the refusal to its orchestrator or Sera if it thinks the block was right. The agent keeps the new model, and may change it again itself, until something changes it back. One fallback per turn: a refusal on the fallback model itself ends the turn as a terminal error. A fallback that is the same model that refused, or whose backend is not connected, is not used and the refusal stays a terminal error.
- **Model HTTP timeouts.** Every backend allows 30 seconds to establish a connection and 120 seconds per request to receive response headers. While reading the body, `modelIdleTimeoutSeconds` (default 300) bounds the wait for the next bytes, not the next SSE event. There is no total stream deadline. Header and body timeouts, stream interruptions and retryable server errors use the five short transient retries, separate from network recovery and quota.
- **ChatGPT authorization.** The OAuth client has a separate 30-second connect deadline and 60-second total deadline for its buffered replies. Turn cancellation also cancels refresh, releasing the refresh gate. Refresh DNS/connect failures use network recovery; total timeouts and retryable OAuth server errors use the short policy. Rejected credentials still require signing in again.
- **Provider network recovery.** Pre-response DNS and connection-establishment failures (including connect timeouts) are identified by the HTTP transport, not error text. The same failed model operation resumes in the same turn without replaying tools. Backoff doubles from a 10-second ceiling to a five-minute ceiling, with each delay independently jittered between half and all of that ceiling. Retry waits give up both agent and model slots. Completion retries also share a process-wide two-second admission interval so simultaneous timers, unpauses, and slot grants do not start all retries together. Use `maxConcurrentModelCalls` to bound concurrent work, including fresh calls; spacing retries is not a concurrency cap.
- **Network wait status.** `waiting_for_network` includes the last connection failure as `statusDetail` (`detail` in the status event) and `retryAt`, the next scheduled attempt time before pause or slot constraints. It is null when no further attempt fits in the window. A recovery episode has a 45-minute elapsed budget from the first connection failure, including pauses, queues and in-flight retries. A pause prevents dispatch, not passage of time. Stop and close cancel the wait without reporting an error; restart drain cancels it and uses normal interrupted-turn recovery. A successful attempt or a different failure exits the network policy.
- **Failed turns.** A terminal error, including a missing model, exhaustion of the five short transient retries, or expiration of provider network recovery, ends the turn without nudging or changing tasks. Marten queues one notice each to the parent and to distinct other agent assigners of unfinished tasks, waking them if idle. Quota waits and an explicit interrupt are not terminal errors. A model setting is not rejected based on an advisory provider catalog.
- **Model calls.** Per agent, the runtime exposes the latest completion attempt, including retries and context compaction: model, kind, start time, end time, and outcome (`succeeded`, `failed`, or `cancelled`). An attempt is in flight when it has no end time. Time spent waiting for quota or backoff is not in flight. This status is process-local, not transcript history.

- **Concurrent work.** The process-wide `maxRunningAgents` limits active turns (model work and tools), and `maxConcurrentModelCalls` limits model completions, including compaction, across providers. These count Marten's active waits: on cancellation a backend that ignores the token may still be running externally, but its wait releases Marten's slot and its late output is fenced. Both default to unlimited (`0`). A turn can hold an agent slot while queued for a model slot, but acquires them in that order; compaction and retries never reacquire a held model slot. Pause and retry backoff give up capacity. Slot queues can be cancelled by interrupting the turn, and the UI and APIs report `waiting_for_slot` with the limit or named slot being waited on. Schedules, notebook replays and detached processes are not agent turns.
- **MCP is stdio only.** No OAuth and no external services.
- **MCP is lazy, per agent.** A server is started once to read and cache its schema. It is launched for real only when an agent first calls one of its tools.

## UI

The UI is how Sera watches and steers the system, so it has to show everything the rest of this spec calls visible or flagged.

**Shows**

- Every message between agents, and messages from orchestrators to Sera.
- Each agent's task list, with blocked tasks and their reasons, in a height-capped panel that scrolls separately from the conversation.
- Each agent's schedules in a separate height-capped, scrollable panel.
- An abandoned runspace, and an agent whose nudges have stopped.
- Background processes, and which agent owns each.
- Each project's memory, with its history and who changed each line.
- A sidebar with individually collapsible project rosters; collapsing does not change the open conversation. The browser remembers the collapsed projects. Provider sign-in and global pause controls are under a normally closed Settings disclosure.

**Lets Sera**

- Chat with any chat agent. Opening a conversation scrolls to its latest entry after history loads. New entries follow the tail only while the reader is near the end.
- Message any agent in either mode.
- Pause an agent, a project or everything. At the next model or tool dispatch the turn parks and yields its agent slot. An already-started model call can finish, but its reply or compaction is held off the transcript until resume, with both slots returned. Deltas while paused are suppressed. An already-started tool can finish; tool results are recorded and the remaining batch resumes in order when all applicable pauses clear, without re-running completed calls. Close cancels a parked turn. Pause also stops nudges and new schedules.
- Explicit notebook replays are operator actions, not agent turns; pause and close do not gate or cancel them.

**Outside agents.** Agents outside Marten, such as Claude Code sessions, get a named client token (`client add NAME`, or the UI). It reaches only the `/api/ext` routes: status, create project, message an agent, read replies, transcripts and tasks. To Marten agents they're peers named `NAME (outside)`, never Sera, and agents answer them with `send_message` to `client:NAME`. Exchanges count toward the loop breaker. If the connection drops, `marten-ext` exits with a short error and exit code 2 instead of silently replaying a command. See docs/outside-agents.md.

**Question cards.** An orchestrator or chat agent can put a structured question to Sera as a card, and she answers on the card. The conversation shows the recorded answer immediately, even if the agent is paused; once delivered as a transcript message, it is not shown twice. Task agents cannot ask Sera directly; they pass questions up to their orchestrator.

## Not day 0

Nothing below is built on day 0. The first list may come later; the second is ruled out.

**Deferred**

- A loop breaker for messaging (built)
- Budgets (built)
- Completion contracts (built)
- A filter on what agents can do, and any review of agent-written tools (built)
- Reproducible, notebook-style PowerShell execution (built)
- Mobile and desktop apps (built as an installable web app: a manifest, a service worker that caches the shell but never the API, and a phone layout with a drawer. A phone reaches Marten through an HTTPS proxy such as `tailscale serve` plus `remoteHosts`.)
- Limited tool schemas, with tool search for the rest (built)
- Durable, versioned storage for `~/.marten`, such as encrypted backups or a git history (built)

**Non-goals**

- Messages between task agents in different projects
- C# hot reloading
- PowerShell state that persists between runs, beyond the state variable
- Marten's own browser integration or computer-use system; existing ones are used instead
- OAuth or remote MCP servers
