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
| `agents/<id>/` | `agent.json`, `system.md` (current generated system prompt), `state.json`, `tasks.json`, `NOTES.md` (task agents), `transcript/*.jsonl`, `outputs/` |
| `projects/<id>/` | `project.json`, `prompt.md`, `MEMORY.md`, `memory/*.md`, `memory-log.jsonl`, `worktrees/` |
| `inbox.jsonl` | Messages agents sent to Sera |
| `prompts/*.md.default` | The shipped default each prompt came from; prompts you haven't edited are upgraded with Marten |

Edit `prompts/marten.md`, `prompts/<kind>.md`, or `projects/<id>/prompt.md` to change an existing agent's instructions. Changes are picked up when the agent next wakes for a turn, or when its prompt is rebuilt during compaction; no restart is needed. Clearing or removing a source removes its rules from the rebuilt prompt. Startup restores missing bundled prompts, so leave a bundled file empty if you want to keep it disabled across restarts. Agents without a saved prompt baseline rebuild once on their next turn, even when no source text remains.

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

## Provider network recovery

If DNS or connection establishment fails, the agent shows **Waiting for network** with the reason and next retry time. The turn releases its slots while waiting and retries the failed model operation without rerunning tools. Retries use jittered exponential backoff (up to five minutes) and are staggered across agents. A continuous outage can wait up to 45 minutes before the turn reports an error to its parent. Pause holds dispatch; Stop, Close, and shutdown cancel the wait. Header/stream timeouts, server errors, and quota still have their separate retry policies.

For failure triage, read `agents/<id>/model-attempt-failure.json` in the Marten home. This redacted file keeps just the latest observed failed completion attempt across restarts, even if a retry later succeeded. It includes timing, a run and attempt ID, an observed backend and allowlisted model when known, cancellation and shutdown flags, and allowlisted exception categories. The HTTP phase is included only when known; its absence does not imply a transport failure. No prompts, credentials, or exception messages are included in this file.

## Policy refusals

Sometimes a provider blocks the request itself instead of failing it: OpenAI reports a content filter or policy code, or the model answers with a refusal, and Anthropic reports a refusal stop reason or its content-filtering error. Retrying the same model cannot help, so Marten moves the agent to `refusalFallbackModel` from `config.json` (default `deepseek-flash`) and leaves a note in its transcript: what refused it, why, which model it is on now, and that it can stay there, hand the work to a fresh agent with a fuller brief, or surface the refusal if it thinks the block was right. The switch sticks until something changes it, including the agent itself with `set_model`. One fallback per turn; a refusal on the fallback itself ends the turn as a normal error. Set `"refusalFallbackModel": null` to keep the old behaviour, where a refusal is just a failed turn.

## Pausing and closing agents

**Pause** holds the turn at its next model or tool dispatch. A model reply or compaction that finishes while paused is held off the transcript until resume, without keeping either slot; streamed text is suppressed while paused. A tool that already started can finish and its result is kept. The rest of the batch continues when the agent, project, and global pauses have all cleared.

**Close** (including `close_agent`) cancels the current turn and its slot waits. Late model output and remaining tools are dropped, even if the agent is reopened immediately. A new message from its orchestrator or Sera reopens it for a fresh turn; explicitly reopening can also wake queued messages. Neither replays the interrupted turn. Close cannot undo tool side effects that already happened. Scheduled runs already admitted may finish without reopening their owner; explicit notebook replays remain operator actions.

## Stopping and restarting

Use Ctrl-C or send SIGTERM and wait for Marten to exit before starting it again. The UI's event WebSockets disconnect immediately; an open UI doesn't delay a restart. Marten stops new model calls, gives active tools `stopGraceSeconds` (5 by default), then cancels unfinished tools and makes bounded attempts to clean up tracked foreground children and MCP servers. Detached `process_start` jobs survive a Marten restart, not a machine reboot.

Look for `Marten shutdown +…ms` in stderr to see which phase is still running. These lines contain elapsed times and phase names, not tool or exception text. A shutdown phase that fails or exceeds its wait budget is reported as incomplete and yields a nonzero exit code. These are bounds on the normal managed waits, not a guarantee that every SIGTERM reaches process exit. The runtime has no self-kill watchdog; if shutdown wedges, the deploy helper handles the timeout separately.

