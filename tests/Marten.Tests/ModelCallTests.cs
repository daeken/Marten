using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Text.Json.Nodes;
using Marten.Agents;
using Marten.Models;
using Marten.Models.OpenAI;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Tests;

public class ModelCallTests {
	sealed class Backend(Func<ModelRequest, CancellationToken, Task<ModelResult>> complete, string provider = "fake") : IModelBackend {
		public string Provider => provider;
		public bool Connected => true;
		public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => throw new Exception("Explicit settings shouldn't need the catalog.");
		public Task<ModelResult> CompleteAsync(ModelRequest request, Action<string> onTextDelta, CancellationToken ct) => complete(request, ct);
	}

	static AgentManager Manager(TempDir dir, IModelBackend backend, Func<TimeSpan, CancellationToken, Task>? delay = null) =>
		new(new Home(dir.File("home")), new Config { Model = "good" }, backend, new ToolRegistry()) {
			ModelRetryDelay = delay ?? Task.Delay,
		};

	static void Wake(Agent agent) => agent.Send(new Inbound("Sera", "go", SendMode.Queued));
	static bool Finished(Agent agent) => agent.Status == AgentStatus.Idle && agent.Transcript.Last?.Kind == EntryKind.Idle;

	static ConcurrentQueue<(string Type, ModelCallStatus Call)> WatchCalls(AgentManager manager, Agent agent) {
		var calls = new ConcurrentQueue<(string, ModelCallStatus)>();
		manager.Events += ev => {
			var type = ev.GetType();
			if(type.GetProperty("agentId")?.GetValue(ev) as string == agent.Info.Id &&
				type.GetProperty("modelCall")?.GetValue(ev) is ModelCallStatus call)
				calls.Enqueue(((string) type.GetProperty("type")!.GetValue(ev)!, call));
		};
		return calls;
	}

