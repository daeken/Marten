# Marten v0 spec

Last updated 2026-11-18.

## Stance

Marten is a small agent runtime that replaces all of Sera's agentic coding. It is greenfield and stays tiny: build what is needed now, not what agents might need later.

- **Wide open by default.** Agents are trusted: no sandbox, and with no `rules` and `reviewTools` off, no approvals. Sera's filter is opt-in. `rules` in config.json is an ordered list of `{tool, match, agents, projects, action, reason}`. `tool` is a name with `*` wildcards; `match` is a regex tried against the call's string arguments, such as a PowerShell command. `action` is `allow`, `deny` or `ask`, and the first matching rule wins. `ask` puts an Allow once / Deny card in Sera's inbox and fails the call; after she allows it, the identical call goes through once. The filter is a guardrail against mistakes, not a sandbox: it sees tool calls, not what a script does once running.
- **Not reproducible, on purpose.** Agents may mutate process state. Reproducibility is a non-goal for now.
- **Restart to change the core.** New C# tools are compiled in. There is no C# hot reloading. A restart resumes agents where they were.
- **What a turn is.** For steering, a turn is one model call: immediate messages land between model calls, at a tool boundary. Everywhere else, a turn runs from a wake until the agent goes idle. A tool call that was running when Marten stopped is reported to the agent as interrupted on restart, and is never re-run.

## Shape and storage

Marten is a C# monolith with a separate web frontend, and all of its state lives in `~/.marten`.

- **Backend:** a single C# process.
- **Exposure:** the backend listens on localhost only. Every request carries a random token, and the Host and Origin headers are checked, so a web page Sera visits cannot drive an agent. The backend serves the built frontend, so the UI is same-origin.
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
- **System prompt.** The Marten-wide prompt comes first, then a short section for the agent's kind (chat, orchestrator, task), then the project prompt.
- **Shared tree by default.** Worktrees are supported as a per-spawn option, and the default is a project setting. Orchestrator rule: concurrent writers get worktrees.
- **Finished task agents stay around.** Once one is marked closed (term not final), only its orchestrator can wake it, and can do so at any time.

## Project memory

Each project has one shared memory that every agent in it reads, and it survives every compaction. It holds what an agent would otherwise have to ask Sera again.

- **An index and optional topics.** `MEMORY.md` in the project's directory in `~/.marten` holds standing rules and pointers. Link details as `[Topic](memory/topic.md)` in the index. Topics are direct Markdown files in the sibling `memory/` directory, not nested files or symlinks.
- **Index in context, topic bodies on demand.** At spawn and compaction, agents get the index and a sorted list of topic paths. The runtime never injects topic bodies; use `read_file` on the relevant file for its latest contents.
- **A size cap on the index.** The `memoryCapBytes` setting defaults to 16 KiB. An edit cannot grow the index beyond the cap; prune or move details to topics. Topic files do not count toward the index cap.
- **Edited with the atomic file editor.** Use `edit_file` on either the index or a topic. If the file changed since the agent read it, re-read and retry. Use `create` for a missing file. To initialize a zero-byte file such as a new `MEMORY.md`, read it first, then pass a single `replace` with an empty string anchor.
- **Fresh without invalidating the cached prefix.** At the start of a turn agents see the index's changed lines and, if the inventory changed, the current topic paths. Existing agents get the topic list at their first turn after upgrade, without rewriting saved prompts. Topic edits do not inject diffs into agent context: re-read the topic when needed.
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

- **Append-only log.** Each agent's transcript is an append-only log. What the model sees is computed from it, so compaction and elision destroy nothing.
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
  - **Waiting** is on another agent or a schedule. It is not nudged and does not reach Sera.
  - **Blocked** is on Sera.
- An agent that goes idle with an open task is nudged awake. To stop on purpose, it marks the task waiting, blocked or done.
- After three nudges in a row with no task changing state, nudging stops and the agent is flagged in the UI.
- When a task turns done, waiting or blocked, the agent that assigned it is woken.
- **Completion contracts.** Whoever assigns a task may attach a check: a bash command run in the owner's working directory (timeout `checkTimeoutSeconds`, 900). Marking the task done runs it; unless it exits 0 the task stays as it was and the agent gets the output. The UI shows each check and its last result. Only the assigner sets the check.
- An orchestrator with a blocked task tells Sera by sending her a message.

**Schedules**

- A schedule is a PowerShell command plus a one-shot time or an interval. The agent that creates it owns it.
- The command runs without the model. Output is delivered to the owner as a queued message; no output means nothing happens.
- A run is skipped if the previous run of the same schedule is still going or still waiting.
- A one-shot missed while the runtime was down runs at startup. Missed interval runs are skipped.
- A closed agent is never nudged, and its schedules are suspended. Only its orchestrator wakes it.

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
- **Rate limits.** When a backend refuses a request or a subscription quota runs out, the agent waits and retries. It does not fail or switch models. This is a default Sera can adjust.
- **MCP is stdio only.** No OAuth and no external services.
- **MCP is lazy, per agent.** A server is started once to read and cache its schema. It is launched for real only when an agent first calls one of its tools.

## UI

The UI is how Sera watches and steers the system, so it has to show everything the rest of this spec calls visible or flagged.

**Shows**

- Every message between agents, and messages from orchestrators to Sera.
- Each agent's task list, with blocked tasks and their reasons.
- An abandoned runspace, and an agent whose nudges have stopped.
- Background processes, and which agent owns each.
- Each project's memory, with its history and who changed each line.

**Lets Sera**

- Chat with any chat agent.
- Message any agent in either mode.
- Pause an agent, a project or everything. Pausing also stops nudges and schedules.

**Outside agents.** Agents outside Marten, such as Claude Code sessions, get a named client token (`client add NAME`, or the UI). It reaches only the `/api/ext` routes: status, create project, message an agent, read replies, transcripts and tasks. To Marten agents they're peers named `NAME (outside)`, never Sera, and agents answer them with `send_message` to `client:NAME`. Exchanges count toward the loop breaker. See docs/outside-agents.md.

**Question cards.** An orchestrator or chat agent can put a structured question to Sera as a card, and she answers on the card. Task agents cannot ask Sera directly; they pass questions up to their orchestrator.

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
