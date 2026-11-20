using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Marten.Agents;
using Marten.Models;
using Marten.Storage;
using Marten.Tools;
using Microsoft.Extensions.Time.Testing;

namespace Marten.Tests;

public class NetworkRecoveryTests {
	static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(5);
	static ProviderUnreachableException Offline() => new("test provider is unreachable");
	static void Wake(Agent agent) => agent.Send(new Inbound("Sera", "go", SendMode.Queued));
	static bool Ended(Agent a) => a.Status == AgentStatus.Idle && a.Transcript.Last?.Kind == EntryKind.Idle;

	[Fact]
	public void BackoffDoublesWithIndependentJitterThenCaps() {
		Check(0, [5, 10, 20, 40, 80, 150, 150]);
		Check(.5, [7.5, 15, 30, 60, 120, 225, 225]);
		Check(1, [10, 20, 40, 80, 160, 300, 300]);

		static void Check(double sample, double[] seconds) =>
			Assert.Equal(seconds, Enumerable.Range(1, seconds.Length).Select(i => Agent.NetworkBackoff(i, sample).TotalSeconds));
	}

	[Fact]
	public async Task RecoversSameRequestWithoutReplayingToolsAndYieldsBothSlotsWhilePaused() {
		using var dir = new TempDir();
		var clock = new FakeTimeProvider();
		var tool = new CountTool();
		var requests = new ConcurrentQueue<ModelRequest>();
		var backend = new Backend(r => {
			if(r.Model != "worker") return FakeBackend.Say("other agent answered");
			requests.Enqueue(r);
			return requests.Count switch {
				1 => FakeBackend.Call("effect", tool.Name, "{}"),
				2 => throw Offline(),
				_ => FakeBackend.Say("recovered"),
			};
		});
		var m = new AgentManager(new Home(dir.File("home")), new Config {
			Model = "other", MaxRunningAgents = 1, MaxConcurrentModelCalls = 1,
		}, backend, new ToolRegistry().Add(tool), clock) { NetworkJitter = () => 0 };
		var (_, parent) = m.CreateProject("project", [dir.Path], null, false);
		var worker = m.SpawnTask(parent, "worker", false, "worker", null);
		var statusEvents = new ConcurrentQueue<string>();
		m.Events += e => statusEvents.Enqueue(JsonIO.Serialize(e));
		Wake(worker);
		await Wait.Until(() => worker.Status == AgentStatus.WaitingForNetwork);
		Assert.Contains("active", worker.StartFreshContext());
		Assert.Equal(clock.GetUtcNow() + FirstWait, worker.CurrentStatus.RetryAt);
		Assert.Equal(Offline().Message, worker.StatusDetail);
		Assert.Equal(ModelCallOutcome.Failed, worker.ModelCall!.Outcome);
		Assert.Contains(statusEvents, e => e.Contains("\"status\":\"waiting_for_network\"") && e.Contains("\"retryAt\":"));
		Assert.Empty(m.AgentMessages(worker.Info.Id));

		Wake(parent); // The outage wait itself, even before pause, gives up both slots.
		await Wait.Until(() => Ended(parent));
		worker.SetPaused(true);
		clock.Advance(FirstWait);
		await Wait.Until(() => worker.Status == AgentStatus.Paused);
		Assert.Equal(2, requests.Count);
		Assert.Null(worker.CurrentStatus.RetryAt);
		worker.SetPaused(false);
		await Wait.Until(() => Ended(worker));

		Assert.Equal(3, requests.Count);
		var retries = requests.ToArray();
		Assert.Same(retries[1], retries[2]);
		Assert.Equal(1, tool.Calls);
		Assert.Single(worker.Transcript.Entries, e => e.Kind == EntryKind.ToolResult);
		Assert.Single(worker.Transcript.Entries, e => e.Kind == EntryKind.Wake);
		Assert.Contains(worker.Transcript.Entries, e => e.Text == "recovered");
		Assert.Empty(m.AgentMessages(worker.Info.Id));
	}