On startup, Marten tells existing agents that it restarted and records interruptions it can infer from disk. It does not automatically repeat interrupted calls. A forced kill or power loss skips cleanup; results or queued messages can be lost even when a command changed files or external state. Check the transcript, logs, and actual state before retrying.

For a planned single-host macOS release on port 7450, use the [detached cutover runbook](docs/release-cutover.md).

## Linked project memory

Keep standing rules and short pointers in `MEMORY.md` (16 KiB by default). Link longer notes as `[Topic](memory/topic.md)`. Agents see the index and topic filenames, not topic contents; they use `read_file` when they need the details. Both the index and topics use `edit_file` with the same audit history and protections for cited entries. Read an empty index before initializing it with one `replace` using `"anchor": ""`; use `create` for a missing topic file. See [Project memory](SPEC.md#project-memory).

## As an app (desktop and phone)

Marten's UI is an installable web app. Opening a conversation shows the latest 80 transcript entries. Scroll back without being pulled to the end by new replies. Use **Older** and **Newer** to read one page at a time, or **Return to latest** to follow live replies. Paging changes only the display, not the agent’s history or context. Question-card answers appear in the conversation as soon as they’re saved, even if the agent is paused. Long task, process, and schedule lists scroll separately from the conversation. Click a project name to collapse or expand its agent list without leaving the current conversation. This preference is saved in the browser. Provider sign-in and global pause controls are in **Settings** at the top of the sidebar.

Expand **Agent Tasks** in a conversation header to see open, waiting, and blocked work. The header counts active and completed tasks; expand **Done** inside it to see completed tasks. Both disclosures start closed.

Detached jobs started with `process_start` appear under **Running processes** in their agent's header and on the sidebar's **Background processes** screen. Running rows disappear automatically when the process stops or exits; expand **Process history** to find those rows and read their saved logs. Both process disclosures start closed. Nothing is deleted when a task completes or a process exits.

Use **Start fresh context** in the agent header and confirm to start over between turns. The next turn uses current instructions and project memory without earlier messages, tool results, or compaction summaries. The transcript keeps a visible divider and all earlier pages. The agent’s identity, children, tasks, schedules, and queued input are unchanged; the action itself does not wake it. Resume any agent, project, or global pause first, and wait for any active turn to finish or stop it.

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

## Amazon Bedrock

Select `bedrock/<model-id>` for Bedrock Runtime or `mantle/<model-id>` for Bedrock Mantle. Check the AWS [model card](https://docs.aws.amazon.com/bedrock/latest/userguide/model-cards.html) for Responses API support, region, model or inference profile ID, and path prefix. Support on one endpoint does not imply support on the other.

Set `AWS_REGION` (or `AWS_DEFAULT_REGION`), or select an AWS profile with a region using `AWS_PROFILE`. Marten signs requests using the AWS SDK credential chain; do not put AWS credentials in `config.json`. Your identity needs the [inference permissions](https://docs.aws.amazon.com/bedrock/latest/userguide/inference-prereq.html) for your endpoint and model.

- **Runtime:** Add unprefixed model or inference profile IDs to the `models.bedrock` array in `config.json`. Runtime does not implement OpenAI-compatible model listing. Requests use `/openai/v1/responses`.
- **Mantle:** Marten lists models with `GET /v1/models` unless you provide a `models.mantle` array of IDs. Requests use `/v1/responses` by default. If a model card specifies the `/openai/v1` prefix on Mantle, include its unprefixed model ID in `MARTEN_MANTLE_OPENAI_ROUTE_MODELS` (comma-separated; read at startup). Configuring an ID does not grant access.

Marten streams responses with `store` set to `false`. A local 32,000,000-byte budget on the serialized request triggers context compaction before transport; it is not an AWS service quota.

To diagnose reasoning settings, set `MARTEN_TRACE_BEDROCK_EFFORT=1` before starting Marten. The opt-in trace writes to stderr. It compares the selected effort with the effort parsed from the serialized request, then reports the completed response’s effort and reasoning token count when available. A per-call identifier joins the two records. Unrecognized effort labels are masked; model IDs, agent IDs, prompts, tool data, and credentials are not logged.

To check the configuration with one billable request, for example using [gpt-oss-120b on Mantle](https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-openai-gpt-oss-120b.html):

```sh
dotnet run --project src/Marten -- bedrock-check mantle/openai.gpt-oss-120b
```

AWS reference: [Responses API endpoints, routing, and model discovery](https://docs.aws.amazon.com/bedrock/latest/userguide/inference-responses-api.html).
