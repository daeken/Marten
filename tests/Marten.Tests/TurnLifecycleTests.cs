using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Marten.Agents;
using Marten.Models;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Tests;

public class TurnLifecycleTests {
	static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
	static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
	static void Tell(Agent a, string text) => a.Send(new Inbound("Sera", text, SendMode.Queued));
	static bool Idle(Agent a) => a.Status == AgentStatus.Idle && a.Transcript.Last?.Kind == EntryKind.Idle;
	static ModelResult Batch => new([.. FakeBackend.Call("first", "held", "{}").Items,
		.. FakeBackend.Call("second", "held", "{}").Items], null);

	[Fact]
	public async Task CloseCancelsStuckModelReleasesSlotsAndFencesLateOutputAfterReopen() {
		using var dir = new TempDir();
		var entered = Signal();
		var cancelled = Signal();
		var release = Signal();
		var returned = Signal();
		var backend = new Backend(async (_, delta, ct) => {
			using var registration = ct.Register(() => cancelled.TrySetResult());
			entered.SetResult();
			await release.Task; // Deliberately ignores cancellation.
			delta("stale delta");
			returned.SetResult();
			return new([.. FakeBackend.Say("stale reply").Items, .. Batch.Items], null);
		});
		var tool = new HeldTool();
		var (m, project, parent, agent) = Create(dir, backend, tool, new Config {
			Model = "fake-1", MaxRunningAgents = 1, MaxConcurrentModelCalls = 1,
		});
		var deltas = new ConcurrentQueue<string>();
		m.Events += ev => {
			if(ev.GetType().GetProperty("type")?.GetValue(ev) as string == "delta")
				deltas.Enqueue((string) ev.GetType().GetProperty("text")!.GetValue(ev)!);
		};
		agent.Send(Inbound.FromAgent(parent, "old wake", SendMode.Queued));
		await entered.Task.WaitAsync(Timeout);
		await new CloseAgentTool(() => m).InvokeAsync(parent, new JsonObject { ["agent"] = agent.Info.Id }, default);
		await cancelled.Task.WaitAsync(Timeout);
		await Wait.Until(() => Idle(agent));
		Assert.True(agent.Info.Closed);
		Assert.Equal("interrupted", agent.Transcript.Last!.Reason);
		Assert.Equal(ModelCallOutcome.Cancelled, agent.ModelCall!.Outcome);

		// Reopen alone doesn't replay the old wake. A deliberate new message starts a new turn
		// even though the abandoned completion is still outstanding.
		agent.SetClosed(false);
		agent.SetClosed(true);
		agent.Send(Inbound.FromAgent(parent, "new wake", SendMode.Immediate));
		Assert.False(agent.Info.Closed);
		await Wait.Until(() => Idle(agent) && agent.Transcript.Entries.Any(e => e.Text == "fresh reply"));
		release.SetResult();
		await returned.Task.WaitAsync(Timeout);
		Assert.Equal(2, backend.Calls);
		Assert.Equal(2, agent.Transcript.Entries.Count(e => e.Kind == EntryKind.Wake));
		Assert.DoesNotContain(agent.Transcript.Entries, e => e.Text == "stale reply" || e.Kind == EntryKind.ToolCall);
		Assert.Equal(0, tool.Calls);
		Assert.Empty(deltas);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task PausedReplyCommitsOnlyOnResumeUnlessClosed(bool close) {
		using var dir = new TempDir();
		var entered = Signal();
		var release = Signal();
		var backend = new Backend(async (_, delta, _) => {
			entered.SetResult();
			await release.Task;
			delta("paused delta");
			return new([.. FakeBackend.Say("complete buffered reply").Items, .. Batch.Items], null);
		});
		var tool = new HeldTool();
		var (m, _, _, agent) = Create(dir, backend, tool, new Config {
			Model = "fake-1", MaxRunningAgents = 1, MaxConcurrentModelCalls = 1,
		});
		var deltas = new ConcurrentQueue<object>();
		m.Events += ev => {
			if(ev.GetType().GetProperty("type")?.GetValue(ev) as string == "delta") deltas.Enqueue(ev);
		};
		Tell(agent, "old wake");
		await entered.Task.WaitAsync(Timeout);
		agent.SetPaused(true);
		release.SetResult();
		await Wait.Until(() => agent.Status == AgentStatus.Paused);
		Assert.Equal(ModelCallOutcome.Succeeded, agent.ModelCall!.Outcome);
		Assert.DoesNotContain(agent.Transcript.Entries, e => e.Kind is EntryKind.Assistant or EntryKind.ToolCall or EntryKind.ToolResult);
		Assert.Equal(0, tool.Calls);
		Assert.Empty(deltas);

		// Both slots must be available while the completed reply is buffered.
		var other = m.CreateChat("other", dir.Path);
		Tell(other, "take both slots");
		await Wait.Until(() => Idle(other));
		Assert.Equal(2, backend.Calls);
		if(close) {
			agent.SetClosed(true);
			Tell(agent, "new wake");
		}
		tool.Release.SetResult();
		agent.SetPaused(false);
		await Wait.Until(() => Idle(agent) && agent.Transcript.Entries.Any(e => e.Text == "fresh reply"));
		Assert.Equal(3, backend.Calls);
		Assert.Equal(close ? 0 : 2, tool.Calls);
		Assert.Equal(!close, agent.Transcript.Entries.Any(e => e.Text == "complete buffered reply"));
		Assert.Equal(close, agent.Transcript.Entries.Any(e => e.Kind == EntryKind.Idle && e.Reason == "interrupted"));
	}

	[Fact]
	public async Task LoopCheckDoesNotPauseAgentsOrParkTheRemainingToolBatch() {
		using var dir = new TempDir();
		var tool = new HeldTool();
		var backend = new FakeBackend { Gate = Signal() };
		var (m, _, parent, agent) = Create(dir, backend, tool, new Config { Model = "fake-1", LoopMessages = 4 });
		m.Tools.Add(new SendMessageTool(() => m));
		m.Tools.Add(new RespondMessageLoopTool(() => m));
		var send = System.Text.Json.JsonSerializer.Serialize(new { to = parent.Info.Id, text = "trip the loop breaker" });
		backend.Script.Enqueue(_ => new([.. FakeBackend.Call("message", "send_message", send).Items, .. Batch.Items], null));
		backend.Script.Enqueue(_ => FakeBackend.Say("finished"));
		Tell(agent, "go");
		await Wait.Until(() => backend.Requests.Count == 1);
		for(var i = 0; i < 3; i++) {
			var (from, to) = i % 2 == 0 ? (parent, agent) : (agent, parent);
			Assert.Null(m.Loops.Admit(from.Info.Id, to.Info.Id));
		}
		tool.Release.SetResult();
		backend.Gate.SetResult();
		await Wait.Until(() => Idle(agent));
		var result = Assert.Single(agent.Transcript.Entries, e => e.Kind == EntryKind.ToolResult && e.CallId == "message");
		Assert.Contains("loop_check", result.Text);
		Assert.False(parent.IsPaused || agent.IsPaused);
		Assert.Equal(2, tool.Calls);
		Assert.Empty(m.AgentMessages());
		Assert.DoesNotContain(parent.Transcript.Entries, e => e.Kind == EntryKind.Wake);
		Assert.All(backend.Requests, request => Assert.Contains(request.Tools, spec => spec.Name == "respond_message_loop"));
	}

	[Fact]
	public async Task ApprovedDelegatedRetryRechecksAdmissionAfterGuardEvenAcrossReopen() {
		using var dir = new TempDir();
		var tool = new HeldTool();
		var backend = new FakeBackend();
		var (m, _, parent, agent) = Create(dir, backend, tool, new Config {
			Model = "fake-1", Rules = [new GuardRule { Tool = "held", Action = GuardAction.Ask }],
		});
		m.Tools.Add(new CallToolTool());
		const string args = """{"name":"held","arguments":{}}""";
		backend.Script.Enqueue(_ => FakeBackend.Call("ask", "call_tool", args));
		backend.Script.Enqueue(_ => FakeBackend.Say("waiting for approval"));
		backend.Script.Enqueue(_ => FakeBackend.Call("retry", "call_tool", args));
		backend.Script.Enqueue(_ => FakeBackend.Say("fresh turn after close"));
		Tell(agent, "ask to run");
		await Wait.Until(() => Idle(agent) && backend.Requests.Count == 2);
		var card = Assert.Single(m.SeraInbox(), e => e.Kind == "approval");
		agent.SetClosed(true);
		m.AnswerQuestion(card.Id, 0, null);
		Assert.True(agent.Info.Closed);
		Assert.Equal(0, tool.Calls);

		// Force the race after the approved inner guard check, before the real tool delegate.
		m.Tools.Gate = (ctx, candidate, arguments) => {
			var answer = m.Guard.Check(ctx, candidate, arguments);
			if(candidate == tool && answer == null) {
				agent.SetClosed(true);
				agent.Send(Inbound.FromAgent(parent, "fresh wake", SendMode.Immediate));
			}
			return answer;
		};
		agent.Send(Inbound.FromAgent(parent, "retry approved call", SendMode.Queued));
		await Wait.Until(() => Idle(agent) && agent.Transcript.Entries.Any(e => e.Text == "fresh turn after close"));
		Assert.Equal(0, tool.Calls);
		var retry = Assert.Single(agent.Transcript.Entries, e => e.Kind == EntryKind.ToolResult && e.CallId == "retry");
		Assert.Contains("closed", retry.Text);
	}

	[Fact]
	public async Task CloseAndImmediateReopenDuringAToolFencesTheRestOfTheBatch() {
		using var dir = new TempDir();
		var tool = new HeldTool();
		var backend = new Backend((_, _, _) => Task.FromResult(Batch));
		var (_, _, parent, agent) = Create(dir, backend, tool);
		Tell(agent, "old wake");
		await tool.Started.Task.WaitAsync(Timeout);
		agent.SetClosed(true);
		agent.Send(Inbound.FromAgent(parent, "new wake", SendMode.Immediate));
		await tool.Cancelled.Task.WaitAsync(Timeout);
		tool.Release.SetResult();
		await Wait.Until(() => Idle(agent) && agent.Transcript.Entries.Any(e => e.Text == "fresh reply"));
		Assert.Equal(1, tool.Calls);
		var entries = agent.Transcript.Entries;
		Assert.All(entries.Where(e => e.Kind == EntryKind.ToolResult), e => {
			Assert.True(e.IsError);
			Assert.Contains("closed", e.Text);
		});
		Assert.Equal(new[] { "first", "second" }, entries.Where(e => e.Kind == EntryKind.ToolResult).Select(e => e.CallId));
		var stopped = Assert.Single(entries, e => e.Kind == EntryKind.Idle && e.Reason == "interrupted");
		Assert.True(stopped.Seq < Assert.Single(entries, e => e.Text == "new wake").Seq);
	}

	[Theory]
	[InlineData("agent")]
	[InlineData("project")]
	[InlineData("global")]
	public async Task EffectivePauseDuringAToolRecordsItsResultAndParksTheRemainingBatch(string scope) {
		using var dir = new TempDir();
		var tool = new HeldTool();
		var backend = new Backend((_, _, _) => Task.FromResult(Batch));
		var (m, p, _, agent) = Create(dir, backend, tool, new Config { Model = "fake-1", MaxRunningAgents = 1 });
		Action<bool> pause = scope switch {
			"agent" => agent.SetPaused,
			"project" => paused => m.SetProjectPaused(p, paused),
			_ => m.SetPausedAll,
		};
		Tell(agent, "go");
		await tool.Started.Task.WaitAsync(Timeout);
		Assert.Contains("active", agent.StartFreshContext());
		pause(true);
		tool.Release.SetResult();
		await Wait.Until(() => agent.Status == AgentStatus.Paused);
		Assert.Contains("active", agent.StartFreshContext());
		var first = Assert.Single(agent.Transcript.Entries, e => e.Kind == EntryKind.ToolResult);
		Assert.Equal("first", first.CallId);
		Assert.Equal("finished", first.Text);
		Assert.Equal(1, tool.Calls);
		Assert.Equal(1, backend.Calls);
		Assert.False(tool.Cancelled.Task.IsCompleted);
		pause(false);
		await Wait.Until(() => Idle(agent));
		Assert.Equal(2, tool.Calls);
		Assert.Equal(2, backend.Calls);
		Assert.Equal(new[] { "first", "second" }, agent.Transcript.Entries.Where(e => e.Kind == EntryKind.ToolResult).Select(e => e.CallId));
		Assert.Single(agent.Transcript.Entries, e => e.Kind == EntryKind.Wake);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task CloseCancelsSlotWait(bool agentSlot) {
		using var dir = new TempDir();
		var backend = new FakeBackend { Gate = Signal() };
		backend.Script.Enqueue(_ => FakeBackend.Say("unblocked"));
		var m = new AgentManager(new Home(dir.File("home")), new Config {
			Model = "fake-1", MaxRunningAgents = agentSlot ? 1 : 0, MaxConcurrentModelCalls = agentSlot ? 0 : 1,
		}, backend, new ToolRegistry());
		var holder = m.CreateChat("holder", dir.Path);
		Tell(holder, "hold the slot");
		await Wait.Until(() => backend.Requests.Count == 1);
		var waiting = m.CreateChat("waiting", dir.Path);
		Tell(waiting, "wait for the slot");
		await Wait.Until(() => waiting.Status == AgentStatus.WaitingForSlot);
		Assert.Contains("active", waiting.StartFreshContext());
		waiting.SetClosed(true);
		await Wait.Until(() => Idle(waiting));
		Assert.Equal("interrupted", waiting.Transcript.Last!.Reason);
		backend.Gate.SetResult();
		await Wait.Until(() => Idle(holder));
		Assert.Single(backend.Requests);
	}

	static (AgentManager Manager, Marten.Projects.Project Project, Agent Parent, Agent Worker) Create(
		TempDir dir, IModelBackend backend, HeldTool tool, Config? config = null) {
		var m = new AgentManager(new Home(dir.File("home")), config ?? new Config { Model = "fake-1" }, backend, new ToolRegistry().Add(tool));
		var (p, parent) = m.CreateProject("p", [dir.Path], null, false);
		return (m, p, parent, m.SpawnTask(parent, "worker", false, null, null));
	}

	sealed class Backend(Func<ModelRequest, Action<string>, CancellationToken, Task<ModelResult>> first) : IModelBackend {
		public int Calls;
		public string Provider => "fake";
		public bool Connected => true;
		public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => throw new InvalidOperationException();
		public Task<ModelResult> CompleteAsync(ModelRequest request, Action<string> onTextDelta, CancellationToken ct) =>
			Interlocked.Increment(ref Calls) == 1 ? first(request, onTextDelta, ct) : Task.FromResult(FakeBackend.Say("fresh reply"));
	}

	sealed class HeldTool : ITool {
		public readonly TaskCompletionSource Started = Signal();
		public readonly TaskCompletionSource Release = Signal();
		public readonly TaskCompletionSource Cancelled = Signal();
		public int Calls;
		public string Name => "held";
		public string Description => "Wait for release";
		public JsonObject Parameters => Schema.Object();
		public async Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
			Interlocked.Increment(ref Calls);
			using var registration = ct.Register(() => Cancelled.TrySetResult());
			Started.TrySetResult();
			await Release.Task;
			return new("finished");
		}
	}
}
