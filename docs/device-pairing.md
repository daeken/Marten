# Pairing a native Marten device

The native app must reach Marten over private HTTPS and validate TLS normally. There are two setup modes:

- **Embedded Tailscale (one scan):** Follow [embedded tailnet setup](embedded-tailnet.md). Marten uses `https://<tailnet.host>` only after the helper verifies that device's actual DNS name, obtains a matching certificate, and opens the tailnet listener. `pairingOrigin` is ignored in embedded mode, even if the helper is unavailable. Supply a fresh **one-off client device auth key** from Tailscale when creating the QR. Give it only the identity/tag and grants the client needs to reach this server. Use the shortest practical lifetime and preapproval only if needed. Do not reuse the server's provisioning key or use a Tailscale API key, reusable auth key, or OAuth secret. Marten treats the key as opaque beyond checking its syntax; it cannot attest one-time use, remaining lifetime, approval or client scope. Tailscale configures and enforces those properties. Marten does not create or revoke these keys.
- **Direct private HTTPS (no embedded enrollment):** Configure `"pairingOrigin": "https://marten.example"` and restart. Use an origin only (optional port, no path, user info, query or fragment). Your trusted TLS proxy must validate and preserve the external authority as upstream `Host`, never rewrite it to the loopback Host or accept a loopback Host from peers. Use a dedicated hostname, not a `remoteHosts` web alias; forwarded headers do not select the QR origin. The app needs connectivity already; the QR omits `tsAuthKey`.

## Setup and revocation

On the server, open the built web UI at `http://127.0.0.1:7450/` (or the configured local port) and sign in with the local operator token. Under **Settings → Paired devices**, enter a client enrollment key if prompted, then choose **Show pairing QR**. Scan inside the Marten app, not with the system camera or another scanner. The QR is drawn locally in the browser. Treat the QR as a secret: don't screenshot, copy, share, or log it. **Hide QR** dismisses the display; it does not revoke anything. Let the Marten offer expire and revoke any exposed Tailscale key in Tailscale. If a device has already used that key, remove the unexpected node there as well.

To revoke a device, refresh the list and select **Revoke** beside it. Already-paired clients with UI support can also invite or revoke devices remotely. A revoked token no longer authenticates new requests, and Marten disconnects its existing event WebSockets. Completed operations are not undone. Revoke any other devices the lost device may have paired; if you suspect compromise, audit other UI actions and rotate any exposed secrets. Outstanding codes expire within five minutes; restarting the backend clears them. Marten revocation does not remove the node from Tailscale, and removing the node from Tailscale does not revoke its Marten token. Do both for a lost embedded device.

## Native API contract

| Method and path | Authentication | Response |
|---|---|---|
| `GET /api/pair/devices` | Paired Bearer, or local operator Bearer | `{ "enabled": true, "requiresTailscaleEnrollment": true, "devices": [{ "id": "…", "name": "…", "createdAt": "…" }] }` |
| `POST /api/pair/create` | Paired Bearer, or local operator Bearer | `{ "payload": "<raw QR JSON text>", "expiresAt": "…" }`; send `application/json` with `{ "tsAuthKey": "…" }` in embedded mode, no body in direct mode. `400` for missing/invalid key or direct-mode body, `409` if not ready, `429` at capacity |
| `POST /api/pair/redeem` | No Authorization header; send `application/json` with `{ "code": "…", "deviceName": "…" }` to the QR's HTTPS origin | `{ "token": "…" }`; `400` for invalid, expired or consumed codes; `429` when throttled |
| `DELETE /api/pair/devices/{id}` | Paired Bearer, or local operator Bearer | `204` (or `404` if not found) |

Management with the original operator token requires the exact local Host and loopback connection; if a browser Origin is present, it must match that local origin. Having an outside-agent token does not grant access to pair management. Redeem only against the canonical HTTPS authority. Reject redirects for enrollment-related Marten requests and do not forward bearer headers to another origin. Do not log credentials, requests or responses on the client, server, or any intermediate proxy. Store the returned device token in the OS credential store and send it only in `Authorization: Bearer …`, including on the `GET /api/events` WebSocket upgrade, image and notebook downloads, and process logs. Never put a device bearer in a URL or copy the operator token onto the phone. A `401` after revocation means the device needs to pair again. The browser UI remains the local bootstrap client; the paired Bearer contract targets native clients that can attach an Authorization header to downloads and the WebSocket upgrade.

Invitations have 256 bits of randomness, expire after five minutes, are kept only as in-memory hashes, and can be exchanged once even under simultaneous requests. The server caps outstanding invites at eight and paired devices at 64. Pairing JSON bodies are limited to 4 KiB and five seconds to read, and redemption admits at most eight concurrent attempts and 60 attempts per rolling minute across the server. This deliberately avoids trusting proxy-forwarded addresses; flooding can temporarily delay legitimate pairing. Device tokens also have 256 bits of randomness and are stored only as SHA-256 hashes under `secrets/paired-devices.json`, which is not included in built-in snapshots. Keep this directory private. Pairing responses are `Cache-Control: no-store`.

