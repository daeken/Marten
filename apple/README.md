# Marten for iPhone and Mac

This is the first milestone of a native **operator** client, not a read-only/outside-agent client. It has SwiftUI screens on iOS 17+ and macOS 14+ and no embedded web UI. Full web capability parity is the target; see the checklist below for what is missing.

## Build and run

Open `apple/Marten.xcodeproj` in Xcode (verified with Xcode 27). Select **Marten-iOS** for an iPhone simulator or **Marten-macOS** for My Mac. The checked-in project is generated from `apple/project.yml`; after adding or removing files, use `xcodegen generate --spec apple/project.yml` from the repository root. Running or testing an existing checkout doesn't require XcodeGen.

```sh
xcrun simctl list devices available
xcodebuild -project apple/Marten.xcodeproj -scheme Marten-macOS -destination 'platform=macOS' test
xcodebuild -project apple/Marten.xcodeproj -scheme Marten-iOS \
  -destination 'platform=iOS Simulator,name=iPhone 17' test
```

Install a simulator runtime from Xcode > Settings > Components if the destination list is empty (or `xcodebuild -downloadPlatform iOS`). For physical iPhone development:

1. Connect the unlocked phone to the Mac, accept the trust prompts, and enable Developer Mode on the phone if Xcode requests it.
2. In Xcode > Settings > Accounts, select your own Apple ID/team. In **Marten_iOS > Signing & Capabilities**, use automatic signing with your development team. If necessary, set a unique bundle ID locally. Do not commit team IDs, personal bundle IDs, provisioning files, or signing changes.
3. Select the phone and run the **Marten-iOS** scheme. This verifies signing and launch; new-device pairing is not available in this build.

## Connect and use

- **New device pairing is disabled.** The old credential-bearing URL scheme, scanner/import entry points for that format, and redemption code were removed. There is no production manual-connect path. Use Debug loopback to exercise the MVP until the in-app credential QR and app-owned Tailscale enrollment are available together.
- Saved servers reconnect with their own Keychain credential. iOS uses a device-only item available when unlocked; macOS uses the local login Keychain and may ask for access, notably after rebuilding an ad-hoc app. Preferences store only server origins and the current selection. **Disconnect** retains the credential; **Forget** deletes it locally and is not server-side revocation.
- Select a project filter or view all open agents; tap an agent for its transcript and composer. Use **Load Older Entries** above the transcript, **Latest** beside delivery mode, and **Agent Actions** for refresh, pause/resume, and interrupt. Status and latest transcript update when the app is active (8s and 4s polling after each response); no background fetch or streaming yet. A failed send retains the draft and is not retried automatically; check the transcript before resending if the connection dropped.
- Debug builds also have **Connect to Loopback** for a scratch server only, with an HTTP origin on `127.0.0.1` (`localhost` is normalized to that literal) and a scratch UI token. Use a separate `MARTEN_HOME` and port, e.g. set `{"port":17450}` in its `config.json`; never paste a production token there. This connects to the Mac from its simulator, but on a physical phone loopback is the phone itself. The Release app has no manual credential entry and won't load saved HTTP endpoints.

The production endpoint validator accepts only the exact lowercase `https://marten.<tailnet>.ts.net` origin, on implicit port 443 and without a trailing slash. The HTTP client uses an ephemeral URLSession with platform TLS validation, no redirects (even same-origin), and no credential in request URLs. It doesn't display request/response dumps. `Transport` is injectable. An embedded Tailscale transport is not wired in yet and must not fall back to the ordinary system route if its proxy fails.

## Web parity checklist

Inventory from `web/src/App.tsx`, with routes in `web/src/api.ts` (baseline `3265865`). Parity means matching the actions and outcomes available to an operator, not cloning the layout or reusing React. The paired credential is intended to have full operator authority; missing controls below are product gaps, not role restrictions. Server-admin and destructive tests should run only against isolated scratch data.

