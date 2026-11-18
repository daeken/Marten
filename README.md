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

Agents outside Marten (e.g. Claude Code sessions) can create projects and message agents with a client token: see [docs/outside-agents.md](docs/outside-agents.md) and `scripts/marten-ext`.

For UI work, run `npm run dev` in `web/` and set `"devOrigin": "http://localhost:5173"` in `config.json`.

## Tests

```sh
dotnet test
```