	[Theory]
	[InlineData("model_not_found", 1)]
	[InlineData("exhaustion", 6)]
	[InlineData("unexpected", 1)]
	public async Task TerminalErrorWakesParentAndEachOutstandingAssignerOnce(string failure, int attempts) {
		using var dir = new TempDir();
		var backoffs = new ConcurrentQueue<double>();
		var backend = new Backend((request, _) => request.Model == "good" ? Task.FromResult(FakeBackend.Say("ack")) : failure switch {
			"model_not_found" => MissingModel(),
			"exhaustion" => throw new TransientModelException("provider down"),
			_ => throw new IOException("unexpected failure"),
		});
		var manager = Manager(dir, backend, (delay, _) => { backoffs.Enqueue(delay.TotalSeconds); return Task.CompletedTask; });
		var (_, parent) = manager.CreateProject("p", [dir.Path], null, false);
		var worker = manager.SpawnTask(parent, "worker", false, "bad", null);
		var assigner = manager.SpawnTask(parent, "assigner", false, null, null);
		var completedAssigner = manager.SpawnTask(parent, "completed assigner", false, null, null);
		worker.AddTask("from parent", parent.Info.Id);
		worker.AddTask("from other agent", assigner.Info.Id);
		var waiting = worker.AddTask("also from other agent", assigner.Info.Id);
		worker.UpdateTask(waiting.Id, TaskState.Waiting, "waiting");
		worker.UpdateTask(worker.AddTask("done", completedAssigner.Info.Id).Id, TaskState.Done, "done");
		worker.AddTask("self", worker.Info.Id);
		worker.AddTask("missing", "missing-id");
		var before = worker.Tasks;
		var calls = WatchCalls(manager, worker);

		Wake(worker);
		await Wait.Until(() => worker.Status == AgentStatus.Error && Finished(parent) && Finished(assigner));
		var error = Assert.Single(worker.Transcript.Entries, e => e.Kind == EntryKind.Error);
		Assert.Contains(failure switch { "exhaustion" => "provider down", "unexpected" => "unexpected failure", _ => failure }, error.Text);
		Assert.Equal(EntryKind.Idle, worker.Transcript.Last!.Kind);
		Assert.Equal(before, worker.Tasks);
		Assert.Empty(completedAssigner.Transcript.Entries);
		Assert.Equal(0, worker.PendingCount);
		var notifications = manager.AgentMessages(worker.Info.Id);
		Assert.Equal(2, notifications.Count);
		foreach(var recipient in new[] { parent, assigner }) {
			Assert.Single(notifications, m => m.ToId == recipient.Info.Id && m.Mode == SendMode.Queued);
			Assert.Equal("turn_error", Assert.Single(recipient.Transcript.Entries, e => e.Kind == EntryKind.Wake).Reason);
			Assert.Single(recipient.Transcript.Entries, e => e.Kind == EntryKind.Message && e.Text!.Contains(worker.StatusDetail!));
		}
		Assert.Equal(failure == "exhaustion" ? [2d, 4, 8, 16, 32] : Array.Empty<double>(), backoffs.ToArray());
		Assert.Equal(attempts * 2, calls.Count);
		foreach(var pair in calls.Chunk(2)) AssertAttempt(pair, ModelCallOutcome.Failed);
		var lastFailure = JsonIO.Deserialize<ModelAttemptFailure>(File.ReadAllText(Path.Combine(worker.Directory, Agent.ModelFailureFile)))!;
		Assert.Equal(attempts, lastFailure.AttemptId);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("AwaitingHeaders")]
	[InlineData("ReadingBody")]
	public async Task DisposalRemainsTerminalAndDurableWithoutRetainingRequestData(string? phaseName) {
		ModelTransportPhase? phase = phaseName == null ? null : Enum.Parse<ModelTransportPhase>(phaseName);
		using var dir = new TempDir();
		const string marker = "diagnostic-redaction-test-only";
		using var tls = new SslStream(new MemoryStream());
		using var http = ModelHttp.CreateClient(new FailureHandler(phase, tls, marker), TimeSpan.FromSeconds(5), () => TimeSpan.FromSeconds(5));
		var attempts = 0;
		var backend = new Backend(async (_, ct) => {
			Interlocked.Increment(ref attempts);
			if(phase == null) throw new ObjectDisposedException(marker);
			using var request = new HttpRequestMessage(HttpMethod.Get, $"https://example.invalid/{marker}");
			request.Headers.Add("X-Test", marker);
			using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
			using var body = await response.Content.ReadAsStreamAsync(ct);
			tls.Dispose();
			try {
				await body.ReadExactlyAsync(new byte[1], ct);
			} catch(ObjectDisposedException e) {
				// The transport annotation should still be visible through a backend wrapper.
				throw new InvalidOperationException(marker, e);
			}
			throw new InvalidOperationException("Expected disposal.");
		}, "openai");
		var manager = Manager(dir, new BackendRouter(new Backend((_, _) => throw new Exception("wrong route")), [(backend, [])]));
		var agent = manager.CreateChat("redaction", dir.Path);
		agent.UpdateSettings($"openai/{marker}", null);
		agent.Send(new Inbound("Sera", marker, SendMode.Queued));
		await Wait.Until(() => agent.Status == AgentStatus.Error);
		Assert.Equal(1, attempts);
		var path = Path.Combine(agent.Directory, Agent.ModelFailureFile);
		var json = File.ReadAllText(path);
		Assert.DoesNotContain(marker, json);
		var record = JsonIO.Deserialize<ModelAttemptFailure>(json)!;
		Assert.Equal(agent.ModelCall!.Id, record.AttemptId);
		Assert.Equal(agent.ModelCall.StartedAt, record.StartedAt);
		Assert.Equal(agent.ModelCall.EndedAt, record.EndedAt);
		Assert.NotEqual(Guid.Empty, record.RunId);
		Assert.Equal(ModelFailureBackend.Unknown, record.Backend);
		Assert.Null(record.Model);
		Assert.Equal(phase, record.TransportPhase);
		Assert.False(record.AttemptCancelled || record.TurnCancelled || record.ShuttingDown);
		Assert.Equal(phase == ModelTransportPhase.ReadingBody ? ["other", nameof(ObjectDisposedException)] : [nameof(ObjectDisposedException)], record.Causes);
		Assert.Single(agent.Transcript.Entries, e => e.Kind == EntryKind.Error);
	}

