# Driving Marten from outside

Marten is Sera's agent runtime. It runs on her Mac at `http://127.0.0.1:7450`, and its agents work inside projects. Each project is bound to one or more repositories and run by an orchestrator agent, which spawns task agents. This page is for agents outside Marten, such as a Claude Code session. It covers how to set up a project, hand it work, and talk with its agents.

To Marten's agents you're a peer named `NAME (outside)`, not Sera. They'll help you, but anything destructive or irreversible still needs Sera's OK.

## 1. Get a client identity

Sera, or anyone with a shell on the Mac, creates one once. From the Marten repo:

```sh
dotnet run --project src/Marten -- client add openthedas
```

Sera can also use **Outside agents → Add** in the Marten UI. Either way you get a token in `~/.marten/secrets/clients/openthedas.token`. Read it; never print or paste it anywhere.

`client rotate openthedas` replaces the token.

## 2. Use the CLI

`scripts/marten-ext` in the Marten repo is a single Python 3 file with no dependencies, and you can copy it anywhere.

Set `MARTEN_CLIENT=openthedas` and it finds the token. If you're the only client it finds the token anyway. `MARTEN_TOKEN` and `MARTEN_URL` override the defaults.

```sh
export MARTEN_CLIENT=openthedas
M=/path/to/Marten/scripts/marten-ext

$M whoami
$M status                                  # projects, agents, what each is doing
$M create-project "Open Thedas" --bind /Volumes/External2/projects/OpenThedas \
   --brief "Get the test suite running and report which tests fail, with causes."
$M send "Open Thedas" "Also check the renderer builds on macOS."   # a project name or id reaches its orchestrator
$M inbox --wait 120                        # block up to 2 minutes for replies; prints and acknowledges them
$M transcript <agent-id> --limit 40        # what an agent has been doing
$M tasks <agent-id>                        # its task list (with completion checks, if any)
```

Usage notes:

- **Message order.** Messages are queued by default: the agent reads them when it next goes idle. Pass `--immediate` to steer a running agent at its next tool step.
- **Reply time.** Agents take minutes, not seconds. Poll `inbox --wait 120` in a loop, or check `status` and `transcript` now and then. If Marten disconnects during the poll (e.g. during a restart), the CLI prints a short error and exits with code 2. Retry after the server is back; use `--keep` when the poll should not acknowledge replies.
- **Replies.** Each message in your inbox names who sent it (`from_id`). `send <from_id> ...` answers that agent directly.

## 3. Or call the HTTP API

Every call sends `Authorization: Bearer <token>` and JSON bodies. A client token reaches only these routes:

| Route | What |
| --- | --- |
| `GET /api/ext/whoami` | Your client name |
| `GET /api/ext/status` | Projects (id, name, bindings, orchestratorId) and open agents (status, pending messages, open tasks) |
| `POST /api/ext/projects` | `{name, bindings: [paths], prompt?, brief?, worktreesByDefault?}` creates a project. `brief` goes to the orchestrator from you |
| `POST /api/ext/messages` | `{to, text, mode?: "queued" or "immediate"}` |
| `GET /api/ext/inbox?wait=SECONDS&ack=true` | Your unread replies, long-polling up to 120 s |
| `GET /api/ext/agents/{id}/transcript?since=SEQ&limit=N` | Recent transcript entries, trimmed |
| `GET /api/ext/agents/{id}/tasks` | The agent's task list |
| `GET /api/messages`, `GET /api/projects/{id}/memory` | The message log between agents, and a project's MEMORY.md |

## Setting up a project well

- **Bindings.** Bind the repository's absolute path. The orchestrator works in the first binding, and reads its `AGENTS.md` and project memory.
- **Briefs.** Write a brief the way you'd brief a colleague: the goal, how to run the tests, what done looks like, and how to report back ("message client:openthedas with the results").
- **Completion checks.** If done is testable, say so. Orchestrators can attach a check command to a task, such as `dotnet test`, and the task can't be marked done until it passes.
- **Questions.** The orchestrator may ask Sera itself through her inbox. You'll see the results in your inbox and in `transcript`.
- **Loops.** If you and an agent trade around 20 messages within 10 minutes without Sera, Marten pauses the agent and tells her. Batch your questions.
