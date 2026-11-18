using System.Threading.Channels;
using Marten.Agents;
using Marten.Models;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Tests;

public class ResourceSlotTests {
	static AgentManager Manager(TempDir dir, Config config, IModelBackend backend, ToolRegistry? tools = null) =>
		new(new Home(dir.File("home")), config with { Model = "fake" }, backend, tools ?? new ToolRegistry());

	static void Wake(Agent agent) => agent.Send(new Inbound("Sera", "go", SendMode.Queued));
	static bool Idle(Agent agent) => agent.Status == AgentStatus.Idle && agent.Transcript.Last?.Kind == EntryKind.Idle;
	static Task Waiting(Agent agent, string detail) => Wait.Until(() => agent.Status == AgentStatus.WaitingForSlot && agent.StatusDetail == detail);

	[Theory]
	[InlineData(true, "Running agent limit")]
	[InlineData(false, "Concurrent model call limit")]
	public async Task QueuedTurnsCanBeInterruptedAndFailureReleasesCapacity(bool capAgents, string detail) {
		using var dir = new TempDir();
		var backend = new HeldBackend();
		var m = Manager(dir, capAgents ? new Config { MaxRunningAgents = 1 } : new Config { MaxConcurrentModelCalls = 1 }, backend);
		var first = m.CreateChat("first", dir.Path);
		var cancelled = m.CreateChat("cancelled", dir.Path);
		var next = m.CreateChat("next", dir.Path);
		var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
		m.Events += e => events.Enqueue(JsonIO.Serialize(e));
		Wake(first);
		var call = await backend.Next();
		Wake(cancelled);
		await Waiting(cancelled, detail);
		Wake(next);
		await Waiting(next, detail);
		cancelled.Interrupt();
		await Wait.Until(() => Idle(cancelled) && cancelled.Transcript.Last?.Reason == "interrupted");
		Assert.Contains(events, e => e.Contains("\"status\":\"waiting_for_slot\"") && e.Contains(cancelled.Info.Id));
		call.Result.SetException(new InvalidOperationException("backend failed"));
		call = await backend.Next();
		Assert.Equal($"marten:{next.Info.Id}", call.Request.CacheKey);
		call.Result.SetResult(FakeBackend.Say("done"));
		await Wait.Until(() => Idle(next) && first.Status == AgentStatus.Error);
	}

	[Fact]
	public async Task PausingAModelWaiterReleasesItsAgentSlot() {
		using var dir = new TempDir();
		var backend = new HeldBackend();
		var m = Manager(dir, new Config { MaxRunningAgents = 2, MaxConcurrentModelCalls = 1 }, backend);
		var first = m.CreateChat("first", dir.Path);
		var paused = m.CreateChat("paused", dir.Path);
		var next = m.CreateChat("next", dir.Path);
		Wake(first);
		var call = await backend.Next();
		Wake(paused);
		await Waiting(paused, "Concurrent model call limit");
		Wake(next);
		await Waiting(next, "Running agent limit");
		paused.SetPaused(true);
		await Waiting(next, "Concurrent model call limit");
		await Wait.Until(() => paused.Status == AgentStatus.Paused);
		call.Result.SetResult(FakeBackend.Say("first"));
		call = await backend.Next();
		Assert.Equal($"marten:{next.Info.Id}", call.Request.CacheKey);
		call.Result.SetResult(FakeBackend.Say("next"));
		paused.SetPaused(false);
		call = await backend.Next();
		Assert.Equal($"marten:{paused.Info.Id}", call.Request.CacheKey);
		call.Result.SetResult(FakeBackend.Say("paused"));
		await Wait.Until(() => Idle(first) && Idle(next) && Idle(paused));
	}

	[Fact]
	public async Task QuotaBackoffGivesUpBothSlots() {
		using var dir = new TempDir();
		var backend = new HeldBackend();
		var m = Manager(dir, new Config { MaxRunningAgents = 1, MaxConcurrentModelCalls = 1 }, backend);
		var waiting = m.CreateChat("quota", dir.Path);
		var next = m.CreateChat("next", dir.Path);
		Wake(waiting);
		(await backend.Next()).Result.SetException(new RateLimitedException("slow down", TimeSpan.FromMinutes(10)));
		await Wait.Until(() => waiting.Status == AgentStatus.WaitingForQuota);
		try {
			Wake(next);
			var call = await backend.Next();
			Assert.Equal($"marten:{next.Info.Id}", call.Request.CacheKey);
			call.Result.SetResult(FakeBackend.Say("done"));
			await Wait.Until(() => Idle(next));
		} finally {
			waiting.Interrupt();
		}
		await Wait.Until(() => Idle(waiting));
	}

