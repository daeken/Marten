# Marten

A small agent runtime for Sera's agentic coding. See [SPEC.md](SPEC.md).

## Running it (M0)

Needs the .NET 10 SDK and Node 20+.

```sh
(cd web && npm install && npm run build)
dotnet run --project src/Marten
```

Marten prints a URL like `http://127.0.0.1:7450/#token=…`. Open it once; the page keeps the token. Click **Connect ChatGPT** to sign in with your subscription, then start a chat.

State lives in `~/.marten` (override with `MARTEN_HOME`):

| Path | What |
| --- | --- |
| `config.json` | Model, effort, port, timeouts, truncation size |
| `secrets/` | ChatGPT tokens and the UI token (mode 0600) |
| `prompts/` | The Marten-wide prompt and one per agent kind; edit freely |
| `agents/<id>/` | `agent.json`, `system.md` (fixed at spawn), `state.json`, `transcript/*.jsonl`, `outputs/` |

For UI work, run `npm run dev` in `web/` and set `"devOrigin": "http://localhost:5173"` in `config.json`.

## Tests

```sh
dotnet test
```