	sealed class FailureHandler(ModelTransportPhase? phase, SslStream tls, string marker) : HttpMessageHandler {
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
			phase == ModelTransportPhase.AwaitingHeaders ? throw new ObjectDisposedException(marker)
				: Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(tls) });
	}

	[Theory]
	[InlineData("gpt-6.2-sol", "gpt-6.2-sol")]
	[InlineData("not-allowlisted-test-model", null)]
	public async Task RecordsOnlyAllowlistedModelFromActualRoutedBackend(string model, string? expected) {
		using var dir = new TempDir();
		using var tls = new SslStream(new MemoryStream());
		using var http = ModelHttp.CreateClient(new FailureHandler(ModelTransportPhase.AwaitingHeaders, tls, "test-disposal"),
			TimeSpan.FromSeconds(5), () => TimeSpan.FromSeconds(5));
		var home = new Home(dir.File("home"));
		JsonIO.Save(Path.Combine(home.Secrets, "openai.json"), new ChatGptAuth.Credentials {
			HostId = "test", AccessToken = "test-token", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(),
		});
		var openai = new ResponsesBackend(new ChatGptAuth(home, http), http);
		var router = new BackendRouter(new Backend((_, _) => throw new Exception("wrong route")), [(openai, [])]);
		var manager = new AgentManager(home, new Config { Model = $"openai/{model}" }, router, new ToolRegistry());
		var agent = manager.CreateChat("routed failure", dir.Path);
		Wake(agent);
		await Wait.Until(() => agent.Status == AgentStatus.Error);
		var json = File.ReadAllText(Path.Combine(agent.Directory, Agent.ModelFailureFile));
		var failure = JsonIO.Deserialize<ModelAttemptFailure>(json)!;
		Assert.Equal(ModelFailureBackend.OpenAI, failure.Backend);
		Assert.Equal(expected, failure.Model);
		Assert.Equal(ModelTransportPhase.AwaitingHeaders, failure.TransportPhase);
		Assert.DoesNotContain("test-token", json);
	}

	[Fact]
	public async Task DiagnosticWriteFailureDoesNotReplaceTheOriginalFailure() {
		using var dir = new TempDir();
		var manager = Manager(dir, new Backend((_, _) => throw new ObjectDisposedException("original")));
		var agent = manager.CreateChat("broken sidecar", dir.Path);
		Directory.CreateDirectory(Path.Combine(agent.Directory, Agent.ModelFailureFile));
		Wake(agent);
		await Wait.Until(() => agent.Status == AgentStatus.Error);
		Assert.Equal(new ObjectDisposedException("original").Message, agent.StatusDetail);
		Assert.False(File.Exists(Path.Combine(agent.Directory, Agent.ModelFailureFile + ".tmp")));
	}

	static Task<ModelResult> MissingModel() {
		var stream = new ResponsesBackend.ResponseStream();
		stream.Feed((JsonObject) JsonNode.Parse("""{"type":"response.failed","response":{"error":{"code":"model_not_found"}}}""")!);
		return Task.FromResult(stream.Finish("bad"));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task InterruptDuringCallOrBackoffDoesNotReportATerminalFailure(bool inBackoff) {
		using var dir = new TempDir();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		static async Task Hang(CancellationToken ct) => await Task.Delay(Timeout.InfiniteTimeSpan, ct);
		var backend = new Backend(async (_, ct) => {
			if(inBackoff) throw new RateLimitedException("quota", TimeSpan.FromSeconds(45));
			entered.TrySetResult();
			await Hang(ct);
			return FakeBackend.Say("should not happen");
		});
		var manager = Manager(dir, backend, async (_, ct) => { entered.TrySetResult(); await Hang(ct); });
		var (_, parent) = manager.CreateProject("p", [dir.Path], null, false);
		var worker = manager.SpawnTask(parent, "worker", false, null, null);
		worker.AddTask("work", parent.Info.Id);
		var calls = WatchCalls(manager, worker);

		Wake(worker);
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Equal(inBackoff ? AgentStatus.WaitingForQuota : AgentStatus.Running, worker.Status);
		Assert.Equal(inBackoff, worker.ModelCall!.EndedAt.HasValue);
		worker.Interrupt();
		await Wait.Until(() => Finished(worker));
		Assert.Equal("interrupted", worker.Transcript.Last!.Reason);
		Assert.DoesNotContain(worker.Transcript.Entries, e => e.Kind == EntryKind.Error);
		Assert.Empty(manager.AgentMessages(worker.Info.Id));
		AssertAttempt(calls.ToArray(), inBackoff ? ModelCallOutcome.Failed : ModelCallOutcome.Cancelled);
		Assert.Equal(inBackoff, File.Exists(Path.Combine(worker.Directory, Agent.ModelFailureFile)));
	}

	[Fact]
	public async Task OverflowCompactionRetriesQuotaAndThenReplySucceeds() {
		using var dir = new TempDir();
		var step = 0;
		var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var manager = Manager(dir, new Backend((_, _) => step++ switch {
			0 => throw new ContextFullException(),
			1 => throw new RateLimitedException("wait", TimeSpan.FromSeconds(40)),
			2 => Task.FromResult(FakeBackend.Say("summary")),
			_ => Task.FromResult(FakeBackend.Say("reply")),
		}), (_, ct) => resume.Task.WaitAsync(ct));
		var (_, parent) = manager.CreateProject("p", [dir.Path], null, false);
		var agent = manager.SpawnTask(parent, "worker", false, null, null);
		var calls = WatchCalls(manager, agent);
		Wake(agent);
		await Wait.Until(() => agent.Status == AgentStatus.WaitingForQuota);
		Assert.Equal(ModelCallKind.Compaction, agent.ModelCall!.Kind);
		Assert.Equal(ModelCallOutcome.Failed, agent.ModelCall.Outcome);
		Assert.Empty(manager.AgentMessages(agent.Info.Id));
		resume.SetResult();
		await Wait.Until(() => Finished(agent));
		Assert.Equal(4, step);
		var attempts = calls.Chunk(2).ToArray();
		Assert.Collection(attempts,
			p => AssertAttempt(p, ModelCallOutcome.Failed, ModelCallKind.Reply),
			p => AssertAttempt(p, ModelCallOutcome.Failed, ModelCallKind.Compaction),
			p => AssertAttempt(p, ModelCallOutcome.Succeeded, ModelCallKind.Compaction),
			p => AssertAttempt(p, ModelCallOutcome.Succeeded, ModelCallKind.Reply));
		Assert.Equal(attempts[^1][1].Call, agent.ModelCall);
		var failure = JsonIO.Load<ModelAttemptFailure?>(Path.Combine(agent.Directory, Agent.ModelFailureFile), () => null);
		Assert.NotNull(failure);
		Assert.Equal(attempts[1][1].Call.Id, failure.AttemptId);
		Assert.Equal(ModelCallKind.Compaction, failure.Kind);
		Assert.Equal(nameof(RateLimitedException), Assert.Single(failure.Causes));
		Assert.Empty(manager.AgentMessages(agent.Info.Id));
	}

	static void AssertAttempt((string Type, ModelCallStatus Call)[] pair, ModelCallOutcome outcome, ModelCallKind kind = ModelCallKind.Reply) {
		Assert.Equal(new[] { "model_call_start", "model_call_end" }, pair.Select(ev => ev.Type));
		var start = pair[0].Call;
		var end = pair[1].Call;
		Assert.Equal(kind, start.Kind);
		Assert.Null(start.EndedAt);
		Assert.Null(start.Outcome);
		Assert.Equal(outcome, end.Outcome);
		Assert.True(end.EndedAt >= start.StartedAt);
		Assert.Equal(start, end with { EndedAt = null, Outcome = null });
	}
}
