# Typesafe AI Jev Settings

This is enrollment and a capability gate only. No Jev HTTP, key validation against Typesafe AI, agent registration, agent authorization, dispatch, or safety classification is part of this change.

## Local operator API

All four routes require `Authorization: Bearer <ui-token>`, the direct HTTP loopback listener, a configured local `Host`, and (if supplied) `Origin` matching that exact local origin. The `OperatorRoute.LocalBearer` route metadata never falls through to pairing, outside identity, or legacy query authentication. Queries and forwarding headers are denied. All matched Jev routes are `Cache-Control: no-store`, including denials and errors. Do not expose the local listener through a proxy that rewrites remote requests to be indistinguishable from direct local requests.

- `GET /api/jev/settings`: returns `{hasKey, enabled, imagePreviewEnabled, storageHealthy}` (booleans).
- `PUT /api/jev/key`: accepts JSON `{ "key": "..." }`; returns status. Set or replace an opaque key, 1–512 ASCII characters total: one or more letters, digits, or `._~+/-`, followed optionally by trailing `=` padding. No whitespace, embedded `=`, or all-padding value. No upstream call. First enrollment or repair starts with both options off; replacement of a healthy record preserves the choices and retires the prior generation, even for the same key value.
- `PUT /api/jev/settings`: accepts JSON `{ "enabled": true, "imagePreviewEnabled": false }`; returns status. Both fields required. Each setting is independent; to opt into either requires a key (otherwise `409`). Image preview alone grants no capability. Unavailable storage gives `503`. An identical update is a no-op.
- `DELETE /api/jev/key`: withdraws local capability first and atomically replaces the record with a keyless, disabled record. Returns `{settings, localRemovalSucceeded, upstreamRevoked: false}` (`200` on successful atomic replacement, `503` if it could not be saved). Revoke the removed or superseded key separately in the Typesafe AI console. Local removal is not an upstream revocation, forensic erasure, or recall of copies someone already made.

The card takes a new key in a password field and clears the input when submitted, hidden by closing Settings, or navigating away. Marten does not store the key in browser storage or return it as part of any Jev response, render it in the agent UI, or pass it to an agent/tool. The existing Marten token handling is unchanged. Get an API key in the [TypeSafe console](https://console.typesafe.ai/keys); don't paste it into a prompt or transcript. Removing it here does not revoke it at TypeSafe.

Once agent dispatch is wired, enabling Jev makes it available to all agents without per-call approval. Explicitly requested payloads go to TypeSafe; images require the separate preview opt-in.

## Storage and internal interface

Marten atomically replaces `Home.Secrets/jev-settings.json` with a strict, versioned record via a 0600 temporary file inside the existing private directory. Marten backups exclude it. The file is flushed before rename; the directory is not flushed, so success does not guarantee survival of a machine or power failure. Interruption before rename can leave a 0600 temporary file containing key bytes in the same excluded directory; there is no automatic startup cleanup. The record is locally readable by the service account: this is not isolation from arbitrary same-UID programs, administrators, or separately authorized whole-Mac Files access.

A missing record on startup starts off. An invalid, insecure or unexpectedly changed record fails closed; after detecting loss of trust the running process requires explicit replacement/removal rather than silently adopting restored bytes. Failed persistence withdraws the running capability; if the disk could not be changed, retry local removal before restarting and revoke upstream separately. A restart can only judge the record it actually reads.

- `JevSettings.TryAcquire() -> JevGrant?` gives an immutable effective generation only when storage is trusted and Jev is enabled. Identity of the lease is its generation; neither the lease nor its key is a wire contract.
- `JevGrant.ImagePreviewEnabled`, `.Revoked` (`CancellationToken`), and `.GetKeyForProvider()` (internal string accessor) belong to that generation. Keep them in the future broker; never serialize a lease or include the key in agent inputs, outputs or diagnostics.
- `JevSettings.IsCurrent(JevGrant?) -> bool` revalidates storage and compares the grant against the active generation. The future broker should link cancellation and recheck immediately before sending and before using or publishing a result. This is a cooperative fence, not an atomic transaction around external sends or downstream publication; stale-result handoff needs a broker-side fence. Retirement swaps the active generation under the settings lock and triggers cancellation outside it. Unexpected storage changes are detected at the next settings read/fence, not by a background watcher.
