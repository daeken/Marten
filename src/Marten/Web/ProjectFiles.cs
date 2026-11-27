using System.Buffers;
using System.Security.Cryptography;
using Marten.Projects;
using Marten.Storage;
using Microsoft.Win32.SafeHandles;
using static Marten.Web.ProjectFileSystem;

namespace Marten.Web;

internal sealed class ProjectFiles(Func<string, Project?> projectFor, Func<Config> config, string home, Func<string> backups, CancellationToken stopping) {
	const int MaxScan = 2_000, Page = 100, MaxGrants = 16, Text = 256 * 1024, Raster = 8 * 1024 * 1024, Share = 32 * 1024 * 1024;
	readonly SemaphoreSlim operations = new(4);
	readonly object gate = new();
	readonly Dictionary<string, (Scope Scope, object Value, long Expiry)> handles = new();
	sealed record Scope(string Project, CancellationToken Paired);
	sealed record Node(ProjectFileGrant Grant, string Relative, Stamp Revision);
	sealed record Cursor(Node Directory, Listed[] Rows, int Offset);
	sealed record Listed(string Name, Node Node);
	sealed record RootRow(string Id, string Label);
	sealed record FileRow(string Id, string Name, bool Directory, long? Bytes, DateTimeOffset Modified);
	sealed record Listing(FileRow[] Entries, string? Next);
	sealed class FilesError(int status) : Exception { public int Status => status; }

	public void Map(RouteGroupBuilder api) {
		var files = api.MapGroup("/projects/{id}/files");
		files.MapGet("/roots", (HttpContext ctx, string id) => Run(ctx, id, TimeSpan.FromSeconds(4), Roots)).AllowPaired();
		files.MapGet("/entries/{handle}", (HttpContext ctx, string id, string handle) => Run(ctx, id, TimeSpan.FromSeconds(4),
			(c, scope, ct) => Entries(c, scope, handle, ct))).AllowPaired();
		files.MapGet("/bytes/{handle}/{purpose}", (HttpContext ctx, string id, string handle, string purpose) => Run(ctx, id, TimeSpan.FromSeconds(25),
			(c, scope, ct) => Bytes(c, scope, handle, purpose, ct))).AllowPaired();
	}

