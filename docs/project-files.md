# Paired project Files pilot

Off by default. There is no web UI or external-client access. Files is for exact existing project bindings, not Marten storage, agent working directories, or worktrees. A binding by itself is **not** permission to browse. An approved checkout can contain secrets in ordinary, non-hidden files; this feature is not a secret scanner.

**Serving is disabled on all normal builds**, even with a configured grant. Only the scratch test harness can run the Darwin resolver (arm64 macOS 26+ and local APFS). The runtime accepted the descriptor-resolution flags in scratch and fixture checks passed, but that does not establish the full containment guarantee under concurrent ancestor changes and mount transitions. Do not enable until reviewed and verified; there is no path-based fallback. Unsupported platforms, changed root identity, an unavailable mount, or a changed/removed binding fail closed. No filesystem path is accepted from a device. Root grants are local, reviewed config; no new write API is exposed. Assume the host operator controls configuration, mounts, and protected storage, and do not run the service privileged.

For an approved pilot root, use the scratch-built binary's local `files-grant <project-id> <exact-binding> <label>` command to print a candidate config object. It does not read or modify Marten home. Check the resolved path and exact binding. The `projectFiles` field is the proposed allowlist, not an activation switch; the scratch tests build it in memory and do not read any live configuration. Confirm neither the path nor its physical target is broad or overlaps Marten home/backups. An empty array (the default) removes access. No binding, root grant, or mount changes can be made by a device. A change to the recorded identity (including a different device ID after reboot/remount) needs fresh local review, not automatic re-pinning.

All routes use the current canonical paired HTTPS origin, `Authorization: Bearer …`, and no query string. Requests to the following routes are GET-only:

| `/api/projects/{id}/files` suffix | Response |
| --- | --- |
| `/roots` | Approved available roots, with opaque `id` and `label`. |
| `/entries/{handle}` | `{entries, next?}` for a directory ID or cursor; rows contain `id`, `name`, `directory`, optional `bytes`, and `modified`. |
| `/bytes/{handle}/text` | At most 256 KiB from the selected revision. |
| `/bytes/{handle}/raster` | Full selected revision, limited to 8 MiB. Client validates type, dimensions and pixels. |
| `/bytes/{handle}/share` | Full selected revision, limited to 32 MiB. Explicit local export only; no share URLs. |

Responses are uncached. Handles are in-memory, expiring, and scoped to the project and device. 404 means missing, disabled, or expired; 409 means unavailable/changed; 413 means over a scan or byte limit; 429 means temporarily busy. Refresh from roots for expired navigation. Directories paginate at 100 visible entries and are capped at 2,000 scanned entries. Hidden names, symlinks, nested mounts, special files and multiply linked files are excluded. No range/resume. The bytes are an observed live filesystem revision, not an atomic snapshot. Discard partial/changed transfers. An exported local copy cannot be recalled.
