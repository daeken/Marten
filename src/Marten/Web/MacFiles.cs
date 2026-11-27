using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Win32.SafeHandles;
using Marten.Storage;
using static Marten.Web.MacFileSystem;

namespace Marten.Web;

internal sealed class MacFiles(bool enabled, Func<HttpContext, bool> nativeIngress, DevicePairing pairing,
	CancellationToken stopping, string rootPath = "/", Func<MacFiles.PublicationPhase, Task>? publicationFixture = null, Func<bool>? ingressReady = null) {
	internal sealed class Route;
	internal enum PublicationPhase { Staged, Committed, FirstChunk }
	internal int ActiveOperations { get { lock(gate) return busy.Count; } }
	static readonly Regex NativeFilesTarget = new(@"\A/api/mac-files/(?:roots|entries/[0-9a-f]{32}|bytes/[0-9a-f]{32}/(?:text|raster|share))\z",
		RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
	static bool CanonicalGet(HttpContext ctx) => HttpMethods.IsGet(ctx.Request.Method) && !ctx.Request.Headers.ContainsKey("Origin") &&
		ctx.Features.Get<IHttpRequestFeature>()?.RawTarget == ctx.Request.Path.Value;

	internal bool? StatusCapability(HttpContext ctx) {
		var native = nativeIngress(ctx);
		if(!enabled || !Supported || stopping.IsCancellationRequested || !native || !CanonicalGet(ctx) || ctx.Request.Path != "/api/status") return null;
		bool? advertised = null;
		pairing.CommitIfActive(Security.DeviceRevocationOf(ctx), () => {
			if(ingressReady?.Invoke() != false && !stopping.IsCancellationRequested) advertised = true;
		});
		return advertised;
	}
	const int MaxScan = 256, PageSize = 100, MaxHandles = 4096, PerDeviceHandles = 2200;
	readonly object gate = new();
	readonly HashSet<CancellationToken> busy = [];
	readonly Dictionary<string, (CancellationToken Device, object Value, long Expiry)> handles = [];
	sealed record Node(string Path, MacFileSystem.Version Version);
	readonly HashSet<Walker> walks = [];
	sealed class Walker(CancellationToken device, Node directory, SafeFileHandle fd) : IDisposable {
		public CancellationToken Device { get; } = device;
		public Node Directory { get; } = directory;
		public SafeFileHandle Fd { get; } = fd;
		public IEnumerator<string?> Names { get; } = MacFileSystem.Names(fd).GetEnumerator();
		public void Dispose() { try { Names.Dispose(); } finally { Fd.Dispose(); } }
	}
	sealed class Pending : Dictionary<string, object> {
		public string? Consumed;
		public Walker? Walk;
		public bool More;
		public bool Published;
	}
	sealed record RootRow(string Id, string Label);
	sealed record FileRow(string Id, string Name, bool Directory, long? Bytes, DateTimeOffset Modified);
	sealed record Page(FileRow[] Entries, string? Next);
	sealed record Prepared(byte[] Bytes, string Type, bool Attachment);
	sealed class Denied(int status) : Exception { public int Status => status; }

	public void Map(RouteGroupBuilder api) {
		if(enabled) stopping.Register(() => Reap(default));
		var files = api.MapGroup("/mac-files").WithMetadata(new Route());
		files.MapGet("/roots", (HttpContext ctx) => Run(ctx, (device, pending, token) => Roots(pending))).AllowPaired();
		files.MapGet("/entries/{handle}", (HttpContext ctx, string handle) => Run(ctx,
			(device, pending, token) => Entries(device, pending, handle, token))).AllowPaired();
		files.MapGet("/bytes/{handle}/{purpose}", (HttpContext ctx, string handle, string purpose) => Run(ctx,
			(device, pending, token) => Bytes(device, handle, purpose, token))).AllowPaired();
	}

	async Task Run(HttpContext ctx, Func<CancellationToken, Pending, CancellationToken, Prepared> prepare) {
		var response = ctx.Response;
		response.Headers.CacheControl = "no-store";
		response.Headers.XContentTypeOptions = "nosniff";
		var device = Security.DeviceRevocationOf(ctx);
		var native = nativeIngress(ctx);
		if(!device.CanBeCanceled || !CanonicalGet(ctx)) {
			response.StatusCode = 403; return;
		}
		if(!enabled) { response.StatusCode = 404; return; }
		if(!NativeFilesTarget.IsMatch(ctx.Request.Path.Value ?? "")) { response.StatusCode = 404; return; }
		if(!native) { response.StatusCode = 403; return; }
		if(!Supported) { response.StatusCode = 404; return; }
		if(ctx.Request.Headers.ContainsKey("Range")) { response.StatusCode = 416; return; }
		lock(gate) {
			if(busy.Count >= 4 || !busy.Add(device)) { response.StatusCode = 429; return; }
		}
		using var stop = CancellationTokenSource.CreateLinkedTokenSource(device, ctx.RequestAborted, stopping);
		stop.CancelAfter(TimeSpan.FromSeconds(20));
		Prepared? result = null;
		var pending = new Pending();
		try {
			stop.Token.ThrowIfCancellationRequested();
			result = await Task.Run(() => {
				Reap(device);
				stop.Token.ThrowIfCancellationRequested();
				return prepare(device, pending, stop.Token);
			}, stop.Token);
			if(publicationFixture != null) await publicationFixture(PublicationPhase.Staged);
			if(!pairing.CommitIfActive(device, () => {
				if(ingressReady?.Invoke() == false) throw new Denied(403);
				Commit(device, pending, stop.Token);
			})) throw new Denied(403);
			// Once committed, a cancellation aborts the transport, including a
			// stalled write. It cannot retract bytes already handed to the proxy.
			using var abort = stop.Token.Register(ctx.Abort);
			if(publicationFixture != null) await publicationFixture(PublicationPhase.Committed);
			stop.Token.ThrowIfCancellationRequested();
			response.ContentType = result.Type;
			response.ContentLength = result.Bytes.Length;
			if(result.Attachment) response.Headers.ContentDisposition = "attachment";
			for(var i = 0; i < result.Bytes.Length; i += 64 * 1024) {
				stop.Token.ThrowIfCancellationRequested();
				await response.Body.WriteAsync(result.Bytes.AsMemory(i, Math.Min(64 * 1024, result.Bytes.Length - i)), stop.Token);
				if(i == 0 && result.Attachment && publicationFixture != null) {
					await response.Body.FlushAsync(stop.Token);
					await publicationFixture(PublicationPhase.FirstChunk);
				}
			}
			await response.Body.FlushAsync(stop.Token);
		} catch(Denied e) { Fail(ctx, e.Status); }
		catch(OperationCanceledException) { Fail(ctx, device.IsCancellationRequested ? 403 : 503); }
		catch(Exception e) when(e is IOException or ArgumentException or UnauthorizedAccessException) { Fail(ctx, 409); }
		finally {
			if(result != null) Array.Clear(result.Bytes);
			try {
				if(pending.Walk is { } walk && (!pending.More || !pending.Published)) {
					if(pending.Consumed is { } old) lock(gate) handles.Remove(old);
					Release(walk);
				}
				if(stopping.IsCancellationRequested) Reap(device);
			} finally { lock(gate) busy.Remove(device); }
		}
	}

	static void Fail(HttpContext ctx, int status) {
		if(ctx.Response.HasStarted) ctx.Abort();
		else {
			ctx.Response.Headers.ContentLength = null;
			ctx.Response.Headers.ContentDisposition = default;
			ctx.Response.ContentType = null;
			ctx.Response.StatusCode = status;
		}
	}

	static string Stage(Dictionary<string, object> pending, object value) {
		var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
		pending.Add(id, value);
		return id;
	}

	void Reap(CancellationToken currentDevice) {
		var expiredWalks = new List<Walker>();
		lock(gate) {
			var now = Environment.TickCount64;
			foreach(var (id, entry) in handles) {
				if(!stopping.IsCancellationRequested && entry.Expiry > now && !entry.Device.IsCancellationRequested) continue;
				if(entry.Value is Walker walk) {
					if(entry.Device != currentDevice && busy.Contains(entry.Device)) continue;
					expiredWalks.Add(walk);
				}
				handles.Remove(id);
			}
		}
		foreach(var walk in expiredWalks) Release(walk);
	}

	void Release(Walker walk) {
		try { walk.Dispose(); } finally { lock(gate) walks.Remove(walk); }
	}

	void Commit(CancellationToken device, Pending pending, CancellationToken token) {
		lock(gate) {
			token.ThrowIfCancellationRequested();
			if(pending.Keys.Any(handles.ContainsKey)) throw new Denied(503);
			if(pending.More && pending.Walk is { } walk && !walks.Contains(walk)) {
				if(walks.Count >= 8 || walks.Count(w => w.Device == device) >= 2) throw new Denied(429);
				walks.Add(walk);
			}
			if(pending.Consumed is { } old) handles.Remove(old);
			var perDevice = handles.Values.Count(h => h.Device == device);
			// Evict oldest rows, never another listing's position. A very large
			// walk can keep making progress rather than freezing at its first quota.
			var deviceEvictions = Math.Max(0, perDevice + pending.Count - PerDeviceHandles);
			var globalEvictions = Math.Max(0, handles.Count - deviceEvictions + pending.Count - MaxHandles);
			if(deviceEvictions != 0 || globalEvictions != 0) {
				var ordered = handles.Where(h => h.Value.Value is Node).OrderBy(h => h.Value.Expiry).ToArray();
				foreach(var h in ordered.Where(h => h.Value.Device == device).Take(deviceEvictions)) handles.Remove(h.Key);
				foreach(var h in ordered.Where(h => handles.ContainsKey(h.Key)).Take(globalEvictions)) handles.Remove(h.Key);
			}
			var now = Environment.TickCount64;
			foreach(var (id, value) in pending) handles.Add(id, (device, value, now + 180_000));
			pending.Published = true;
		}
	}

	object Load(CancellationToken device, string handle) {
		if(handle.Length != 32 || !handle.All(char.IsAsciiHexDigitLower)) throw new Denied(404);
		lock(gate) {
			if(!handles.TryGetValue(handle, out var entry) || entry.Device != device || entry.Expiry <= Environment.TickCount64)
				throw new Denied(404);
			return entry.Value;
		}
	}

	static Prepared Json<T>(T value) => new(JsonSerializer.SerializeToUtf8Bytes(value, JsonIO.Options), "application/json", false);

	Prepared Roots(Dictionary<string, object> pending) {
		using var root = Root(rootPath);
		return Json(new[] { new RootRow(Stage(pending, new Node("", Inspect(root))), "Mac") });
	}

	Prepared Entries(CancellationToken device, Pending pending, string handle, CancellationToken ct) {
		var target = Load(device, handle);
		var node = target switch { Node n when n.Version.Stat.Directory => n, Walker w => w.Directory, _ => throw new Denied(404) };
		if(target is Walker consumed) {
			pending.Walk = consumed;
			pending.Consumed = handle;
		}
		using var root = Root(rootPath);
		using var dir = Descendant(root, node.Path);
		var revision = Inspect(dir);
		if(!revision.Stat.Directory || !revision.SameObject(node.Version)) throw new Denied(409);
		if(pending.Walk == null) {
			// A request may tentatively open one new walker; the global request
			// limit charges this before we know whether a continuation is needed.
			pending.Walk = new Walker(device, new Node(node.Path, revision), Descendant(root, node.Path));
		} else if(!revision.SameRevision(node.Version)) throw new Denied(409);
		var walk = pending.Walk;
		if(!Inspect(walk.Fd).SameRevision(revision)) throw new Denied(409);
		var rows = new List<(string Name, Node Node)>();
		var more = true;
		for(var scanned = 0; scanned < MaxScan && rows.Count < PageSize; scanned++) {
			ct.ThrowIfCancellationRequested();
			if(!walk.Names.MoveNext()) { more = false; break; }
			if(walk.Names.Current is not { } name || !Leaf(name)) continue;
			var path = node.Path.Length == 0 ? name : node.Path + "/" + name;
			if(!Representable(path)) continue;
			try {
				using var item = Descendant(walk.Fd, name);
				rows.Add((name, new Node(path, Inspect(item))));
			} catch(IOException) { /* unbrowseable or replaced before inspection */ }
		}
		var page = rows.OrderBy(r => !r.Node.Version.Stat.Directory).ThenBy(r => r.Name, StringComparer.Ordinal).Select(row => {
			var stat = row.Node.Version.Stat;
			return new FileRow(Stage(pending, row.Node), row.Name, stat.Directory, stat.Directory ? null : stat.Size,
				DateTimeOffset.FromUnixTimeSeconds(stat.ModifySec));
		}).ToArray();
		using var current = Descendant(root, node.Path);
		if(!Inspect(walk.Fd).SameRevision(revision) || !Inspect(current).SameRevision(revision)) throw new Denied(409);
		pending.More = more;
		return Json(new Page(page, more ? Stage(pending, walk) : null));
	}

	Prepared Bytes(CancellationToken device, string handle, string purpose, CancellationToken ct) {
		var cap = purpose switch { "text" => 256 * 1024, "raster" => 8 * 1024 * 1024, "share" => 32 * 1024 * 1024, _ => throw new Denied(404) };
		if(Load(device, handle) is not Node n || !n.Version.Stat.File) throw new Denied(404);
		var size = n.Version.Stat.Size;
		if(purpose != "text" && size > cap) throw new Denied(413);
		using var root = Root(rootPath);
		using var fd = Descendant(root, n.Path, readFile: true);
		if(!Inspect(fd).SameRevision(n.Version)) throw new Denied(409);
		var data = new byte[(int) Math.Min(cap, size)];
		try {
			var at = 0;
			while(at < data.Length) {
				ct.ThrowIfCancellationRequested();
				var got = RandomAccess.Read(fd, data.AsSpan(at, Math.Min(64 * 1024, data.Length - at)), at);
				if(got == 0) throw new Denied(409);
				at += got;
			}
			using var current = Descendant(root, n.Path);
			if(!Inspect(fd).SameRevision(n.Version) || !Inspect(current).SameRevision(n.Version)) throw new Denied(409);
			return new Prepared(data, "application/octet-stream", true);
		} catch { Array.Clear(data); throw; }
	}
}
