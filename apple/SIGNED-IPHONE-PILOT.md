# Opt-in signed iPhone pilot (scratch only)

**This is a procedure, not a release or a completed device test.** Run only after Sera explicitly coordinates the signing, device, and test-tailnet window. Do not merge the pilot overlay or ship it. The default and regular embedded projects must continue to launch with no enrollment adapter. Nothing here uses the live Marten checkout, home, listener, operator token, tailnet, provider accounts, or trust store. A paired bearer is a full scratch operator credential, not a read-only credential.

## 1. Pin the inputs and build without signing

On an Apple Silicon Mac: Xcode 27, Go 1.27.4 (on `PATH`, `GOTOOLCHAIN=local`), XcodeGen, Python 3, .NET 10 SDK, and Node/npm for the #55 web UI. Use a physical iPhone running iOS 18.1+ supported by the selected Xcode. In a dedicated shell, from a **non-live** git repository containing both PR commits:

```sh
set -eu
umask 077
REPO=$(git rev-parse --show-toplevel)
PILOT=$(mktemp -d "${TMPDIR:-/tmp}/marten-iphone-pilot.XXXXXX")
PILOT=$(cd "$PILOT" && pwd -P)
CLIENT=354a1e2ba8064d93e5469d42784eb10cfb2e255b
SERVER=547eb24f880af713055b19beb756a46587684cc7
LIB=dacde75057ff350e146505829beff446e6823966
mkdir "$PILOT/client" "$PILOT/server"
git -C "$REPO" archive "$CLIENT" apple | tar -x -C "$PILOT/client"
git -C "$REPO" archive "$SERVER" | tar -x -C "$PILOT/server"
git clone https://github.com/tailscale/libtailscale.git "$PILOT/libtailscale"
git -C "$PILOT/libtailscale" checkout --detach "$LIB"
test "$(git -C "$PILOT/libtailscale" rev-parse HEAD)" = "$LIB"
export GOTOOLCHAIN=local
go version
xcodebuild -version
```

Build the pinned framework locally; do not use an unattributed binary. Use the same selected Xcode for the archives, framework slices and app:

```sh
(
  cd "$PILOT/libtailscale"
  make c-archive-ios c-archive-ios-sim c-archive
  cd swift
  build_slice() {
    xcodebuild -project TailscaleKit.xcodeproj -scheme "$1" -configuration Release \
      -destination "$2" -derivedDataPath "$PILOT/sdk-build" CODE_SIGNING_ALLOWED=NO build
  }
  build_slice 'TailscaleKit (iOS)' 'generic/platform=iOS'
  build_slice 'TailscaleKit (Simulator)' 'generic/platform=iOS Simulator'
  build_slice 'TailscaleKit (macOS)' 'platform=macOS,arch=arm64'
)
products="$PILOT/sdk-build/Build/Products"
mkdir -p "$PILOT/client/apple/.local"
xcodebuild -create-xcframework \
  -framework "$products/Release-iphoneos/TailscaleKit.framework" \
  -framework "$products/Release-iphonesimulator/TailscaleKit.framework" \
  -framework "$products/Release/TailscaleKit.framework" \
  -output "$PILOT/client/apple/.local/TailscaleKit.xcframework"
```

## 2. Isolate the app and constrain it to the test origin

Pick a **fresh** bundle identifier within the signing team's namespace (not `dev.marten.native`, not a prior pilot's ID). Pick the exact lowercase non-sensitive scratch server FQDN the test-tailnet owner will provision, with `.ts.net` suffix; no explicit port or path. Set these two **non-secret** shell values before applying the overlay below. The `marten-pilot.example.ts.net` value is an example, not a server to use.

```sh
PILOT_BUNDLE='your.team.namespace.martenpilot.unique'
PILOT_HOST='marten-pilot.example.ts.net'
export PILOT_BUNDLE PILOT_HOST
```

From the same shell, apply this **only to the archive copy**. It injects the real adapter only in an embedded non-Debug iOS build with that exact bundle ID. In addition to the separate iOS sandbox/preferences and default Keychain access group, it uses a pilot-specific Keychain service and node-state directory inside that sandbox. The hardcoded origin is checked at scan/import before confirmation or enrollment, and when selecting a saved entry. The only diagnostics are fixed strings after a decoded WSS refresh hint and on a non-cancelled socket failure; they do not log a URL, header, payload, error or identifier.