	[Fact]
	public async Task SharedPacerStaggersAClusterEvenWithUnlimitedModelSlots() {
		using var dir = new TempDir();
		var clock = new FakeTimeProvider();
		var attempts = new ConcurrentDictionary<string, int>();
		var starts = new ConcurrentQueue<DateTimeOffset>();
		var m = Manager(dir, clock, new Backend(r => {
			if(attempts.AddOrUpdate(r.CacheKey, 1, (_, n) => n + 1) == 1) throw Offline();
			starts.Enqueue(clock.GetUtcNow());
			return FakeBackend.Say("back online");
		}));
		var agents = Enumerable.Range(0, 4).Select(i => m.CreateChat($"chat {i}", dir.Path)).ToArray();
		foreach(var a in agents) Wake(a);
		await Wait.Until(() => agents.All(a => a.Status == AgentStatus.WaitingForNetwork));
		clock.Advance(FirstWait);
		for(int i = 1; i <= agents.Length; i++) {
			var next = clock.GetUtcNow() + TimeSpan.FromSeconds(2);
			await Wait.Until(() => agents.Count(Ended) == i && agents.Count(a => a.CurrentStatus.RetryAt == next) == agents.Length - i);
			Assert.Equal(i, starts.Count);
			clock.Advance(TimeSpan.FromSeconds(2));
		}
		Assert.All(starts.Zip(starts.Skip(1)), pair => Assert.Equal(TimeSpan.FromSeconds(2), pair.Second - pair.First));
	}

	[Theory]
	[InlineData("expire")]
	[InlineData("stop")]
	[InlineData("close")]
	[InlineData("drain")]
	public async Task RecoveryWaitTerminatesWithoutExtraDispatchAndOnlyExpiryNotifiesParent(string action) {
		using var dir = new TempDir();
		var clock = new FakeTimeProvider();
		var failures = 0;
		var m = Manager(dir, clock, new Backend(r => {
			if(r.Model != "worker") return FakeBackend.Say("acknowledged");
			Interlocked.Increment(ref failures);
			throw Offline();
		}));
		var (_, parent) = m.CreateProject("project", [dir.Path], null, false);
		var worker = m.SpawnTask(parent, "worker", false, "worker", null);
		Wake(worker);
		await Wait.Until(() => worker.Status == AgentStatus.WaitingForNetwork);
		Assert.Empty(m.AgentMessages(worker.Info.Id));
		if(action == "expire") {
			var expires = clock.GetUtcNow() + Agent.NetworkRecoveryWindow;
			for(int i = 1; ; i++) {
				var now = clock.GetUtcNow();
				var next = now + Agent.NetworkBackoff(i, 0);
				await Wait.Until(() => worker.Status == AgentStatus.WaitingForNetwork && worker.CurrentStatus.RetryAt == (next < expires ? next : null));
				Assert.Equal(i, failures);
				if(next >= expires) {
					clock.Advance(expires - now - TimeSpan.FromSeconds(1));
					Assert.Equal(AgentStatus.WaitingForNetwork, worker.Status);
					clock.Advance(TimeSpan.FromSeconds(1));
					break;
				}
				clock.Advance(next - now);
			}
			await Wait.Until(() => worker.Status == AgentStatus.Error && Ended(parent));
			Assert.Contains("45 minutes", worker.StatusDetail);
			Assert.Equal("turn_error", Assert.Single(m.AgentMessages(worker.Info.Id)).Reason);
			Assert.Equal("turn_error", Assert.Single(parent.Transcript.Entries, e => e.Kind == EntryKind.Wake).Reason);
			Assert.Null(worker.CurrentStatus.RetryAt);
			return;
		}
		if(action == "drain") {
			await m.Restart.DrainAsync(TimeSpan.Zero, TimeSpan.FromSeconds(5), () => { });
			Assert.DoesNotContain(worker.Transcript.Entries, e => e.Kind == EntryKind.Idle);
		} else {
			if(action == "stop") worker.Interrupt();
			else worker.SetClosed(true);
			await Wait.Until(() => Ended(worker));
			Assert.Equal("interrupted", worker.Transcript.Last!.Reason);
			Assert.Null(worker.CurrentStatus.RetryAt);
		}
		clock.Advance(TimeSpan.FromHours(1));
		Assert.Equal(1, failures);
		Assert.Empty(m.AgentMessages(worker.Info.Id));
		Assert.DoesNotContain(worker.Transcript.Entries, e => e.Kind == EntryKind.Error);
	}

	static AgentManager Manager(TempDir dir, TimeProvider clock, IModelBackend backend) =>
		new(new Home(dir.File("home")), new Config { Model = "other" }, backend, new ToolRegistry(), clock) { NetworkJitter = () => 0 };

	sealed class Backend(Func<ModelRequest, ModelResult> reply) : IModelBackend {
		public string Provider => "test";
		public bool Connected => true;
		public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => throw new InvalidOperationException();
		public Task<ModelResult> CompleteAsync(ModelRequest request, Action<string> onTextDelta, CancellationToken ct) => Task.FromResult(reply(request));
	}

	sealed class CountTool : ITool {
		public int Calls;
		public string Name => "effect";
		public string Description => "Record a side effect";
		public JsonObject Parameters => Schema.Object();
		public Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
			Interlocked.Increment(ref Calls);
			return Task.FromResult(new ToolOutput("effect recorded"));
		}
	}
}
