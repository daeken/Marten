using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Marten.Agents;
using Marten.Models.OpenAI;
using Marten.Storage;
using Marten.Tools;
using Marten.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Marten.Tests;

public class ReleaseDrainTests {
	static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
	const string First = "abcdef0123456789abcdef0123456789";
	const string Second = "0123456789abcdef0123456789abcdef";
	const string Path = "/api/release/drain";

	[Fact]
	public async Task ActiveTurnFinishesItsToolAndFollowUpButNewTurnsAndSchedulesWait() {
		using var dir = new TempDir();
		var backend = new FakeBackend();
		var registry = new ToolRegistry();
		var manager = new AgentManager(new Home(dir.File("home")), new Config { Model = "fake-1" }, backend, registry);
		var active = manager.CreateChat("active", dir.Path);
		var recipient = manager.CreateChat("follow-up", dir.Path);
		var tool = new FollowUp(recipient);
		registry.Add(tool);
		backend.Script.Enqueue(_ => FakeBackend.Call("tool", tool.Name, "{}"));
		backend.Script.Enqueue(_ => FakeBackend.Say("finished the original turn"));
		backend.Script.Enqueue(_ => FakeBackend.Say("read the follow-up"));
		var due = DateTimeOffset.UtcNow;
		var schedule = recipient.AddSchedule("no-op", null, due, null, null);
		var scheduler = new Scheduler(manager);
		try {
			active.SendOperator("start", SendMode.Queued);
			await tool.Started.Task.WaitAsync(Deadline);
			Assert.Equal("draining", manager.Restart.BeginRelease(First, 30)?.State);
			Assert.False(manager.Restart.Stopping.IsCancellationRequested);
			Assert.False(manager.Restart.CancelTools.IsCancellationRequested);
			Assert.Empty(scheduler.Tick(due));
			Assert.Equal(schedule, Assert.Single(recipient.Schedules));
			tool.Finish.SetResult();
			await Wait.Until(() => manager.Restart.GetRelease(First)?.ActiveOperations == 0);
			Assert.Equal("busy", manager.Restart.GetRelease(First)?.State);
			Assert.Equal(1, manager.Restart.GetRelease(First)?.PendingMessages);
			Assert.Contains(active.Transcript.Entries, e => e.Kind == EntryKind.ToolResult && e.Text == "saved");
			Assert.Equal(EntryKind.Idle, active.Transcript.Last?.Kind);
			Assert.Empty(recipient.Transcript.Entries);
			Assert.True(manager.Restart.AbortRelease(First));
			await Wait.Until(() => recipient.Transcript.Last?.Kind == EntryKind.Idle);
			Assert.Contains(recipient.Transcript.Entries, e => e.Kind == EntryKind.Message && e.Text == "accepted follow-up");
			await Task.WhenAll(scheduler.Tick(due)).WaitAsync(Deadline);
			Assert.True(Assert.Single(recipient.Schedules).Done);
		} finally {
			tool.Finish.TrySetResult();
			await manager.Restart.DrainAsync(TimeSpan.Zero, Deadline, () => { });
		}
	}

