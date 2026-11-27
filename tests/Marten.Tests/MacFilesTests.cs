using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Marten.Storage;
using Marten.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Marten.Tests;

public class MacFilesTests {
	[Fact]
	public async Task RejectsNonNativeLoopbackEvenWithRealPairedCredentialAndCanonicalHost() {
		await using var f = await Fixture.Start();
		await using var disabled = await Fixture.Start(enabled: false);
		Assert.Equal(HttpStatusCode.Forbidden, (await f.Get("roots", proof: false)).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await disabled.Get("roots", proof: false)).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await f.Get("roots", token: "")).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await f.Get("roots", token: f.Operator)).StatusCode);
		Assert.Equal(HttpStatusCode.Unauthorized, (await f.Get("roots", token: "outside")).StatusCode);
		Assert.Equal(HttpStatusCode.Forbidden, (await f.Get("roots?token=fake")).StatusCode);
		Assert.Equal(HttpStatusCode.Forbidden, (await f.Get("roots", host: "wrong.example")).StatusCode);
	}

	[Fact]
	public async Task ListsHiddenRegularFilesSkipsLinksAndSpecialsAndStagesExactBytes() {
		if(!MacFileSystem.Supported) return;
		await using var f = await Fixture.Start();
		var leaf = Path.Combine(f.Root, ".scratch");
		await File.WriteAllTextAsync(leaf, "test bytes");
		File.CreateSymbolicLink(Path.Combine(f.Root, "alias"), leaf);
		using(var p = System.Diagnostics.Process.Start("/usr/bin/mkfifo", Path.Combine(f.Root, "queue"))) {
			await p!.WaitForExitAsync(); Assert.Equal(0, p.ExitCode);
		}
		var root = (await f.Json("roots"))[0].GetProperty("id").GetString();
		var row = (await f.Json($"entries/{root}")).GetProperty("entries").EnumerateArray().Single();
		Assert.Equal(".scratch", row.GetProperty("name").GetString());
		var id = row.GetProperty("id").GetString();
		Assert.Equal(HttpStatusCode.NotFound, (await f.Get($"bytes/{root}/share")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await f.Get($"bytes/{id}/share", token: f.Other)).StatusCode);
		var bytes = await f.Get($"bytes/{id}/share");
		Assert.Equal(HttpStatusCode.OK, bytes.StatusCode);
		Assert.Equal("attachment", bytes.Content.Headers.ContentDisposition?.DispositionType);
		Assert.Equal(10, bytes.Content.Headers.ContentLength);
		Assert.Equal("test bytes", await bytes.Content.ReadAsStringAsync());
		await File.WriteAllTextAsync(leaf, "changed data");
		var stale = await f.Get($"bytes/{id}/share");
		Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
		Assert.Empty(await stale.Content.ReadAsByteArrayAsync());
	}

	[Fact]
	public async Task ContinuationsAreCredentialAndDirectoryRevisionBound() {
		if(!MacFileSystem.Supported) return;
		await using var f = await Fixture.Start();
		for(var i = 0; i < 103; i++) File.WriteAllText(Path.Combine(f.Root, $"f{i:000}"), "");
		var root = (await f.Json("roots"))[0].GetProperty("id").GetString();
		var page = await f.Json($"entries/{root}");
		Assert.Equal(100, page.GetProperty("entries").GetArrayLength());
		var next = page.GetProperty("next").GetString();
		Assert.Equal(HttpStatusCode.NotFound, (await f.Get($"entries/{next}", token: f.Other)).StatusCode);
		Assert.Equal(3, (await f.Json($"entries/{next}")).GetProperty("entries").GetArrayLength());
		Assert.Equal(HttpStatusCode.NotFound, (await f.Get($"entries/{next}")).StatusCode);
		var fresh = (await f.Json($"entries/{root}")).GetProperty("next").GetString();
		File.WriteAllText(Path.Combine(f.Root, "changed"), "");
		Assert.Equal(HttpStatusCode.Conflict, (await f.Get($"entries/{fresh}")).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await f.Get($"entries/{fresh}")).StatusCode);
	}

	[Fact]
	public async Task LargeDirectoriesProgressPastScanAndHandleCaps() {
		if(!MacFileSystem.Supported) return;
		await using var f = await Fixture.Start();
		for(var i = 0; i < 2311; i++) File.WriteAllText(Path.Combine(f.Root, $"f{i:0000}"), "");
		var root = (await f.Json("roots"))[0].GetProperty("id").GetString();
		var next = root;
		var seen = new HashSet<string>();
		for(var n = 0; next != null && n < 25; n++) {
			var page = await f.Json($"entries/{next}");
			var rows = page.GetProperty("entries").EnumerateArray().ToArray();
			Assert.InRange(rows.Length, 1, 100);
			foreach(var row in rows) Assert.True(seen.Add(row.GetProperty("name").GetString()!));
			next = page.TryGetProperty("next", out var value) ? value.GetString() : null;
		}
		Assert.Null(next);
		Assert.Equal(2311, seen.Count);
	}

	[Fact]
	public async Task AFilteredPageStillHasContinuationAndMakesProgress() {
		if(!MacFileSystem.Supported) return;
		await using var f = await Fixture.Start();
		for(var i = 0; i < 300; i++) File.CreateSymbolicLink(Path.Combine(f.Root, $"link{i:000}"), "missing");
		var root = (await f.Json("roots"))[0].GetProperty("id").GetString();
		var page = await f.Json($"entries/{root}");
		Assert.Empty(page.GetProperty("entries").EnumerateArray());
		var next = page.GetProperty("next").GetString();
		var tail = await f.Json($"entries/{next}");
		Assert.Empty(tail.GetProperty("entries").EnumerateArray());
		Assert.False(tail.TryGetProperty("next", out _));
	}

	[Fact]
	public async Task ByteCapsRejectWholeFileAndAReplacementSymlink() {
		if(!MacFileSystem.Supported) return;
		await using var f = await Fixture.Start();
		var original = Path.Combine(f.Root, "big");
		await using(var stream = File.Create(original)) {
			stream.WriteByte(0x5a);
			stream.SetLength(32L * 1024 * 1024 + 1);
		}
		var root = (await f.Json("roots"))[0].GetProperty("id").GetString();
		var id = (await f.Json($"entries/{root}")).GetProperty("entries")[0].GetProperty("id").GetString();
		using var prefix = await f.Get($"bytes/{id}/text");
		Assert.Equal(HttpStatusCode.OK, prefix.StatusCode);
		Assert.Equal(256 * 1024, prefix.Content.Headers.ContentLength);
		var body = await prefix.Content.ReadAsByteArrayAsync();
		Assert.Equal(256 * 1024, body.Length);
		Assert.Equal(0x5a, body[0]);
		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await f.Get($"bytes/{id}/raster")).StatusCode);
		Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await f.Get($"bytes/{id}/share")).StatusCode);
		Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, (await f.Get($"bytes/{id}/text", range: true)).StatusCode);
		var moved = Path.Combine(f.Root, "moved");
		File.Move(original, moved);
		File.CreateSymbolicLink(original, moved);
		using var result = await f.Get($"bytes/{id}/text");
		Assert.Equal(HttpStatusCode.Conflict, result.StatusCode);
		Assert.Empty(await result.Content.ReadAsByteArrayAsync());
	}

	[Fact]
	public async Task SnapshotQuotaDoesNotBlockOtherDeviceOrExistingContinuation() {
		if(!MacFileSystem.Supported) return;
		await using var f = await Fixture.Start();
		for(var i = 0; i < 101; i++) File.WriteAllText(Path.Combine(f.Root, $"f{i:000}"), "");
		var root = (await f.Json("roots"))[0].GetProperty("id").GetString();
		var cursor = (await f.Json($"entries/{root}")).GetProperty("next").GetString();
		await f.Json($"entries/{root}");
		using var denied = await f.Get($"entries/{root}");
		Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
		Assert.Empty(await denied.Content.ReadAsByteArrayAsync());
		Assert.Single((await f.Json($"entries/{cursor}")).GetProperty("entries").EnumerateArray());
		using var otherRoots = await f.Get("roots", token: f.Other);
		Assert.Equal(HttpStatusCode.OK, otherRoots.StatusCode);
		using var document = JsonDocument.Parse(await otherRoots.Content.ReadAsByteArrayAsync());
		var other = document.RootElement[0].GetProperty("id").GetString();
		Assert.Equal(HttpStatusCode.OK, (await f.Get($"entries/{other}", token: f.Other)).StatusCode);
	}

	sealed class CursorClock : TimeProvider {
		long ticks;
		public override long TimestampFrequency => TimeSpan.TicksPerSecond;
		public override long GetTimestamp() => Interlocked.Read(ref ticks);
		public void Expire() => Advance(TimeSpan.FromMinutes(4));
		public void Advance(TimeSpan delta) => Interlocked.Add(ref ticks, delta.Ticks);
	}

	[Fact]
	public async Task ShutdownDuringOffLockCloseRetiresBothWalkersExactlyOnce() {
		if(!MacFileSystem.Supported) return;
		var time = new CursorClock();
		var staged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var closed = new System.Collections.Concurrent.ConcurrentQueue<string>();
		var armed = 0;
		await using var f = await Fixture.Start(clock: time, publicationFixture: async phase => {
			if(phase == MacFiles.PublicationPhase.Staged && Interlocked.Exchange(ref armed, 0) == 1) {
				staged.SetResult();
				await resume.Task.WaitAsync(TimeSpan.FromSeconds(5));
			}
		}, closingFixture: path => {
			closed.Enqueue(path);
			if(path == "older") {
				closing.TrySetResult();
				release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
			}
		});
		foreach(var name in new[] { "older", "newer" }) {
			var dir = Directory.CreateDirectory(Path.Combine(f.Root, name)).FullName;
			for(var i = 0; i < 101; i++) File.WriteAllText(Path.Combine(dir, $"f{i:000}"), "");
		}
		var root = (await f.Json("roots"))[0].GetProperty("id").GetString();
		var dirs = (await f.Json($"entries/{root}")).GetProperty("entries").EnumerateArray()
			.ToDictionary(d => d.GetProperty("name").GetString()!, d => d.GetProperty("id").GetString());
		await f.Json($"entries/{dirs["older"]}");
		time.Advance(TimeSpan.FromMinutes(2));
		await f.Json($"entries/{dirs["newer"]}");
		Assert.Equal(2, f.RetainedStreams);
		Interlocked.Exchange(ref armed, 1);
		var pending = f.Get("roots");
		try {
			await staged.Task.WaitAsync(TimeSpan.FromSeconds(5));
			time.Advance(TimeSpan.FromMinutes(2));
			resume.TrySetResult();
			await closing.Task.WaitAsync(TimeSpan.FromSeconds(5));
			f.Stop();
			await f.SweepCompletion.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.Equal(2, f.RetainedStreams);
			Assert.Equal(1, f.ActiveOperations);
		} finally { resume.TrySetResult(); release.TrySetResult(); }
		using var response = await pending;
		await f.WaitForIdle();
		Assert.Equal(new[] { "", "older", "newer" }, closed.ToArray());
	}

	[Theory]
	[InlineData("expiry")]
	[InlineData("revoke")]
	[InlineData("shutdown")]
	public async Task AutonomousIdleCleanupNeedsNoFollowUpRequest(string mode) {
		if(!MacFileSystem.Supported) return;
		var time = new CursorClock();
		await using var f = await Fixture.Start(clock: time);
		for(var i = 0; i < 102; i++) File.WriteAllText(Path.Combine(f.Root, $"f{i:000}"), "");
		var root = (await f.Json("roots"))[0].GetProperty("id").GetString();
		await f.Json($"entries/{root}");
		Assert.Equal(1, f.RetainedStreams);
		if(mode == "expiry") time.Expire();
		else if(mode == "revoke") f.RevokeFirst();
		else f.Stop();
		await f.WaitForIdle();
		if(mode == "shutdown") await f.SweepCompletion.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ClaimedPositionCannotReplayAndFailedPublicationReleasesStream(bool shutdown) {
		if(!MacFileSystem.Supported) return;
		var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var arm = 0;
		await using var f = await Fixture.Start(operationBudget: TimeSpan.FromMilliseconds(700), publicationFixture: async phase => {
			if(phase == MacFiles.PublicationPhase.Staged && Interlocked.Exchange(ref arm, 0) == 1) {
				reached.SetResult();
				await resume.Task.WaitAsync(TimeSpan.FromSeconds(5));
			}
		});
		for(var i = 0; i < 210; i++) File.WriteAllText(Path.Combine(f.Root, $"f{i:000}"), "");
		var root = (await f.Json("roots"))[0].GetProperty("id").GetString();
		var cursor = (await f.Json($"entries/{root}")).GetProperty("next").GetString();
		Interlocked.Exchange(ref arm, 1);
		var pending = f.Get($"entries/{cursor}");
		try {
			await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
			Assert.Equal(HttpStatusCode.TooManyRequests, (await f.Get($"entries/{cursor}")).StatusCode);
			if(shutdown) { f.Stop(); await f.SweepCompletion.WaitAsync(TimeSpan.FromSeconds(5)); }
			else await Task.Delay(800);
			Assert.Equal(1, f.RetainedStreams);
		} finally { resume.TrySetResult(); }
		using var result = await pending;
		Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
		await f.WaitForIdle();
		if(!shutdown) Assert.Equal(HttpStatusCode.NotFound, (await f.Get($"entries/{cursor}")).StatusCode);
	}

	[Fact]
	public async Task LostCommittedPageDoesNotReplayAndSuccessorExpires() {
		if(!MacFileSystem.Supported) return;
		var time = new CursorClock();
		var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var arm = 0;
		await using var f = await Fixture.Start(clock: time, publicationFixture: async phase => {
			if(phase == MacFiles.PublicationPhase.Committed && Interlocked.Exchange(ref arm, 0) == 1) {
				reached.SetResult();
				await resume.Task.WaitAsync(TimeSpan.FromSeconds(5));
			}
		});
		for(var i = 0; i < 202; i++) File.WriteAllText(Path.Combine(f.Root, $"f{i:000}"), "");
		var root = (await f.Json("roots"))[0].GetProperty("id").GetString();
		var position = (await f.Json($"entries/{root}")).GetProperty("next").GetString();
		Interlocked.Exchange(ref arm, 1);
		using var clientAborted = new CancellationTokenSource();
		var pending = f.Get($"entries/{position}", cancellationToken: clientAborted.Token);
		try {
			await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
			clientAborted.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
		} finally { resume.TrySetResult(); }
		using var idleTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		while(f.ActiveOperations != 0) await Task.Delay(10, idleTimeout.Token);
		Assert.Equal(HttpStatusCode.NotFound, (await f.Get($"entries/{position}")).StatusCode);
		Assert.Equal(1, f.RetainedStreams);
		time.Expire();
		await f.WaitForIdle();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task RevocationBeforeCommitDeniesAndAfterCommitAborts(bool afterCommit) {
		if(!MacFileSystem.Supported) return;
		var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var held = 0;
		await using var f = await Fixture.Start(publicationFixture: async phase => {
			if(phase == (afterCommit ? MacFiles.PublicationPhase.Committed : MacFiles.PublicationPhase.Staged) && Interlocked.Exchange(ref held, 1) == 0) {
				reached.SetResult();
				await resume.Task.WaitAsync(TimeSpan.FromSeconds(10));
			}
		});
		var pending = f.Get("roots");
		try {
			await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
			Assert.Equal(HttpStatusCode.TooManyRequests, (await f.Get("roots")).StatusCode);
			Assert.Equal(HttpStatusCode.OK, (await f.Get("roots", token: f.Other)).StatusCode);
			f.RevokeFirst();
		} finally { resume.TrySetResult(); }
		if(afterCommit) await Assert.ThrowsAsync<HttpRequestException>(async () => await pending);
		else {
			using var response = await pending;
			Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
			Assert.Empty(await response.Content.ReadAsByteArrayAsync());
		}
		Assert.Equal(HttpStatusCode.Unauthorized, (await f.Get("roots")).StatusCode);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ActualLauncherStartsMatchedGoFixtureAndClosesLease(bool enabled) {
		var executable = Environment.GetEnvironmentVariable("MARTEN_MAC_FILES_PROXY_FIXTURE");
		if(executable == null || !(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())) return;
		using var data = new TempDir();
		var home = new Home(data.File("launcher-home"));
		Directory.CreateDirectory(Path.Combine(home.Root, "bin"));
		var helper = Path.Combine(home.Root, "bin", "marten-tsnet");
		File.Copy(executable, helper);
		File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		var matched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		TailnetTransport? transport = null;
		var builder = WebApplication.CreateSlimBuilder();
		builder.Logging.ClearProviders();
		builder.WebHost.UseUrls("http://127.0.0.1:0");
		await using var app = builder.Build();
		app.MapGet("/fixture/launcher", (HttpContext ctx) => {
			if(transport?.IsReady != true) return Results.StatusCode(425);
			var hasHeader = ctx.Request.Headers.ContainsKey(TailnetTransport.NativeProofHeader);
			if(hasHeader != enabled || transport.HasNativeProof(ctx) != enabled) return Results.StatusCode(403);
			matched.TrySetResult();
			return Results.NoContent();
		});
		await app.StartAsync();
		transport = new TailnetTransport(new Config {
			Port = new Uri(app.Urls.Single()).Port, MacFilesEnabled = enabled,
			Tailnet = new TailnetSettings { Host = "files-launch-fixture.scratch.ts.net" },
		});
		using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
		var launched = transport.RunAsync(home, _ => { }, stop.Token);
		try {
			if(await Task.WhenAny(launched, matched.Task).WaitAsync(TimeSpan.FromSeconds(7)) == launched)
				Assert.Fail("scratch Go child exited before the launcher accepted READY and checked proof mode");
			Assert.True(transport.IsReady);
			Assert.False(File.Exists(Path.Combine(home.Root, "fixture-eof")));
		} finally {
			await stop.CancelAsync();
			await launched.WaitAsync(TimeSpan.FromSeconds(7));
		}
		Assert.False(transport.IsReady);
		Assert.Equal("observed\n", File.ReadAllText(Path.Combine(home.Root, "fixture-eof")));
	}

	[Fact]
	public async Task CanonicalGoTlsToRealKestrelWhenFixtureExecutableProvided() {
		var executable = Environment.GetEnvironmentVariable("MARTEN_MAC_FILES_PROXY_FIXTURE");
		if(executable == null || !MacFileSystem.Supported) return;
		var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var chunks = 0;
		await using var f = await Fixture.Start(host: "example.com", publicationFixture: async phase => {
			if(phase == MacFiles.PublicationPhase.FirstChunk && Interlocked.Increment(ref chunks) == 2)
				await resume.Task.WaitAsync(TimeSpan.FromSeconds(10));
		});
		await File.WriteAllTextAsync(Path.Combine(f.Root, ".fixture"), "scratch bytes");
		using(var large = File.Create(Path.Combine(f.Root, "large"))) large.SetLength(4 * 1024 * 1024);
		try { await f.VerifyTls(executable, resume); } finally { resume.TrySetResult(); }
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		while(f.ActiveOperations != 0) await Task.Delay(10, timeout.Token);
		Assert.Equal(HttpStatusCode.OK, (await f.Get("roots", token: f.Other)).StatusCode);
	}

	sealed class Fixture(TempDir data, WebApplication app, string url, string root, string bearer, string other,
		string op, string ingress, string host, DevicePairing pairing, Guid first, MacFiles files) : IAsyncDisposable {
		readonly HttpClient client = new() { BaseAddress = new Uri(url) };
		public string Other => other;
		public string Operator => op;
		public string Root => root;
		string Host => host;
		public void RevokeFirst() => Assert.True(pairing.Revoke(first));
		public int ActiveOperations => files.ActiveOperations;
		public int RetainedStreams => files.RetainedStreams;
		public Task SweepCompletion => files.SweepCompletion;
		public void Stop() => app.Lifetime.StopApplication();
		public async Task WaitForIdle() {
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			while(ActiveOperations != 0 || RetainedStreams != 0) await Task.Delay(10, timeout.Token);
		}
		public async Task VerifyTls(string executable, TaskCompletionSource resume) {
			var start = new System.Diagnostics.ProcessStartInfo(executable) {
				UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
			};
			start.ArgumentList.Add("-test.run=^TestMacFilesProxyToKestrel$");
			start.Environment["MARTEN_TEST_MAC_FILES_KESTREL"] = "1";
			using var process = System.Diagnostics.Process.Start(start)!;
			try {
				using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
				await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { url, bearer, ingress }).AsMemory(), timeout.Token);
				process.StandardInput.Close();
				var error = process.StandardError.ReadToEndAsync(timeout.Token);
				var signal = await process.StandardOutput.ReadLineAsync(timeout.Token);
				var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
				Assert.True(signal == "FIXTURE-FIRST-CHUNK", (signal ?? "") + (signal == "FIXTURE-FIRST-CHUNK" ? "" : await output));
				Assert.Equal(1, ActiveOperations);
				RevokeFirst();
				resume.TrySetResult();
				await process.WaitForExitAsync(timeout.Token);
				Assert.True(process.ExitCode == 0, (await output) + (await error));
			} finally {
				if(!process.HasExited) {
					try { process.Kill(entireProcessTree: true); } catch(InvalidOperationException) when(process.HasExited) { }
					using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
					await process.WaitForExitAsync(cleanup.Token);
				}
			}
		}

		public static async Task<Fixture> Start(bool enabled = true, Func<MacFiles.PublicationPhase, Task>? publicationFixture = null,
			string host = "mac-files.example", TimeProvider? clock = null, TimeSpan? operationBudget = null,
			Action<string>? closingFixture = null) {
			var data = new TempDir();
			var root = Directory.CreateDirectory(data.File("scratch")).FullName;
			if(root.StartsWith("/var/", StringComparison.Ordinal)) root = "/private" + root;
			var home = new Home(data.File("home"));
			var cfg = new Config { PairingOrigin = "https://" + host };
			var pairing = new DevicePairing(home);
			var bearer = pairing.Redeem(pairing.Create()!.Code, "fixture-one")!;
			var first = pairing.List().Single().Id;
			var other = pairing.Redeem(pairing.Create()!.Code, "fixture-two")!;
			var ingress = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
			var security = new Security(home, cfg) { Pairing = pairing, ClientAuth = t => t == "outside" ? t : null };
			var builder = WebApplication.CreateSlimBuilder();
			builder.Logging.ClearProviders();
			builder.WebHost.UseUrls("http://127.0.0.1:0");
			var app = builder.Build();
			app.UseRouting(); app.Use(security.Middleware);
			var proof = System.Text.Encoding.ASCII.GetBytes(ingress);
			var files = new MacFiles(enabled, ctx => TailnetTransport.ConsumeNativeProof(ctx, proof),
				pairing, app.Lifetime.ApplicationStopping, root, publicationFixture, clock: clock,
				sweepInterval: TimeSpan.FromMilliseconds(20), operationBudget: operationBudget, closingFixture: closingFixture);
			files.Map(app.MapGroup("/api"));
			app.MapGet("/api/status", (HttpContext ctx) => Results.Json(new { macFiles = files.StatusCapability(ctx) }, JsonIO.Options)).AllowPaired();
			app.MapGet("/api/proof-control", (HttpContext ctx) => ctx.Request.Headers.ContainsKey(TailnetTransport.NativeProofHeader)
				? Microsoft.AspNetCore.Http.Results.BadRequest() : Microsoft.AspNetCore.Http.Results.NoContent()).AllowPaired();
			await app.StartAsync();
			return new Fixture(data, app, app.Urls.Single(), root, bearer, other, security.Token, ingress, host, pairing, first, files);
		}

		public async Task<HttpResponseMessage> Get(string tail, string? token = null, bool proof = true, string? host = null, bool range = false,
			CancellationToken cancellationToken = default) {
			using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/mac-files/{tail}");
			request.Headers.Host = host ?? this.Host;
			if(proof) request.Headers.Add(TailnetTransport.NativeProofHeader, ingress);
			if(range) request.Headers.Range = new(0, 1);
			if((token ?? bearer).Length != 0) request.Headers.Authorization = new("Bearer", token ?? bearer);
			return await client.SendAsync(request, cancellationToken);
		}
		public async Task<JsonElement> Json(string tail) {
			using var r = await Get(tail);
			Assert.Equal(HttpStatusCode.OK, r.StatusCode);
			Assert.True(r.Headers.CacheControl?.NoStore);
			using var document = JsonDocument.Parse(await r.Content.ReadAsByteArrayAsync());
			return document.RootElement.Clone();
		}
		public async ValueTask DisposeAsync() {
			client.Dispose(); await app.StopAsync();
			await files.SweepCompletion.WaitAsync(TimeSpan.FromSeconds(5));
			await app.DisposeAsync(); data.Dispose();
		}
	}
}
