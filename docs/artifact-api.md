# Operator artifact contract

A successful `publish_artifact` tool result can have `artifacts`, an optional array in the standard transcript page, full operator transcript, and the existing `entry` event. Old entries/hosts may omit it. No separate list event or polling is required. Treat unknown preview kinds as download-only. Match the result to its tool call if both are in the current transcript window; otherwise render at the result. Do not prefetch.

```json
{
  "kind": "tool_result",
  "callId": "call_123",
  "artifacts": [{"id":"3c856041d56a4121b6f5134f44cb12b6","name":"summary.txt","bytes":1234,"previewKind":"text"}]
}
```

| GET (no query) | Success |
| --- | --- |
| `/api/agents/{agentId}/artifacts/{artifactId}` | Exact original bytes (`application/octet-stream`), ≤8 MiB. |
| `/api/agents/{agentId}/artifacts/{artifactId}/preview` | `text/plain; charset=utf-8` (≤64 KiB, complete UTF-8 prefix, `X-Marten-Preview-Truncated: 1` if clipped), or `image/png` (static PNG ≤2 MiB, 8 million pixels and 8,192 pixels on either axis); `previewKind` is `text`, `png`, or `none`. |

IDs are 32 lowercase hexadecimal characters and scoped by exact agent ID. Use the current pinned HTTPS authority/transport for native calls; pass operator credentials as `Authorization: Bearer …`, never in a URL. The normal local web operator credential is admitted through the existing web host policy. Outside clients cannot fetch bytes or read the refs. Success is HTTP 200 with `Cache-Control: no-store`, `X-Content-Type-Options: nosniff` and `Content-Disposition: attachment`, including for the preview route. 404 includes unknown owner, missing bytes (including a normal backup restore), or no usable preview. There are no redirects, share URLs, range/resume, or upload endpoints.

Clients should reject redirects, check status and media type, cap bytes received independently of `Content-Length`, reject incomplete downloads, and discard partial transfers on cancellation or route change. Preview as plain text or bounded raster, not as a document. Keep local exports explicit; stage native picker/share files in a protected, backup-excluded temp directory and remove them once the consumer is done. Old messages have no artifact fields. Existing tool images and truncated tool text are unchanged.

## Host storage

`publish_artifact` accepts `filename` and nullable `label`. Stage under `$MARTEN_HOME/agents/<id>/artifact-inbox/`; no absolute paths, nested names, worktree lookup, source links, or outside-client sharing. The host opens a flat entry relative to a verified private staging directory, copies from the checked descriptor, and syncs the snapshot before it commits the transcript entry. Agents must finish writing before publishing. The feature protects the publishing interface; an agent with a shell already runs as the Marten user and is not sandboxed. Snapshots survive deletion of the staged copy, restart and context reset. Routine backups include references but not staged or published bytes. Do not put secrets in staging. There is no artifact deletion UI or retention timer in v1.

The host pins the configured Marten home (which may be an intentional symlink) and validates the descendants by descriptor. A server that encounters the artifact writer lease held by another process exits before initializing the home, even through a different alias or with a different port. This guarantee applies only to servers that honor the lease; do not overlap with older releases or external maintenance writers. Unsupported locking disables artifact publishing with a warning; once acquired, the lease remains held even if the artifact inventory is unverifiable and publishing is disabled. The host syncs the snapshot, relevant directory entries and the result log before acknowledging the tool. This ordering is designed for process failure; it is not a guarantee about power loss or storage hardware. If the result write is indeterminate, the host retains the snapshot, disables further publishing for that process, and prevents the affected transcript from being written until restart. Startup indexes the durable transcript before repairing dangling calls. A corrupt older segment prevents only that agent from loading; its files are retained. Finalized but unreferenced snapshots still count against quota in v1; recovery automatically removes only verified temporary files. Outside transcripts omit the publication's tool arguments, raw replay, and structured references; this does not redact other conversation text.
