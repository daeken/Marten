# Marten

A small agent runtime for Sera's agentic coding. See [SPEC.md](SPEC.md).

## Running it (M0)

Needs the .NET 10 SDK and Node 20+.

```sh
(cd web && npm ci && npm run build)
dotnet run --project src/Marten
```

Marten prints a URL like `http://127.0.0.1:7450/` without the UI token, even when output goes to a terminal. On first use, open the URL and paste the token from `~/.marten/secrets/ui-token` (or `secrets/ui-token` under `MARTEN_HOME`) into the page. It remembers the token for that origin. Older startup logs may still contain the token; protect those logs and treat the old token as exposed if anyone else had access. Click **Connect ChatGPT** to sign in with your subscription, then start a chat, or create a project (from the sidebar, or by asking a chat agent) and talk to its orchestrator.

State lives in `~/.marten` (override with `MARTEN_HOME`):

| Path | What |
| --- | --- |
| `config.json` | Model, effort, port, timeouts, truncation size |
| `secrets/` | ChatGPT tokens and the UI token (mode 0600) |
| `prompts/` | The Marten-wide prompt and one per agent kind; edit freely |
| `agents/<id>/` | `agent.json`, `system.md` (fixed at spawn), `state.json`, `tasks.json`, `NOTES.md` (task agents), `transcript/*.jsonl`, `outputs/` |
| `projects/<id>/` | `project.json`, `prompt.md`, `MEMORY.md`, `memory/*.md`, `memory-log.jsonl`, `worktrees/` |
| `inbox.jsonl` | Messages agents sent to Sera |
| `prompts/*.md.default` | The shipped default each prompt came from; prompts you haven't edited are upgraded with Marten |

Model API calls allow up to 30 seconds to connect and 120 seconds to receive response headers. Set `"modelIdleTimeoutSeconds": 300` in `config.json` to adjust how long a response can go without delivering bytes (300 seconds by default; nonpositive values use the default). A stream that keeps delivering bytes has no total time limit. ChatGPT OAuth uses a separate client with a 60-second total request limit; stopping a turn also cancels its token refresh.

Agents outside Marten (e.g. Claude Code sessions) can create projects and message agents with a client token: see [docs/outside-agents.md](docs/outside-agents.md) and `scripts/marten-ext`.

The agent header and status APIs (`/api/status`, `/api/ext/status`, and `$Marten.Agents()`) show the start and end of the latest model attempt, including compaction. If a turn fails outright (for example, its model is unavailable or retries run out), Marten wakes the parent and agent assigners of unfinished tasks with a queued error message. It does not change task states.

## Resource limits

Set process-wide limits in `~/.marten/config.json` (read at startup):

```json
{
  "maxConcurrentModelCalls": 3,
  "maxRunningAgents": 5,
  "slots": { "build": 1, "integration-tests": 2 }
}
```

Omit either `max…` setting or set it to `0` for no limit. Turns yield their agent slot while paused or backing off after a model error. The UI and status APIs show **Waiting for slot** and which limit the agent hit; **Stop** interrupts a turn even while it is queued.

Named slots are opt-in and shared by all agents. In a PowerShell command, run `$Marten.WithSlot('build', { dotnet build })`; `$Marten.Slots` lists the configured names. A slot must have positive capacity. The wait counts toward the command timeout. Wrap only foreground work, and don't nest slots or wait for another agent while holding one.

## Stopping and restarting

Use Ctrl-C or send SIGTERM and wait for Marten to exit before starting it again. Marten stops new model calls, gives active tools `stopGraceSeconds` (5 seconds by default), then cancels unfinished tools and cleans up tracked foreground children. Detached `process_start` jobs survive a Marten restart, not a machine reboot.

On startup, Marten tells existing agents that it restarted and records interruptions it can infer from disk. It does not automatically repeat interrupted calls. A forced kill or power loss skips cleanup; results or queued messages can be lost even when a command changed files or external state. Check the transcript, logs, and actual state before retrying.

## Linked project memory

Keep standing rules and short pointers in `MEMORY.md` (16 KiB by default). Link longer notes as `[Topic](memory/topic.md)`. Agents see the index and topic filenames, not topic contents; they use `read_file` when they need the details. Both the index and topics use `edit_file` with the same audit history and protections for cited entries. Read an empty index before initializing it with one `replace` using `"anchor": ""`; use `create` for a missing topic file. See [Project memory](SPEC.md#project-memory).

## As an app (desktop and phone)

Marten's UI is an installable web app. Opening a conversation jumps to its latest entry. Scroll back to read older messages without being pulled to the end by new ones. Question-card answers appear in the conversation as soon as they’re saved, even if the agent is paused.

- **Desktop:** open the printed URL in Chrome or Edge and choose **Install Marten**, or in Safari choose **File → Add to Dock**. It opens in its own window and keeps working across Marten restarts.
- **Phone:** Marten listens only on 127.0.0.1, so reach it through an HTTPS proxy on the Mac. [Tailscale](https://tailscale.com) is the easy one:
  1. On the Mac, run `tailscale serve --bg 7450`. It prints a URL like `https://your-mac.your-tailnet.ts.net`.
  2. Add `"remoteHosts": ["your-mac.your-tailnet.ts.net"]` to `~/.marten/config.json` and restart Marten.
  3. On the phone, which must be on the same tailnet, open that URL. Paste the token from `~/.marten/secrets/ui-token` into the box, then use **Add to Home Screen**.

  `remoteHosts` only tells Marten to accept requests carrying that name. The API still needs the token, and nothing is exposed beyond your tailnet.

For UI work, run `npm run dev` in `web/` and set `"devOrigin": "http://localhost:5173"` in `config.json`.

## Tests

```sh
dotnet test
```