```sh
python3 - "$PILOT/client/apple" <<'PY'
import os, re, sys
from pathlib import Path

root = Path(sys.argv[1])
bundle, host = os.environ['PILOT_BUNDLE'], os.environ['PILOT_HOST']
assert re.fullmatch(r'[a-z0-9-]+(?:\.[a-z0-9-]+){2,}', bundle)
assert bundle not in {'dev.marten.native', 'your.team.namespace.martenpilot.unique'}
label = r'[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?'
assert re.fullmatch(rf'{label}\.{label}\.ts\.net', host)
assert host != 'marten-pilot.example.ts.net'

def replace(file, old, new):
    p = root / file
    text = p.read_text()
    assert text.count(old) == 1, file
    p.write_text(text.replace(old, new))

replace('project.yml', 'PRODUCT_BUNDLE_IDENTIFIER: dev.marten.native\n',
        f'PRODUCT_BUNDLE_IDENTIFIER: {bundle}\n')
replace('Info.plist', '<key>CFBundleName</key><string>Marten</string>',
        '<key>CFBundleName</key><string>Marten Pilot</string>\n'
        '    <key>CFBundleDisplayName</key><string>Marten Pilot</string>')
replace('App/MartenApp.swift', '@State private var connections = Connections()', '''@State private var connections = pilotConnections()

    @MainActor private static func pilotConnections() -> Connections {
        #if os(iOS) && MARTEN_EMBEDDED_TAILNET && !DEBUG
        if Bundle.main.bundleIdentifier == "BUNDLE_PLACEHOLDER",
           let base = try? TailnetState.applicationSupport() {
            let state = TailnetState(root: base.root.appending(path: "SignedPilot", directoryHint: .isDirectory))
            return Connections(keychain: CredentialStore(service: "BUNDLE_PLACEHOLDER.endpoint"),
                               enrollment: TailscaleKitEnrollment(state: state))
        }
        #endif
        return Connections()
    }'''.replace('BUNDLE_PLACEHOLDER', bundle))
replace('App/Connections.swift', 'do { pendingOffer = try PairOffer(payload, mode: .embedded, now: clock()) }', '''do {
            let offer = try PairOffer(payload, mode: .embedded, now: clock())
            guard offer.endpoint.origin == "ORIGIN_PLACEHOLDER" else { throw ClientError.origin }
            pendingOffer = offer
        }'''.replace('ORIGIN_PLACEHOLDER', f'https://{host}'))
replace('App/Connections.swift', '''func select(_ endpoint: Endpoint) async {
        guard !busy, endpoints.contains(endpoint) else { return }''', '''func select(_ endpoint: Endpoint) async {
        guard !busy, endpoints.contains(endpoint) else { return }
        guard endpoint.origin == "ORIGIN_PLACEHOLDER" else { show(ClientError.origin); return }'''
        .replace('ORIGIN_PLACEHOLDER', f'https://{host}'))
replace('App/Client.swift', 'import Foundation\n', 'import Foundation\nimport OSLog\n')
replace('App/Client.swift', 'if change.invalidate { continuation.yield(()) }', '''if change.invalidate {
                        Logger(subsystem: "marten.signed-pilot", category: "wss").notice("pilot: WSS hint received")
                        continuation.yield(())
                    }''')
replace('App/Client.swift', '} catch { continuation.finish(throwing: ClientError.network) }', '''} catch {
                if !Task.isCancelled {
                    Logger(subsystem: "marten.signed-pilot", category: "wss").notice("pilot: WSS receive ended")
                }
                continuation.finish(throwing: ClientError.network)
            }''')
PY
xcodegen generate --spec "$PILOT/client/apple/project-embedded.yml"
xcodebuild -project "$PILOT/client/apple/MartenEmbedded.xcodeproj" -scheme Marten-iOS \
  -configuration Release -destination 'generic/platform=iOS' -derivedDataPath "$PILOT/unsigned" \
  CODE_SIGNING_ALLOWED=NO build
```

Stop if the unsigned build fails or if any overlay edit misses. Re-review the overlay before retargeting to another commit. The SDK build flag alone never enables production enrollment. The documented overlay has passed an unsigned iOS Release build with the cached framework from the stated pin and synthetic identifiers; no signing, installation, or enrollment was performed. Do not add ATS overrides, trust anchors, custom certificate delegates, Keychain Sharing, app groups, or VPN entitlements. Do not distribute the pilot through TestFlight or export a reusable signed artifact.

## 3. Prepare the disposable server and test tailnet

Only the coordinated owner may make test-tailnet changes. Use a **separate, dedicated test tailnet** (not a tag in the production tailnet). Turn on MagicDNS and HTTPS Certificates there; the chosen non-sensitive FQDN can appear in public certificate transparency logs and cannot be removed from those logs afterward. Check tag ownership, and allow only the pilot client tag to reach the scratch server tag on `tcp:443` (for example `tag:marten-pilot-client` to `tag:marten-pilot-server`). Remove or narrow any overlapping broad allow within the test tailnet beforehand; rules are additive. No Funnel, Serve, host port forwarding, subnet routes, exit nodes, legacy `remoteHosts`, or `pairingOrigin` overrides. No personal system Tailscale VPN on the phone for the run; if an existing VPN cannot safely be disconnected, use a separate device or stop. The app itself must establish the route.