	async Task Run(HttpContext ctx, string project, TimeSpan timeout, Func<HttpContext, Scope, CancellationToken, Task> body) {
		ctx.Response.Headers.CacheControl = "no-store";
		ctx.Response.Headers.XContentTypeOptions = "nosniff";
		var paired = Security.DeviceRevocationOf(ctx); // set only after canonical paired bearer admission
		if(!paired.CanBeCanceled || ctx.Request.QueryString.HasValue) { ctx.Response.StatusCode = 403; return; }
		if(!Supported || config().ProjectFiles is not { Count: > 0 and <= MaxGrants } || projectFor(project) == null) {
			ctx.Response.StatusCode = 404;
			return;
		}
		if(!operations.Wait(0)) { ctx.Response.StatusCode = 429; return; }
		using var stop = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, paired, stopping);
		stop.CancelAfter(timeout);
		try {
			stop.Token.ThrowIfCancellationRequested();
			await body(ctx, new Scope(project, paired), stop.Token);
		} catch(FilesError e) { Fail(ctx, e.Status); }
		catch(OperationCanceledException) { Fail(ctx, paired.IsCancellationRequested ? 403 : 503); }
		catch(Exception e) when(e is IOException or ArgumentException or UnauthorizedAccessException) { Fail(ctx, 409); }
		finally { operations.Release(); }
	}

	static void Fail(HttpContext ctx, int status) {
		if(ctx.Response.HasStarted) ctx.Abort();
		else { ctx.Response.Headers.ContentLength = null; ctx.Response.Headers.ContentDisposition = default;
			ctx.Response.ContentType = null; ctx.Response.StatusCode = status; }
	}

	string Save(Scope scope, object value) {
		lock(gate) {
			Collect();
			if(handles.Count >= 8192 || handles.Values.Count(h => h.Scope.Paired == scope.Paired) >= 2500) throw new FilesError(429);
			if(value is Cursor cursor) {
				var snapshots = handles.Values.Where(h => h.Value is Cursor).ToArray();
				if(!snapshots.Any(h => ReferenceEquals(((Cursor) h.Value).Rows, cursor.Rows)) &&
					(snapshots.Select(h => ((Cursor) h.Value).Rows).Distinct().Count() >= 16 ||
					 snapshots.Where(h => h.Scope.Paired == scope.Paired).Select(h => ((Cursor) h.Value).Rows).Distinct().Count() >= 6))
					throw new FilesError(429);
			}
			var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
			handles.Add(id, (scope, value, Environment.TickCount64 + 180_000));
			return id;
		}
	}

	void Collect() {
		var now = Environment.TickCount64;
		foreach(var (key, item) in handles) if(item.Expiry <= now || item.Scope.Paired.IsCancellationRequested) handles.Remove(key);
	}

	object Load(Scope scope, string handle) {
		if(handle.Length != 32 || !handle.All(char.IsAsciiHexDigitLower)) throw new FilesError(404);
		lock(gate) {
			if(!handles.TryGetValue(handle, out var item) || item.Scope != scope || item.Expiry <= Environment.TickCount64)
				throw new FilesError(404);
			return item.Value;
		}
	}

	// Canonical compare is deliberately conservative on case/Unicode, even on case-sensitive APFS.
	static bool ContainsPath(string parent, string child) {
		parent = parent.Normalize().TrimEnd('/');
		child = child.Normalize().TrimEnd('/');
		return child.Equals(parent, StringComparison.OrdinalIgnoreCase) || child.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
	}

	SafeFileHandle OpenRoot(Scope scope, ProjectFileGrant grant) {
		var cfg = config().ProjectFiles;
		if(cfg is not { Count: > 0 and <= MaxGrants } || grant.ProjectId != scope.Project || !cfg.Contains(grant) ||
			projectFor(scope.Project)?.Info.Bindings.Contains(grant.Binding, StringComparer.Ordinal) != true ||
			string.IsNullOrEmpty(grant.Binding) || !Path.IsPathFullyQualified(grant.Binding) || string.IsNullOrEmpty(grant.Identity) ||
			string.IsNullOrEmpty(grant.PhysicalPath) || Physical(grant.Binding) != grant.PhysicalPath)
			throw new IOException();
		var personal = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if(string.IsNullOrEmpty(personal) || ContainsPath(grant.PhysicalPath, Physical(personal) ?? personal)) throw new IOException();
		if(Physical(home) is not { } physicalHome) throw new IOException();
		foreach(var path in new[] { home, backups(), home.TrimEnd('/') + "-backups", physicalHome.TrimEnd('/') + "-backups" }) {
			var canonical = Physical(path) ?? Path.GetFullPath(path);
			if(ContainsPath(grant.PhysicalPath, canonical) || ContainsPath(canonical, grant.PhysicalPath)) throw new IOException();
		}
		return Root(grant.PhysicalPath, grant.Identity);
	}

	async Task Roots(HttpContext c, Scope scope, CancellationToken ct) {
		var roots = new List<RootRow>();
		foreach(var grant in config().ProjectFiles) {
			ct.ThrowIfCancellationRequested();
			if(grant?.ProjectId != scope.Project || !Leaf(grant.Label ?? "")) continue;
			try {
				using var root = OpenRoot(scope, grant);
				roots.Add(new RootRow(Save(scope, new Node(grant, "", Stat(root))), grant.Label!));
			} catch(Exception e) when(e is IOException or ArgumentException or UnauthorizedAccessException) { /* unavailable */ }
		}
		await c.Response.WriteAsJsonAsync(roots, ct);
	}

	async Task Entries(HttpContext c, Scope scope, string handle, CancellationToken ct) {
		var target = Load(scope, handle);
		var directory = target switch { Node n when n.Revision.Directory => n, Cursor s => s.Directory, _ => throw new FilesError(404) };
		using var root = OpenRoot(scope, directory.Grant);
		var device = Stat(root).Device;
		using var dir = Descendant(root, directory.Grant.PhysicalPath, directory.Relative, device);
		var now = Stat(dir);
		if(!now.Directory || now.Identity != directory.Revision.Identity) throw new FilesError(409);
		Listed[] rows;
		int offset;
		if(target is Cursor cursor) { rows = cursor.Rows; offset = cursor.Offset; }
		else {
			var sorted = new List<Listed>();
			var scanned = 0;
			foreach(var name in Names(dir)) {
				ct.ThrowIfCancellationRequested();
				if(++scanned > MaxScan) throw new FilesError(413);
				if(name == null || !Leaf(name)) continue;
				var rel = directory.Relative.Length == 0 ? name : directory.Relative + "/" + name;
				try {
					using var file = Descendant(root, directory.Grant.PhysicalPath, rel, device);
					var stat = Stat(file);
					sorted.Add(new Listed(name, new Node(directory.Grant, rel, stat)));
				} catch(IOException) { /* link, unsupported node, or entry vanished */ }
			}
			if(!Stat(dir).SameRevision(now)) throw new FilesError(409);
			rows = sorted.OrderBy(r => !r.Node.Revision.Directory).ThenBy(r => r.Name, StringComparer.Ordinal).ToArray();
			offset = 0;
		}
		var entries = rows.Skip(offset).Take(Page).Select(row => new FileRow(Save(scope, row.Node), row.Name, row.Node.Revision.Directory,
			row.Node.Revision.Directory ? null : row.Node.Revision.Size, DateTimeOffset.FromUnixTimeSeconds(row.Node.Revision.ModifySec))).ToArray();
		var next = offset + entries.Length;
		await c.Response.WriteAsJsonAsync(new Listing(entries, next < rows.Length ? Save(scope, new Cursor(directory, rows, next)) : null), ct);
	}

	async Task Bytes(HttpContext c, Scope scope, string handle, string purpose, CancellationToken ct) {
		var cap = purpose switch { "text" => Text, "raster" => Raster, "share" => Share, _ => throw new FilesError(404) };
		if(Load(scope, handle) is not Node n || !n.Revision.File) throw new FilesError(404);
		if(purpose != "text" && n.Revision.Size > cap) throw new FilesError(413);
		if(c.Request.Headers.ContainsKey("Range")) throw new FilesError(416);
		using var root = OpenRoot(scope, n.Grant);
		var device = Stat(root).Device;
		using var file = Descendant(root, n.Grant.PhysicalPath, n.Relative, device, readFile: true);
		if(!Stat(file).SameRevision(n.Revision)) throw new FilesError(409);
		var total = Math.Min(n.Revision.Size, cap);
		c.Response.ContentType = "application/octet-stream";
		c.Response.Headers.ContentDisposition = "attachment";
		c.Response.ContentLength = total;
		var block = ArrayPool<byte>.Shared.Rent(64 * 1024);
		try {
			long at = 0;
			do {
				ct.ThrowIfCancellationRequested();
				var wanted = (int) Math.Min(total - at, 64 * 1024);
				var got = wanted == 0 ? 0 : await RandomAccess.ReadAsync(file, block.AsMemory(0, wanted), at, ct);
				if(wanted > 0 && got == 0) throw new FilesError(409);
				at += got;
				// Don't finish a successful response unless the opened file still matches the selection.
				if(at == total) {
					using var verifiedRoot = OpenRoot(scope, n.Grant);
					using var current = Descendant(verifiedRoot, n.Grant.PhysicalPath, n.Relative, device);
					if(!Stat(file).SameRevision(n.Revision) || !Stat(current).SameRevision(n.Revision)) throw new FilesError(409);
				}
				ct.ThrowIfCancellationRequested();
				if(got > 0) await c.Response.Body.WriteAsync(block.AsMemory(0, got), ct);
			} while(at < total);
		} finally { ArrayPool<byte>.Shared.Return(block, clearArray: true); }
	}
}