	sealed class FollowUp(Agent recipient) : ITool {
		public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public readonly TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public string Name => "follow_up";
		public string Description => "Test tool";
		public JsonObject Parameters => Schema.Object();
		public async Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
			Started.TrySetResult();
			await Finish.Task.WaitAsync(ct);
			recipient.Send(new Inbound("test tool", "accepted follow-up", SendMode.Queued));
			return new ToolOutput("saved");
		}
	}

	[Fact]
	public async Task PreexistingPausedQueueStaysBusyAndExpiryWakesParkedAdmissionsWithoutARequest() {
		using var dir = new TempDir();
		var backend = new FakeBackend();
		backend.Script.Enqueue(_ => FakeBackend.Say("resumed"));
		var manager = new AgentManager(new Home(dir.File("home")), new Config { Model = "fake-1" }, backend, new ToolRegistry());
		var paused = manager.CreateChat("paused", dir.Path);
		paused.SetPaused(true);
		paused.SendOperator("before seal", SendMode.Queued);
		try {
			Assert.Equal("busy", manager.Restart.BeginRelease(First, 1)?.State);
			var parked = manager.Restart.EnterTurnAsync().AsTask();
			Assert.False(parked.IsCompleted);
			using var resumed = await parked.WaitAsync(Deadline);
			Assert.NotNull(resumed);
			Assert.Null(manager.Restart.GetRelease(First));
			Assert.True(paused.IsPaused);
			Assert.Equal(1, paused.PendingCount);
			Assert.Empty(backend.Requests);
			paused.SetPaused(false);
			await Wait.Until(() => paused.Transcript.Last?.Kind == EntryKind.Idle);
			Assert.Contains(paused.Transcript.Entries, e => e.Kind == EntryKind.Message && e.Text == "before seal");
		} finally {
			await manager.Restart.DrainAsync(TimeSpan.Zero, Deadline, () => { });
		}
	}

	[Fact]
	public async Task AlreadyAdmittedNestedCallCanOutliveParentAndIsNotMistakenForReadiness() {
		var drain = new RestartDrain();
		using var run = new CancellationTokenSource();
		using var parent = drain.Enter();
		Assert.Equal("draining", drain.BeginRelease(First, 30)?.State);
		using var nested = drain.EnterNested(run.Token);
		Assert.NotNull(nested);
		run.Cancel();
		parent!.Dispose();
		Assert.Null(drain.EnterNested(run.Token));
		Assert.Equal(1, drain.GetRelease(First)?.ActiveOperations);
		nested!.Dispose();
		Assert.Equal("ready", drain.GetRelease(First)?.State);
		var parked = drain.EnterTurnAsync().AsTask();
		await drain.DrainAsync(TimeSpan.Zero, Deadline, () => { });
		Assert.Null(await parked.WaitAsync(Deadline));
		Assert.Null(drain.GetRelease(First));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task UnclaimedStartupRecoveryIsBusyUntilClaimedOrExplicitlyClosed(bool close) {
		using var dir = new TempDir();
		var home = new Home(dir.File("home"));
		var backend = new FakeBackend { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
		backend.Script.Enqueue(_ => FakeBackend.Say("recovered"));
		var id = RecoveryFixture(home, dir);
		new Transcript(System.IO.Path.Combine(home.AgentDir(id), "transcript")).Append(new Entry { Kind = EntryKind.Wake });
		var manager = new AgentManager(home, new Config { Model = "fake-1" }, backend, new ToolRegistry());
		// Seal before launching the loops so recovery is registered but cannot race
		// ahead of the assertion. Production always loads before accepting HTTP.
		Assert.Equal("ready", manager.Restart.BeginRelease(First, 30)?.State);
		manager.LoadAll();
		var recovered = manager.Get(id)!;
		try {
			Assert.Empty(backend.Requests);
			Assert.Equal("busy", manager.Restart.GetRelease(First)?.State);
			Assert.Equal(1, manager.Restart.GetRelease(First)?.PendingMessages);
			if(close) {
				recovered.SetClosed(true);
				Assert.Equal("ready", manager.Restart.GetRelease(First)?.State);
				Assert.True(recovered.Info.Closed);
				Assert.Empty(backend.Requests);
			} else {
				Assert.True(manager.Restart.AbortRelease(First));
				await Wait.Until(() => backend.Requests.Count == 1);
				var claimed = manager.Restart.BeginRelease(Second, 30);
				Assert.Equal("draining", claimed?.State);
				Assert.Equal(0, claimed?.PendingMessages);
				backend.Gate.SetResult();
				await Wait.Until(() => manager.Restart.GetRelease(Second)?.State == "ready");
				Assert.Equal(EntryKind.Idle, recovered.Transcript.Last?.Kind);
			}
		} finally {
			backend.Gate.TrySetResult();
			await manager.Restart.DrainAsync(TimeSpan.Zero, Deadline, () => { });
		}
	}

	[Fact]
	public async Task FailedRecoveryDoesNotOrphanADurableDependencyWakeRegistration() {
		using var dir = new TempDir();
		var home = new Home(dir.File("home"));
		var id = RecoveryFixture(home, dir);
		var agentDir = home.AgentDir(id);
		var tasksPath = System.IO.Path.Combine(agentDir, "tasks.json");
		const string wake = "durable dependency wake";
		JsonIO.Save(tasksPath, new List<TaskItem> { new() { Id = 1, Title = "fixture", AssignedBy = "Sera", PendingDependencyWakes = [wake] } });
		var obstructedSegment = System.IO.Path.Combine(agentDir, "transcript", "000001.jsonl");
		Directory.CreateDirectory(obstructedSegment); // Index can load; startup append fails.
		AgentManager Manager(FakeBackend backend) => new(home, new Config { Model = "fake-1" }, backend, new ToolRegistry());
		var failed = Manager(new FakeBackend());
		failed.LoadAll();
		Assert.Null(failed.Get(id));
		Assert.Equal(new[] { wake }, Assert.Single(JsonIO.Load(tasksPath, () => new List<TaskItem>())).PendingDependencyWakes);
		Assert.Equal("ready", failed.Restart.BeginRelease(First, 30)?.State);
		await failed.Restart.DrainAsync(TimeSpan.Zero, Deadline, () => { });

		Directory.Delete(obstructedSegment);
		var backend = new FakeBackend();
		backend.Script.Enqueue(_ => FakeBackend.Say("processed"));
		var retried = Manager(backend);
		try {
			retried.LoadAll();
			var recovered = retried.Get(id)!;
			await Wait.Until(() => recovered.Transcript.Last?.Kind == EntryKind.Idle);
			Assert.Single(recovered.Transcript.Entries, e => e.Kind == EntryKind.Message && e.Text == wake);
		} finally { await retried.Restart.DrainAsync(TimeSpan.Zero, Deadline, () => { }); }
	}

	static string RecoveryFixture(Home home, TempDir dir) {
		const string id = "release-recovery-fixture";
		var agentDir = home.AgentDir(id);
		Directory.CreateDirectory(agentDir);
		File.WriteAllText(System.IO.Path.Combine(agentDir, "system.md"), "Recovery test");
		JsonIO.Save(System.IO.Path.Combine(agentDir, "agent.json"), new AgentInfo { Id = id, Kind = AgentKind.Chat,
			Name = "Recovery test", Created = DateTimeOffset.UtcNow, WorkingDirectory = dir.Path });
		return id;
	}

	[Fact]
	public async Task BrowserCallbackCannotStartWhileReadyAndAdmittedExchangeCountsUntilCompletion() {
		using var dir = new TempDir();
		var drain = new RestartDrain();
		var handler = new HeldExchange();
		using var upstream = new HttpClient(handler);
		using var browser = new HttpClient { Timeout = Deadline };
		var auth = new ChatGptAuth(new Home(dir.File("home")), upstream);
		var url = new Uri(auth.BeginLogin(drain.Enter));
		var args = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(url.Query);
		var callback = args["redirect_uri"].ToString() + "?state=" + Uri.EscapeDataString(args["state"].ToString()) + "&code=fixture&client_id=fixture";
		try {
			Assert.Equal("ready", drain.BeginRelease(First, 30)?.State);
			using(var denied = await browser.GetAsync(callback)) {
				Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
				Assert.Equal(TimeSpan.FromSeconds(1), denied.Headers.RetryAfter?.Delta);
			}
			Assert.True(auth.SigningIn);
			Assert.False(handler.Entered.Task.IsCompleted);
			Assert.True(drain.AbortRelease(First));
			var accepted = browser.GetAsync(callback);
			await handler.Entered.Task.WaitAsync(Deadline);
			Assert.Equal("draining", drain.BeginRelease(Second, 30)?.State);
			handler.Finish.SetResult();
			using(var response = await accepted) Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
			await Wait.Until(() => drain.GetRelease(Second)?.State == "ready");
		} finally {
			handler.Finish.TrySetResult();
			auth.Logout();
		}
	}

	sealed class HeldExchange : HttpMessageHandler {
		public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public readonly TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
			Entered.TrySetResult();
			await Finish.Task.WaitAsync(ct);
			return new(HttpStatusCode.BadRequest);
		}
	}

	[Fact]
	public async Task RealRoutesAuthenticateBeforeAdmissionAndCountAnInFlightWriteUntilItSettles() {
		using var dir = new TempDir();
		var home = new Home(dir.File("home"));
		var pairing = new DevicePairing(home);
		var native = pairing.Redeem(pairing.Create()!.Code, "fixture")!;
		const string canonical = "paired.example";
		const string outside = "outside-fixture";
		var drain = new RestartDrain();
		var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var builder = WebApplication.CreateSlimBuilder();
		builder.Logging.ClearProviders();
		builder.WebHost.UseUrls("http://127.0.0.1:0");
		await using var app = builder.Build();
		Security? security = null;
		app.UseRouting();
		app.Use(next => ctx => security!.Middleware(next)(ctx));
		app.Use(next => ReleaseDrainEndpoints.GateWrites(drain, next));
		var api = app.MapGroup("/api");
		api.MapReleaseDrain(drain);
		api.MapGet("/status", () => Results.Ok()).AllowPaired();
		api.MapPost("/agents/fixture/messages", async () => {
			began.TrySetResult();
			await complete.Task;
			drain.ChangePendingMessages(1); // accepted by the endpoint after the seal
			return Results.Ok();
		}).AllowPaired();
		api.MapPost("/ext/messages", (HttpContext ctx) => ctx.Items["client"] is string ? Results.Ok() : Results.Forbid())
			.WithMetadata(new ReleaseDrainEndpoints.OutsideWrite());
		await app.StartAsync();
		var uri = new Uri(app.Urls.Single());
		security = new Security(home, new Config { Port = uri.Port, PairingOrigin = "https://" + canonical, RemoteHosts = ["alias.example"] }) {
			Pairing = pairing, ClientAuth = t => t == outside ? t : null,
		};
		var op = security.Token;
		using var client = new HttpClient { BaseAddress = uri, Timeout = Deadline };
		var pid = Environment.ProcessId;
		const string composer = "/api/agents/fixture/messages";
		async Task<HttpResponseMessage> Send(HttpMethod method, string path, string? token = null,
			object? body = null, string? host = null, string? id = null, int? reportedPid = null, bool forwarded = false) {
			using var request = new HttpRequestMessage(method, path);
			if(token != null) request.Headers.Authorization = new("Bearer", token);
			if(host != null) request.Headers.Host = host;
			if(body != null) request.Content = body is string raw ? new StringContent(raw, System.Text.Encoding.UTF8, "application/json") : JsonContent.Create(body);
			if(id != null) request.Headers.Add("X-Marten-Drain-Id", id);
			if(reportedPid != null) request.Headers.Add("X-Marten-Drain-Pid", reportedPid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
			if(forwarded) request.Headers.Add("Forwarded", "for=192.0.2.1");
			return await client.SendAsync(request);
		}
		async Task<RestartDrain.ReleaseStatus?> Check(Task<HttpResponseMessage> req, HttpStatusCode code, bool expectStatus = false) {
			using var response = await req;
			Assert.Equal(code, response.StatusCode);
			Assert.True(response.Headers.CacheControl?.NoStore);
			return expectStatus ? await response.Content.ReadFromJsonAsync<RestartDrain.ReleaseStatus>() : null;
		}
		await Check(Send(HttpMethod.Get, Path), HttpStatusCode.Forbidden);
		await Check(Send(HttpMethod.Get, Path + "?token=" + op), HttpStatusCode.Forbidden);
		await Check(Send(HttpMethod.Get, Path, op, forwarded: true), HttpStatusCode.Forbidden);
		await Check(Send(HttpMethod.Get, Path, op, host: "alias.example"), HttpStatusCode.Forbidden);
		await Check(Send(HttpMethod.Get, Path, native, host: canonical), HttpStatusCode.Forbidden);
		await Check(Send(HttpMethod.Get, Path, outside), HttpStatusCode.Forbidden);
		await Check(Send(HttpMethod.Get, Path + "/", op), HttpStatusCode.NotFound);
		await Check(Send(HttpMethod.Get, Path, op), HttpStatusCode.BadRequest);
		await Check(Send(HttpMethod.Post, Path, op, new { id = "INVALID", pid, leaseSeconds = 20 }), HttpStatusCode.BadRequest);
		await Check(Send(HttpMethod.Post, Path, op, new { id = First, pid, leaseSeconds = 301 }), HttpStatusCode.BadRequest);
		await Check(Send(HttpMethod.Post, Path, op, $$"""{"id":"{{Second}}","id":"{{First}}","pid":{{pid}},"leaseSeconds":20}"""), HttpStatusCode.BadRequest);
		await Check(Send(HttpMethod.Post, Path, op, new { id = First, pid = pid + 1, leaseSeconds = 20 }), HttpStatusCode.Conflict);

		var earlier = Send(HttpMethod.Post, composer, op);
		try {
			await began.Task.WaitAsync(Deadline);
			var body = new { id = First, pid, leaseSeconds = 30 };
			var initial = (await Check(Send(HttpMethod.Post, Path, op, body), HttpStatusCode.OK, true))!;
			Assert.Equal(First, initial.Id);
			Assert.Equal(pid, initial.Pid);
			Assert.Equal("draining", initial.State);
			Assert.Equal(1, initial.ActiveOperations);
			Assert.InRange(initial.RemainingMs, 1, 30_000);
			await Check(Send(HttpMethod.Post, Path, op, body), HttpStatusCode.Conflict);
			await Check(Send(HttpMethod.Delete, Path, op, id: Second, reportedPid: pid), HttpStatusCode.Conflict);
			await Check(Send(HttpMethod.Delete, Path, op, id: First, reportedPid: pid + 1), HttpStatusCode.Conflict);
			foreach(var (path, token, host) in new[] { (composer, op, (string?)null), (composer, native, canonical), ("/api/ext/messages", outside, null) }) {
				using var rejected = await Send(HttpMethod.Post, path, token, host: host);
				Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
				Assert.True(rejected.Headers.CacheControl?.NoStore);
				Assert.Equal(TimeSpan.FromSeconds(1), rejected.Headers.RetryAfter?.Delta);
			}
			using var unauth = await Send(HttpMethod.Post, composer);
			Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);
			using var read = await Send(HttpMethod.Get, "/api/status", native, host: canonical);
			Assert.Equal(HttpStatusCode.OK, read.StatusCode);
		} finally { complete.TrySetResult(); }
		using var accepted = await earlier;
		Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
		var busy = (await Check(Send(HttpMethod.Get, Path, op, id: First, reportedPid: pid), HttpStatusCode.OK, true))!;
		Assert.Equal("busy", busy.State);
		Assert.Equal(1, busy.PendingMessages);
		await Check(Send(HttpMethod.Delete, Path, op, id: First, reportedPid: pid), HttpStatusCode.NoContent);
		using(var resumed = await Send(HttpMethod.Post, "/api/ext/messages", outside)) Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
		drain.ChangePendingMessages(-1);
		var next = (await Check(Send(HttpMethod.Post, Path, op, new { id = Second, pid, leaseSeconds = 30 }), HttpStatusCode.OK, true))!;
		Assert.Equal("ready", next.State);
		Assert.Equal(0, next.ActiveOperations);
		await Check(Send(HttpMethod.Delete, Path, op, id: First, reportedPid: pid), HttpStatusCode.Conflict);
		await Check(Send(HttpMethod.Delete, Path, op, id: Second, reportedPid: pid), HttpStatusCode.NoContent);
	}
}
