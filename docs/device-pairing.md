# Native device pairing (integration preview)

This integration composes the [embedded tailnet transport](embedded-tailnet.md) and the Marten redemption and revocation API. **One-scan enrollment is not available in this build.** The local **Settings → Paired devices** UI reports configuration validity and whether the helper has reported its HTTPS listener ready, and can revoke existing devices. A ready listener does not prove reachability from a particular phone (ACLs, DNS and connectivity still matter).

## Canonical network contract

`tailnet.host` names the exact enrolled lowercase MagicDNS FQDN, e.g. `marten.example.ts.net`. The sole native authority is `https://<host>` on implicit port 443; live events use WSS at that authority. The helper verifies the enrolled name and certificate before reporting ready, validates TLS SNI and HTTP Host, and preserves the canonical Host towards loopback. An absent Origin is valid; a supplied Origin must be exactly `https://<host>`. No fallback to other hosts/ports, React/PWA/static, shared operator bearer, outside-agent credentials, or outside-agent endpoints. `remoteHosts` remains a separate legacy web-proxy configuration and does not select the pairing destination.

## Implemented Marten wire contract

- `POST /api/pair/redeem` on the canonical HTTPS origin accepts JSON `{ "code": "…", "deviceName": "…" }`, no Authorization or query string. It returns `{ "token": "…" }`, `400` for invalid/expired/used code, and `429` when throttled. No issuer is exposed in this preview; tests seed an invitation directly.
- `GET /api/pair/devices` and `DELETE /api/pair/devices/{id}` require the original operator Bearer **and** actual local Host and origin, never a paired bearer. Delete returns `204` or `404`. `POST /api/pair/create` is local-only and returns `409` until the one-scan issuer lands.
- The established credential is an independent per-device Bearer. Send `Authorization: Bearer …` for JSON, attachments, logs and `GET /api/events` upgrade; never carriage in URL queries or fragments. Keep the token in the OS credential store. Revocation durably marks the credential unusable for future requests and disconnects its active WebSockets, without reversing prior operations.

The Marten code and token are 256-bit secrets. Invitations are in-memory hashes, short-lived (five minutes), bounded at eight pending, and atomically single-use. Redemption parses at most 4 KiB within five seconds, with at most eight concurrent attempts and 60 attempts per rolling minute. Up to 64 paired tokens are stored as SHA-256 hashes under `secrets/paired-devices.json`, outside built-in backups. Do not log QR payloads, Authorization headers or request/response bodies.

## Remaining one-scan enrollment work

The final QR should be a versioned, in-app **non-URL** payload encoding the canonical server and two distinct secrets: a one-off, nonreusable narrowly scoped Tailscale device enrollment key and a one-time Marten pairing code. Never include a shared UI bearer, server enrollment key, Tailscale administrative OAuth/API credential or external link. The marker and encoding are not yet frozen. The Swift app should validate the payload, bring up the embedded node with bounded `up()`, and only then redeem the Marten code over canonical HTTPS. No direct fallback. Production issuance/scan remains disabled pending agreed provisioning, payload and reconciliation flows.

An operator-provided one-shot Tailscale key is the least-privilege starting point; no blanket API token is needed. Configure the narrowest device/ACL scope the provider and phone support and verify scope/nonreuse there. Tailscale key expiry/revocation, the enrolled node, the Marten code and the Marten device bearer have independent lifecycles. Expiration of the QR/Marten code does not revoke a Tailscale key or remove an enrolled node. If enrollment succeeds but Marten redemption fails or is abandoned, reconcile the node separately using verified nonsecret identity or admin inventory; do not auto-delete based on client-supplied names. Test actual key policy, cancellation, and recovery before enabling issuance.

## Authority and endpoint inventory

A paired device is an **operator**, not an outside agent, read-only client, or sandbox. It can view private transcripts, run or approve tools, rerun notebook cells, change credentials/settings, control agents and projects, . These powers may allow host code execution or access to local files/secrets as the Marten user. No pairing response returns the shared operator token, any other device token, or a consumed invitation. Use private transport and pair only devices you would trust with direct web UI access.

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
| Devices and events | `GET /events` (WebSocket); pairing management is local only |

Two existing UI flows still depend on the server host. ChatGPT OAuth uses a callback on the **server’s** loopback and may need a provider identity-token hint; native `POST /api/auth/login` returns `409` with instructions to sign in on the server instead of returning an OAuth URL. Native-only sign-in would need a separate OAuth flow. Outside-client registration returns the path to the generated token **on the server**, not the secret itself, just like the local web UI; remotely delivering it would need a separate safe handoff.