Build #55 and the web UI in the archive, and create an empty server home. Choose an unused loopback port other than the live service's (`7450`); example below uses `17450`. Check that `lsof -nP -iTCP:17450 -sTCP:LISTEN` lists no listener before starting. Do not stop another process to free a port.

```sh
SCRATCH_HOME="$PILOT/server-home"
mkdir -m 700 "$SCRATCH_HOME" "$SCRATCH_HOME/bin" "$SCRATCH_HOME/secrets" "$PILOT/host-home"
(
  cd "$PILOT/server"
  (cd web && npm ci && npm run build)
  dotnet build src/Marten
  (cd src/Marten.Tsnet && CGO_ENABLED=0 go build -mod=readonly \
    -tags 'ts_omit_logtail ts_omit_oauthkey ts_omit_identityfederation' \
    -trimpath -o "$SCRATCH_HOME/bin/marten-tsnet" .)
)
printf '{"port":17450,"tailnet":{"host":"%s"},"backup":{"intervalMinutes":0}}\n' "$PILOT_HOST" \
  > "$SCRATCH_HOME/config.json"
```

This is the authorization checkpoint: **stop here unless the signing, test-tailnet, certificate disclosure, and key/enrollment window are explicitly approved.** Sera or the designated test-tailnet owner must create the keys in the test admin console during that window. Reserve one short-lived **one-off, non-ephemeral** server device key tagged only as the pilot server, and at least two separate short-lived **one-off** client device keys restricted to the pilot client tag (initial pair and re-pair; mint as needed). Arrange prompt approval or use permitted preapproval if required; the app waits at most 25 seconds for the node to reach Running and the Marten QR expires in five minutes. Do not use a reusable key, API key, OAuth secret, or the server key as the client key.

Place the server key in `$SCRATCH_HOME/secrets/tailnet-auth-key`, `0600`, via a private editor/secret manager that will not leave swap or history copies. Never pass it as a shell argument or environment variable; no terminal tracing. Launch **only the scratch server** in the foreground of the shell where `PILOT` and `SCRATCH_HOME` are set, with no inherited provider credentials (use a trusted executable path). Use another Terminal for the listener check and Xcode:

```sh
DOTNET=$(command -v dotnet)
(
  cd "$PILOT/server"
  exec env -i PATH="$PATH" HOME="$PILOT/host-home" MARTEN_HOME="$SCRATCH_HOME" \
    MARTEN_WEB_ROOT="$PILOT/server/web/dist" "$DOTNET" src/Marten/bin/Debug/net10.0/Marten.dll
)
```

Wait for `Native tailnet transport ready` and the expected authority. Use `lsof` to confirm that Kestrel listens only at `127.0.0.1:17450` and the known scratch process owns it. Verify the server tag and exact DNS name in the test console. Wrong suffix, hostname collision, unexpected listener, invalid certificate, or wrong tag = **stop**; do not override the client checks. Remove the scratch `tailnet-auth-key` file after readiness. Open `http://127.0.0.1:17450/` on the server Mac, in a fresh private browser session with no synchronization, password manager or service-worker carryover. Enter only the scratch home’s `secrets/ui-token` locally by a private workflow; never put it on the phone, in a URL, command, or notes. Under **Settings → Paired devices**, check that pairing is enabled and there are no devices. Use a blank scratch home and at most a disposable empty chat. Do not send it messages, schedule work, configure backends, or attach any live directories.

## 4. Sign, install, and run the gates

**Sera / device owner:** Select the approved Apple development team in Xcode and unlock/connect the iPhone. Personally handle any Apple login/2FA, device registration/provisioning, trust prompts and Developer Mode/reboot that are needed. Open **only** `$PILOT/client/apple/MartenEmbedded.xcodeproj`. In `Marten_iOS` Signing & Capabilities verify automatic **development** signing, the unique pilot bundle ID, and no shared groups. Set the `Marten-iOS` scheme's **Run** build configuration to **Release** (Debug is intentionally off), select the physical phone and Run. Verify that the installed app is separately labeled **Marten Pilot** and shows no saved servers. If the project is regenerated, recheck signing and scheme settings. Use Console on the Mac with the phone selected, filtered to subsystem `marten.signed-pilot` and category `wss`; record only the fixed markers and UTC times. No network dumps, screenshots of pairing, or downloaded app containers.