| Web surface | Native status / acceptance target |
| --- | --- |
| Token gate / authentication and device management | **Partial:** Keychain per origin, switch/disconnect/forget, Debug-only loopback. Add the final in-app credential QR, origin confirmation, app-owned Tailscale enrollment, then one-time Marten redemption; persist only node state and the per-device bearer. Add list/invite/revoke through `/api/pair` management. Keep node enrollment separate from backend account login. Test invalid/expired/used invitations, revocation and TLS failures without exposing credentials. |
| Sidebar Projects, Chats, `NewProject` | **Partial:** project filter and active-agent list. Add project groups with collapsible membership, bindings and pause badges; project creation (name, bindings, prompt, worktree default, brief), select new orchestrator; create chat and show/hide closed chats. |
| `AgentRow` / `AgentView` controls | **Partial:** agent status, send queued / immediate, interrupt, pause/resume and inherited-pause indicator. Add close/reopen, confirmed fresh context (preserves transcript and obeys guards), per-agent model & effort/defaults, parent/worktree links, pending/task counts, retry reason/time, model-call timing, and stopped-nudge/abandoned-runspace diagnostics. |
| `AgentView` transcript and `EntryView` | **Partial:** latest and older pages, basic text and disclosure rows, explicit latest jump. Add stable latest-follow and older/newer/archive navigation, streaming/model/tool activity and reconnect/reconcile, full Markdown/code, tool call/result association, authenticated images, usage/model details, and inline question cards. Keep Bearer auth out of asset/realtime URLs wherever the server contract permits. |
| `InboxView`, `QuestionCard`, `MessagesView` | **Gap:** Sera inbox, unread/question indicators, project/agent navigation, question options/text/notes and answered cards, plus agent-to-agent messages with mode/reason and source/target navigation. |
| `AgentView` tasks, schedules and processes | **Gap:** view task dependency links, completion checks/output, schedule timing/command/last result and remove schedule, view per-agent/global background processes, logs and stop process tree. |
| `NotebookView` | **Gap:** list cells with command, cwd, input/output `$State`, output, status and timing; export `.ipynb` through native Files/share. Rerun a chosen cell on the server and show output/state comparisons and failures. Make it clear that rerun really executes and can write files, without replacing the agent's live `$State`. |
| `MemoryView` and approvals | **Gap:** project memory, prompt and attributed edit history/diffs; list agent-written tools (including scope, path, source, broken/review state) and approve the reviewed version using the server action. Refresh after approval; handle stale/conflict/error states without blind retries. |
| `Sidebar` Settings / `BackendRow` | **Gap:** pause/resume all, project pause/resume, account status/email/signing-in/error, connect/reconnect ChatGPT and sign out. Use system browser/authentication UI for provider login with server completion/status; design and verify remote callback behavior without changing account/signing enrollment in this MVP. Show provider connection/key source/spend/budget; add/change/remove API keys using secure non-persisted fields. `setConfig` exists in the wrapper but there is no global model/effort editor in this baseline's web UI. |
| `ProcessesView` administration | **Gap:** global process inspection/log/stop (above), MCP server config/status/tool list, failures and running-agent/PID links, reload MCP; outside-agent list, unread, token-file path, add/remove (not Apple app pairing). |
| `BackupsPanel` in `ProcessesView` | **Gap:** show latest snapshot, size, count, dir, automatic interval and encryption state; run backup now; set/change/remove encryption passphrase via a secure non-persisted field. Restore is CLI-only in the baseline, not a web parity requirement. |

Suggested order: (1) daily operation — stream/reconnect, inbox/questions, agent settings and lifecycle, create/navigation; (2) projects and observability — memory/approvals, tasks/schedules, processes, notebook rerun/export; (3) settings/admin — provider sign-in/logout and keys, pause scopes, MCP/outside clients, backups. At each milestone exercise the same operation as web on a scratch server, test failure and destructive confirmations, and check iPhone layout/VoiceOver and macOS keyboard navigation. End-to-end HTTPS redeem and camera/Tailscale checks on a physical iPhone are separate from unit and simulator tests.