	[Fact]
	public async Task CompactionSharesModelLimitWithoutRecursingIntoTheLease() {
		using var dir = new TempDir();
		var backend = new HeldBackend();
		var m = Manager(dir, new Config { MaxConcurrentModelCalls = 1 }, backend);
		var first = m.CreateChat("first", dir.Path);
		var next = m.CreateChat("next", dir.Path);
		Wake(first);
		var call = await backend.Next();
		Wake(next);
		await Waiting(next, "Concurrent model call limit");
		call.Result.SetException(new ContextFullException());
		call = await backend.Next();
		Assert.Equal($"marten:{next.Info.Id}", call.Request.CacheKey);
		call.Result.SetResult(FakeBackend.Say("next"));
		call = await backend.Next();
		Assert.Equal("Compacting context", first.StatusDetail);
		call.Result.SetResult(FakeBackend.Say("summary"));
		call = await backend.Next();
		call.Result.SetResult(FakeBackend.Say("first"));
		await Wait.Until(() => Idle(first) && Idle(next));
		Assert.Contains(first.Transcript.Entries, e => e.Kind == EntryKind.Compaction);
	}

	[Fact]
	public async Task NamedCommandSlotsQueueAcrossAgentsAndClearOnCancelTimeoutAndThrow() {
		using var dir = new TempDir();
		var ps = new PowerShellTool(() => 20, () => 2, new ChildProcessWatcher());
		var m = Manager(dir, new Config { Slots = new() { ["build"] = 1, ["test"] = 1 } }, new HeldBackend(), new ToolRegistry().Add(ps));
		var holder = m.CreateChat("holder", dir.Path);
		var waiter = m.CreateChat("waiter", dir.Path);
		var marker = dir.File("entered");
		var finish = dir.File("finish");
		var hold = ps.RunAsync(holder, $"$Marten.WithSlot('build', {{ New-Item '{marker}' | Out-Null; while (!(Test-Path '{finish}')) {{ Start-Sleep -Milliseconds 20 }}; throw 'failed inside slot' }})", TimeSpan.FromSeconds(20), default);
		const string command = "$Marten.WithSlot('build', { 'success' })";
		using var cancel = new CancellationTokenSource();
		try {
			await Wait.Until(() => File.Exists(marker));
			var queued = ps.RunAsync(waiter, command, TimeSpan.FromSeconds(20), cancel.Token);
			await Waiting(waiter, "Command slot: build");
			cancel.Cancel();
			Assert.Contains("Cancelled", (await queued.WaitAsync(TimeSpan.FromSeconds(5))).Text);
			Assert.Equal(AgentStatus.Idle, waiter.Status);
			var timedOut = await ps.RunAsync(waiter, command, TimeSpan.FromMilliseconds(250), default);
			Assert.True(timedOut.IsError);
			Assert.Contains("Timed out", timedOut.Text);
			Assert.DoesNotContain("success", timedOut.Text);
			Assert.Equal(AgentStatus.Idle, waiter.Status);
		} finally {
			File.WriteAllText(finish, "go");
		}
		Assert.Contains("failed inside slot", (await hold.WaitAsync(TimeSpan.FromSeconds(5))).Text);
		var nested = await ps.RunAsync(waiter, "$Marten.WithSlot('build', { $Marten.WithSlot('test', { 'no' }) })", TimeSpan.FromSeconds(5), default);
		Assert.Contains("cannot be nested", nested.Text);
		Assert.Contains("success", (await ps.RunAsync(waiter, command, TimeSpan.FromSeconds(5), default)).Text);
		var replay = await waiter.RerunCell(waiter.Notebook().Last().Id, default);
		Assert.True(replay.SameOutput);
	}

	sealed class HeldBackend : IModelBackend {
		readonly Channel<Call> calls = Channel.CreateUnbounded<Call>();
		public record Call(ModelRequest Request, TaskCompletionSource<ModelResult> Result);
		public string Provider => "fake";
		public bool Connected => true;
		public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["fake"]);
		public Task<Call> Next() => calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
		public Task<ModelResult> CompleteAsync(ModelRequest request, Action<string> onDelta, CancellationToken ct) {
			var result = new TaskCompletionSource<ModelResult>(TaskCreationOptions.RunContinuationsAsynchronously);
			calls.Writer.TryWrite(new Call(request, result));
			return result.Task.WaitAsync(ct);
		}
	}
}