### In-app QR (v2 only)

The QR contains raw UTF-8 JSON, **not a URL or deep link**. Example (illustrative placeholders):

```json
{"v":2,"server":"https://marten.example.ts.net","code":"<opaque code>","tsAuthKey":"tskey-auth-…","expiresAt":"2026-11-25T06:35:00.000Z"}
```

- `v`: JSON integer `2`; no v1/deep-link fallback.
- `server`: canonical HTTPS origin string, no trailing slash; embedded mode uses the exact lowercase device FQDN with no explicit port. A direct HTTPS origin may use a non-default port.
- `code`: 43-character unpadded base64url string. Redeem with a `deviceName` label (trimmed, 1–80 UTF-16 code units; no control, format, or line/paragraph separator characters). A label is for operator recognition, not a verified identity.
- `tsAuthKey`: in embedded mode only, a 12–256 character ASCII string starting with `tskey-auth-`, followed by letters, digits, hyphens or underscores. Use it **only** with Tailscale's enrollment mechanism. It is not sent in redemption or stored by Marten. Wait for connectivity, then redeem `code` on the exact origin using normal certificate validation. If enrollment succeeds but redemption fails or times out, check and remove any unintended enrolled node in Tailscale; enrollment and redemption are not atomic.
- `expiresAt`: UTC ISO 8601 string `yyyy-MM-ddTHH:mm:ss.SSSZ`. This is the **Marten offer deadline**, not the provider key's expiry. Time spent bringing up tailnet connectivity counts against the Marten deadline.

## Authority and endpoint inventory

A paired device is an **operator**, not an outside agent, read-only client, or sandbox. It can view private transcripts, run or approve tools, rerun notebook cells, change credentials/settings, control agents and projects, and pair additional devices. These powers may allow host code execution or access to local files/secrets as the Marten user. No pairing response returns the shared operator token, any other device token, or a consumed invitation. Use private transport and pair only devices you would trust with direct web UI access.

The API opts in individual UI endpoints. A paired token is accepted only at the canonical HTTPS authority, including for device management; localhost and legacy web aliases do not accept it. A paired token cannot claim an outside-agent identity (`/api/ext/*`) or automatically reach a new API route. [Jev Settings](jev-settings.md) are local-operator-only and do not admit paired devices. The native pairing authority rejects the root and static shell; those retain their existing Host/Origin behavior on localhost and other configured web aliases. Current UI API inventory (paths relative to `/api`):

| Surface | Methods and routes |
|---|---|
| Overview | `GET /status`, `/models`, `/usage`, `/agents`, `/processes`, `/inbox`, `/messages`; `POST /pause`; `PUT /config` |
| Agents | `POST /agents`; `GET /agents/{id}/transcript`, `/agents/{id}/tasks`, `/agents/{id}/schedules`, `/agents/{id}/processes`; `POST /agents/{id}/messages`, `/agents/{id}/interrupt`, `/agents/{id}/close`, `/agents/{id}/pause`, `/agents/{id}/start-fresh-context`; `PUT /agents/{id}/settings`; `DELETE /agents/{id}/schedules/{scheduleId}`; `POST /agents/{id}/processes/{n}/stop` |
| Notebooks/downloads | `GET /agents/{id}/notebook`, `/agents/{id}/notebook.ipynb`, `/agents/{id}/images/{file}`, `/agents/{id}/processes/{n}/log`; `POST /agents/{id}/notebook/{n}/rerun` |
| Projects and questions | `POST /projects`, `/projects/{id}/pause`, `/questions/{id}/answer`; `GET /projects/{id}/memory` |
| Runtime integrations | `GET /tools`, `/mcp`; `POST /tools/approve`, `/mcp/refresh` |
| Account and credentials | `POST /auth/login`, `/auth/logout`; `PUT /backends/{name}/key`; `GET /clients`; `POST /clients`; `DELETE /clients/{name}` |
| Backups | `GET /backups`; `POST /backups`; `PUT /backups/passphrase` |
| Devices and events | Pair management above; `GET /events` (WebSocket) |

Two existing UI flows still depend on the server host. ChatGPT OAuth (`POST /api/auth/login`) returns an authorization URL whose callback is on the **server's** loopback, so finish sign-in in a browser on the server; opening it on a phone would send the callback to the phone. Native-only sign-in would need a separate OAuth flow. Outside-client registration returns the path to the generated token **on the server**, not the secret itself, just like the local web UI; remotely delivering it would need a separate safe handoff.
