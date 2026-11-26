# Pairing a native Marten device

Pairing does not discover devices or depend on Wi-Fi, Tailscale, or any particular VPN. The app needs network access to the server's configured HTTPS origin and must validate its TLS certificate normally.

## Setup and revocation

1. Configure a **private** HTTPS transport to Marten's loopback listener. The trusted TLS proxy must validate the external authority and preserve that authority as the upstream `Host`. Never let a remote peer supply a loopback `Host`, and never rewrite an external `Host` to `127.0.0.1` or `localhost`. Marten does not use `X-Forwarded-Host`, a remote peer's address, or a request-provided URL to choose the QR origin. Do not log authorization headers, query values, or pairing request/response bodies at the proxy.
2. Set `"pairingOrigin": "https://marten.example"` in the server's `config.json` and restart Marten. This enables new invitations and admits the origin's authority to the Host check. Use an origin only (optional port, no path, credentials, query or fragment). `remoteHosts` alone does not select a pairing origin. Use a dedicated hostname: the pairing origin is native-only and does not serve the browser shell, operator-token traffic, or outside-agent API. Legacy web proxy aliases at other hosts continue to use `remoteHosts`. If `pairingOrigin` is unset or invalid, existing device credentials are not erased, but no new invitations can be created or redeemed.
3. On the server, open the built web UI via `http://127.0.0.1:7450/` (or `http://localhost:7450/`, adjusted for your port), and sign in with the existing operator token. Under **Settings → Paired devices**, choose **Show pairing QR** and scan it with the native app. Keep the QR private: the first person to redeem it is paired. The QR is rendered entirely in the browser; nothing is sent to a QR service. **Hide QR** closes the display; it does not cancel the invitation.
4. To revoke a device, refresh the list and select **Revoke** beside it. Already-paired clients with UI support can also invite or revoke devices remotely. A revoked token no longer authenticates new requests, and Marten disconnects its existing event WebSockets. Completed operations are not undone. Revoke any other devices the lost device may have paired; if you suspect compromise, audit other UI actions and rotate any exposed secrets. Outstanding codes expire within five minutes; restarting the backend clears all outstanding codes.

## Native API contract

| Method and path | Authentication | Response |
|---|---|---|
| `GET /api/pair/devices` | Paired Bearer, or local operator Bearer | `{ "enabled": true, "devices": [{ "id": "…", "name": "…", "createdAt": "…" }] }` |
| `POST /api/pair/create` | Paired Bearer, or local operator Bearer | `{ "uri": "marten://pair?v=1&server=<percent-encoded HTTPS origin>&code=<opaque code>", "expiresAt": "…" }`; `409` if disabled or `429` at capacity |
| `POST /api/pair/redeem` | No Authorization header; send `application/json` with `{ "code": "…", "deviceName": "…" }` to the QR's HTTPS origin | `{ "token": "…" }`; `400` for invalid, expired or consumed codes; `429` when throttled |
| `DELETE /api/pair/devices/{id}` | Paired Bearer, or local operator Bearer | `204` (or `404` if not found) |

Management with the original operator token requires the exact local Host and loopback connection; if a browser Origin is present, it must match that local origin. Having an outside-agent token does not grant access to pair management. Redeem only against the canonical HTTPS authority; no redirects to another origin. Store the returned device token in the OS credential store and send it only in `Authorization: Bearer …`, including on the `GET /api/events` WebSocket upgrade, image and notebook downloads, and process logs. Never put a device bearer in a URL or copy the operator token onto the phone. A `401` after revocation means the device needs to pair again. The browser UI remains the local bootstrap client; the paired Bearer contract targets native clients that can attach an Authorization header to downloads and the WebSocket upgrade.

Invitations have 256 bits of randomness, expire after five minutes, are kept only as in-memory hashes, and can be exchanged once even under simultaneous requests. The server caps outstanding invites at eight and paired devices at 64. Redemption bodies are limited to 4 KiB and five seconds to read, with at most eight concurrent attempts and 60 attempts per rolling minute across the server. This deliberately avoids trusting proxy-forwarded addresses; flooding can temporarily delay legitimate pairing. Device tokens also have 256 bits of randomness and are stored only as SHA-256 hashes under `secrets/paired-devices.json`, which is not included in built-in snapshots. Keep this directory private. Pairing responses are `Cache-Control: no-store`.

## Authority and endpoint inventory

A paired device is an **operator**, not an outside agent, read-only client, or sandbox. It can view private transcripts, run or approve tools, rerun notebook cells, change credentials/settings, control agents and projects, and pair additional devices. These powers may allow host code execution or access to local files/secrets as the Marten user. No pairing response returns the shared operator token, any other device token, or a consumed invitation. Use private transport and pair only devices you would trust with direct web UI access.

The API opts in individual UI endpoints. A paired token cannot claim an outside-agent identity (`/api/ext/*`) or automatically reach a new API route. The native pairing authority rejects the root and static shell; those retain their existing Host/Origin behavior on localhost and other configured web aliases. Current UI API inventory (paths relative to `/api`):

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
