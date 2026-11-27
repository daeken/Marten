# Files containment review: keep Darwin serving disabled

## Required guarantee

Files remains read-only and sees the live contents of an exact, approved project binding. It is not a publisher, snapshot store, or agent worktree browser. Device authorization, root approval, entry selection and filesystem containment are separate checks. A stale handle must never authorize a replacement entry. Reads always use the already-open regular file; they must not switch to another object after it moves. A final re-resolution and revision check rejects observed moves and changes before delivery of the last chunk, but cannot promise an atomic copy of a file with concurrent writers. Do not emit the final chunk on failure, and require the client to discard incomplete transfers.

Assume the host administrator and kernel are trusted, but ordinary workspace writers can concurrently rename and replace entries. The privileged mount test belongs in a disposable VM, not on the active Mac. A string path, vnode spelling, equal `st_dev`, or a successful post-open stat is not an atomic precondition on the byte-bearing open.

## Darwin decision

Keep the test-only resolver gate in #74. The draft uses a verified directory descriptor, full relative resolution with `O_RESOLVE_BENEATH | O_NOFOLLOW_ANY`, metadata-only inspection, local APFS and identity/revision checks. Current tests verify stable and replaced entries. They do **not** show either of these stronger properties:

1. The kernel rejects every nested mount during the same lookup that selects the object. A loop that stats parents, then does another lookup from the root, is not equivalent. `st_dev` is a filesystem/device comparison, not a mount boundary guarantee.
2. The byte-bearing open can acquire read access to **the exact regular vnode previously checked**, without another workspace-name lookup that can select a special file. `O_NONBLOCK` and checking the returned descriptor before reading are useful defenses but are not a regular-file-only open. `proc_pidfdinfo` is corroborating metadata, not a containment primitive.

Do not ship a best-effort realpath/lexical check, a pathname reopen with additional checks, a sandbox rule that matches path spellings, or a privileged helper as a substitute. Local APFS and unprivileged operation reduce exposure but don't prove the contract. A Darwin implementation is viable only after identifying and validating supported kernel primitives (or an equivalent isolation design) that enforce both properties. An immutable export would change live Files semantics; keep it a separate artifact/publish feature, not a fallback.

## Prototype direction for a supported filesystem mechanism

If a Linux deployment becomes useful, use `openat2` as a reference path, not an implementation claim for this Mac:

- Pin a verified approved root, then resolve each complete descendant name in a **single syscall** relative to that root, with `RESOLVE_BENEATH | RESOLVE_NO_SYMLINKS | RESOLVE_NO_MAGICLINKS | RESOLVE_NO_XDEV`. Use `O_PATH | O_CLOEXEC` to classify without activating nodes. No downgrade on unsupported flags, kernel, or filesystem. A bounded retry can handle a transient race error; otherwise report stale/unavailable.
- Compare the metadata descriptor against the opaque handle's identity, file type, link count and revision. For regular bytes, research a same-object reopen through a **verified, private procfs descriptor directory**, never a process-supplied path; the live `O_PATH` descriptor must stay owned and referenced through reopen and the read descriptor must revalidate. No procfs, no reopen. Validate this mechanism before adopting it. Enumeration likewise must use only a checked directory descriptor and classify entries beneath the pinned root.
- Preserve current API/auth, deny hidden entries, keep caps/timeouts and revocation, do not add global shares or write to the source tree. Never send a newly selected object's bytes with an older handle. Existing binary contract/API and the independent iOS fixture need not change.

## Acceptance matrix before removing a platform gate

- On the supported OS/filesystem, record runtime version, mount type, and required syscall behavior. Successful listing/reads must use the same approved binding and live revision; unsupported platform/kernel/fs errors must deny when a grant exists.
- Mount fixtures in an isolated disposable VM: cover nested mounts (including same-filesystem bind mounts where applicable), remount/replacement of the approved root, and transitions concurrent with resolution. Require kernel denial before activation, not just a late revision mismatch.
- Driver/target trace or instrumented helper: assert there is no byte-bearing open on an unclassified object, including regular-to-FIFO/special-file substitution. Verify directory/file leaf and intermediate rename/symlink substitutions, short reads, changed revisions, and no final committed body on cancellation or revocation.
- All fixtures use generated scratch data. Outside/operator/query/alias auth and distinct-device handle rejection stay covered. No real binding, Mac mount changes, real pairing, or shared simulator is needed.

`ProjectFilesTests` exercises deterministic Kestrel/scratch cases; it is not a mount-race proof. Run `dotnet test tests/Marten.Tests --filter FullyQualifiedName~ProjectFilesTests` on arm64 Darwin for the prototype regression suite.