| Gate | Action and required evidence |
| --- | --- |
| 1. Camera and consent | In the local web UI enter a fresh client auth key and **Show pairing QR**. Use the pilot’s **Scan Pairing QR…**, grant camera permission, and verify the displayed origin matches exactly. Cancel. The saved-server list and server pairing list stay empty, and no app node appears in the test console. Rescan privately before expiry, or discard the offer/key and issue a fresh one. Only **Confirm Pairing** may enroll. If the device has already denied permission, the device owner may enable it for **Marten Pilot** only. |
| 2. Pair, TLS, WSS | Confirm with label `pilot-iphone-a`. Verify one corresponding Marten device and one device with the expected pilot client tag in the test console; read native status and open the empty scratch chat. This must work with ordinary platform TLS and the system Tailscale VPN disconnected. With app active, change global pause once from the scratch web UI and require a new `pilot: WSS hint received` marker, not merely a UI update from polling. No fallbacks, alternate origins or trust changes. Keep the scratch server paused after the check. |
| 3. Foreground and persistence | Background/lock the phone for at least 30 seconds; while inactive change global pause from scratch web UI with no work queued, then return unlocked. Require refreshed status and a new hint after a second web toggle; an old queued marker is not evidence. Repeat across an approved connectivity change (e.g. Wi‑Fi to cellular, if available): fresh reads may briefly fail but must recover without new QR/keys, duplicate server device rows, or new tailnet identity. Force-quit/relaunch while unlocked and require the saved connection and authenticated read without a QR, which exercises stored bearer/node state. This does not prove the stale-listener recovery branch was taken or fault-injected mutation no-replay on hardware. |
| 4. Revoke while connected | Keep phone active and first prove WSS working with a new hint. From the local web UI revoke `pilot-iphone-a` only. Expect a correlated `pilot: WSS receive ended` marker, rejected-session UI on the next authenticated refresh (pull to refresh if needed), no workspace on relaunch and no automatic re-pair. Confirm the server pairing row is gone. The test-tailnet node still exists: do not remove it before this check or network failure can mask bearer rejection. |
| 5. Forget and re-pair | In **Marten Servers** choose **Forget** for the saved pilot origin. Confirm and require the row to disappear; relaunch and verify no reconnect or saved row before scanning a new offer. If cleanup is pending, do not re-pair: quit/reopen and retry Forget once; persistent failure is no-go. Remove the old client node in the test console separately. With a fresh one-off client key and fresh QR, pair as `pilot-iphone-b`; require a newly enrolled test-tailnet node, one new Marten device, authenticated status and a new WSS hint. |

For expected post-pair reads and event delivery, allow up to 60 seconds on a known-good link (or after restoring it); mark a missing or uncorrelated result inconclusive, not a pass. If any gate fails or is inconclusive, record the step, UTC times, redacted outcome and whether teardown succeeded. Never keep trying an uncertain key/code. An enrollment can succeed even if redemption fails; check **both** admin lists and clean up before a fresh attempt. A pass qualifies only the tested scratch configuration for review; it does not authorize enabling pairing in either app root or on the service. A full UI/UX pass and iOS mutation fault injection are separate work.

## 5. Teardown (even on an abort)

1. Hide all QR offers; hiding is not cancellation. In the local scratch web UI revoke every pilot device (including ambiguous pair attempts) and verify the list is empty. On the phone, Forget any saved pilot entry while the pilot app is still installed; do not rely on app deletion to clear Keychain. If local cleanup remains pending, record it as unresolved, revoke/remove remote access regardless, and retain the pilot app only if needed to retry Forget under the owner's direction. Otherwise delete **Marten Pilot** only.
2. In the **test** tailnet revoke pilot keys that remain and remove all pilot client nodes and scratch server node, including orphaned enrollments. Restore only test-tailnet policy/settings changed for the pilot following the owner's plan; verify no pilot devices/keys/grants remain. Revoking the Marten bearer, removing the tailnet node, and Forget are three separate operations.
3. Stop the known foreground scratch server with Ctrl-C in its own Terminal, and verify its process/helper and `17450` listener are gone. Stopping clears unredeemed Marten offers. Close the private bootstrap browser. Delete the exact `$PILOT` private directory after checking its absolute path and that it contains only pilot artifacts. Do not touch another `MARTEN_HOME`, provisioning profiles, shared SDK caches, accounts or trust settings. Report only non-secret versions, gate pass/fail, timestamps and cleanup result. No QR images, tokens, keys, device identifiers, raw logs or app containers in the report.

**Time to reserve:** about **60–90 minutes** with working signing and a prepared test tailnet, plus **30–60 minutes** if SDKs/dependencies are cold or provisioning/device approval needs setup. Sera's hands-on part is roughly **30–45 minutes**: authorize the test window, handle signing and device prompts, coordinate key issuance/approval, drive the camera/lock/relaunch/Forget sequence, and confirm cleanup with the test-tailnet owner. An unsigned build can be staged beforehand; it does not require Apple sign-in or provisioning.
