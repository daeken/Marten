# Embedded tailnet transport for native Marten

This optional transport runs a small Go helper supervised by the Marten server. It joins your tailnet as a **separate device** using Tailscale's userspace `tsnet` SDK. The Marten host does not need the Tailscale app, a TUN device, a system VPN, or admin privileges. ASP.NET still listens only on `127.0.0.1`. The helper listens only on the tailnet, on TCP 443; it does not register a Funnel, advertise routes, or expose a browser UI. This is separate from the legacy, explicitly configured `remoteHosts` proxy setup; do not add the embedded node name to `remoteHosts`.

The native app reaches `https://<device>.<tailnet>.ts.net` and `wss://` on the same authority. Its device must have working tailnet connectivity and permission to connect to the Marten node. TLS and tailnet membership are not substitutes for Marten authorization. Native API access also requires the paired-device credentials (bootstrap is initiated locally); the UI token and outside-client tokens are not native credentials. Only explicitly tagged native API endpoints are reachable through this transport. The backend selects a route once, then enforces the same explicit route policy for paired authority and native-host admission. There is no fallback to legacy or outside credentials, even when the configured tailnet host is also listed in `remoteHosts`.

## Requirements and setup

- Server: macOS or Linux, with normal outbound Tailscale control/relay access. Windows is not supported by the supervisor. The helper pins Tailscale `tsnet` **v1.106.1** and **Go 1.27.4** (or a compatible newer Go toolchain) for builds. Tested on macOS arm64; Linux amd64 was cross-built. Marten itself still needs its usual .NET runtime.
- In Tailscale DNS settings, enable **MagicDNS** and **HTTPS Certificates**. Tailscale gets and renews a publicly trusted certificate for the exact device FQDN; this name is published in certificate transparency logs, so choose a non-sensitive machine name. There is no fallback to plaintext, an IP URL, a short hostname, or a self-signed certificate.
- Use a dedicated tailnet tag, e.g. `tag:marten`, with limited tag ownership. Allow only the intended users/devices to connect to that tag on `tcp:443`. If an existing broad allow rule also grants that access, narrow that rule; adding a stricter grant does not override an existing allow. No inbound Internet listener or host firewall port 443 rule is needed.
- Generate a short-lived, **one-off, non-ephemeral Tailscale device auth key** tagged only for this server. Preapprove it if required by your device approval policy, or complete approval in the admin console. Do not use a tailnet API key, OAuth client secret, reusable key, or another administrator credential. The helper accepts only `tskey-auth-…` format; the expiry, tags and one-off setting are chosen in Tailscale.

Set `MARTEN_HOME` to the server's actual home (default `~/.marten`). Build the helper from the same checkout and install it as the user running Marten:

```sh
marten_home="${MARTEN_HOME:-$HOME/.marten}"
mkdir -p "$marten_home/bin" "$marten_home/secrets"
chmod 700 "$marten_home/secrets"
(
  cd src/Marten.Tsnet
  CGO_ENABLED=0 go build -mod=readonly \
    -tags 'ts_omit_logtail ts_omit_oauthkey ts_omit_identityfederation' \
    -trimpath -o "$marten_home/bin/marten-tsnet" .
)
```

Use a secret-file editor or your normal secret-management workflow to create `$MARTEN_HOME/secrets/tailnet-auth-key` (owned by the Marten user, mode `0600`) containing only the device auth key. Do not put it in shell command arguments, environment variables, `config.json`, logs, or a pairing QR. Add the following to `config.json`, merging it with existing settings:

```json
{
  "tailnet": { "host": "marten.example-tailnet.ts.net" }
}
```

Choose the exact lowercase FQDN, with no scheme, path, port, or trailing dot. The helper requests the first DNS label as the device name. If there is a collision and Tailscale assigns a different name (e.g. `marten-1`), startup is refused; reconcile the enrolled device's actual name in the admin console and Marten configuration before trying again. Restart Marten to apply changes. The server logs the canonical HTTPS URL only after connecting, verifying the actual device name, obtaining a certificate, and opening the private listener. Finish [native pairing](device-pairing.md) from the local web UI. Wait for readiness before creating a QR. Use a different, short-lived, single-use client auth key with limited client grants; do not put the server enrollment key into the QR. Provisioning automation is separate. The readiness message is a server-side startup check, not proof that an iPhone can reach the listener or that client grants, device approval, or DNS work on that device.

## State and operations

After successful enrollment, remove `secrets/tailnet-auth-key`. Persistent node identity and certificate state live in the service-owned `secrets/tailnet-state` directory (`0700`, excluded from Marten backups). Keep it on private storage supporting Unix permissions and file locking. Never point two instances at it or copy it to another server. If state is lost or registration needs to be replaced, remove the old device in Tailscale's admin console before provisioning a new one with a fresh key. For a migration, enroll a distinct node rather than sharing device state.

Leave `tailnet` absent or set it to `null` to disable the helper. The helper is tied to Marten's lifetime and exits even if its parent crashes. Shutdown closes the helper lease and waits for cleanup within the bounded `tailnet-stop` phase; a timeout is reported as an incomplete shutdown. On failure the native listener stays closed or goes away; local Marten remains available. Fix the reported configuration/approval/DNS/TLS issue and restart Marten; the supervisor does not retry enrollment automatically. Disabling the local helper is not a substitute for removing its tailnet device or revoking paired devices when decommissioning it.

No tailnet account is required to run transport tests:

```sh
cd src/Marten.Tsnet
CGO_ENABLED=0 go test -mod=readonly \
  -tags 'ts_omit_logtail ts_omit_oauthkey ts_omit_identityfederation' ./...
```

## Rollout boundary

1. **Stage dark.** Retest the exact release SHA and follow the [macOS cutover runbook](release-cutover.md). The cutover builds the web UI and .NET server, **not** the Go helper. Build the matching helper for the production platform and stage it separately. The binaries (including an installed helper) can ship while `tailnet` and `pairingOrigin` are unset; the helper does not start and pairing remains off. Verify the local UI and existing outside-client paths after cutover.
2. **Authorize and provision.** Confirm the exact canonical FQDN and public Certificate Transparency disclosure before enabling HTTPS. Limit tailnet TCP 443 access to the intended clients and check overlapping grants. No Funnel, Tailscale Serve, `remoteHosts` entry for the embedded authority, trust bypass, or changes to outside-client trust. Sera places the short-lived, one-off server key privately in the `0600` file described above; use a separate, restricted, one-off key for each client. Never copy scratch state or credentials to production.
3. **Activate and verify.** Install the matching helper before enabling `tailnet.host`, then restart via the cutover procedure. Require readiness for the exact FQDN, confirm the web UI is still local and no anonymous, legacy-token or outside API access works on the embedded authority. Issue a QR from local web; confirm the displayed origin in the native app and require ordinary platform TLS for HTTPS and WSS. Verify a device read, WSS event, and revocation. Remove the server key file after enrollment. If the authority, approval, TLS, or scope differs from what was authorized, stop rather than relaxing checks.
4. **Roll back.** Remove or null `tailnet`, keep `pairingOrigin` unset, and use the coordinated restart to close the listener. Revoke affected Marten devices separately (disablement does not erase their tokens). Revoke unused enrollment keys and remove any unintended or decommissioned nodes in Tailscale; removing a node alone does not revoke its Marten token. Verify local service and outside-client access again.
