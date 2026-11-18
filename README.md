# Marten

A small agent runtime for Sera's agentic coding. See [SPEC.md](SPEC.md).

## Running it (M0)

Needs the .NET 10 SDK and Node 20+.

```sh
(cd web && npm install && npm run build)
dotnet run --project src/Marten
```

Marten prints a URL like `http://127.0.0.1:7450/#token=…`. Open it once; the page keeps the token. Click **Connect ChatGPT** to sign in with your subscription, then start a chat, or create a project (from the sidebar, or by asking a chat agent) and talk to its orchestrator.

State lives in `~/.marten` (override with `MARTEN_HOME`):

| Path | What |
| --- | --- |
| `config.json` | Model, effort, port, timeouts, truncation size |
| `secrets/` | ChatGPT tokens and the UI token (mode 0600) |
| `prompts/` | The Marten-wide prompt and one per agent kind; edit freely |
| `agents/<id>/` | `agent.json`, `system.md` (fixed at spawn), `state.json`, `tasks.json`, `NOTES.md` (task agents), `transcript/*.jsonl`, `outputs/` |
| `projects/<id>/` | `project.json`, `prompt.md`, `MEMORY.md`, `memory-log.jsonl`, `worktrees/` |
| `inbox.jsonl` | Messages agents sent to Sera |
| `prompts/*.md.default` | The shipped default each prompt came from; prompts you haven't edited are upgraded with Marten |

Model API calls allow up to 30 seconds to connect and 120 seconds to receive response headers. Set `"modelIdleTimeoutSeconds": 300` in `config.json` to adjust how long a response can go without delivering bytes (300 seconds by default; nonpositive values use the default). A stream that keeps delivering bytes has no total time limit. ChatGPT OAuth uses a separate client with a 60-second total request limit; stopping a turn also cancels its token refresh.

Agents outside Marten (e.g. Claude Code sessions) can create projects and message agents with a client token: see [docs/outside-agents.md](docs/outside-agents.md) and `scripts/marten-ext`.

The agent header and status APIs (`/api/status`, `/api/ext/status`, and `$Marten.Agents()`) show the start and end of the latest model attempt, including compaction. If a turn fails outright (for example, its model is unavailable or retries run out), Marten wakes the parent and agent assigners of unfinished tasks with a queued error message. It does not change task states.

## As an app (desktop and phone)

Marten's UI is an installable web app.

- **Desktop:** open the printed URL in Chrome or Edge and choose **Install Marten**, or in Safari choose **File → Add to Dock**. It opens in its own window and keeps working across Marten restarts.
- **Phone:** Marten listens only on 127.0.0.1, so reach it through an HTTPS proxy on the Mac. [Tailscale](https://tailscale.com) is the easy one:
  1. On the Mac, run `tailscale serve --bg 7450`. It prints a URL like `https://your-mac.your-tailnet.ts.net`.
  2. Add `"remoteHosts": ["your-mac.your-tailnet.ts.net"]` to `~/.marten/config.json` and restart Marten.
  3. On the phone, which must be on the same tailnet, open that URL. Paste the printed link or the token from `~/.marten/secrets/ui-token` into the box, then use **Add to Home Screen**.

  `remoteHosts` only tells Marten to accept requests carrying that name. The API still needs the token, and nothing is exposed beyond your tailnet.

For UI work, run `npm run dev` in `web/` and set `"devOrigin": "http://localhost:5173"` in `config.json`.

## Tests

```sh
dotnet test
```
