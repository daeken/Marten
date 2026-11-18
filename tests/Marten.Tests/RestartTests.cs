using System.Text.Json.Nodes;
using Marten.Agents;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Tests;

public class RestartTests {
	static AgentManager Manager(TempDir dir, FakeBackend backend, ToolRegistry? tools = null) =>
		new(new Home(dir.File("home")), new Config { Model = "fake-1" }, backend, tools ?? new());

	static Task Drain(AgentManager m, int graceMs) =>
		m.Restart.DrainAsync(TimeSpan.FromMilliseconds(graceMs), TimeSpan.FromSeconds(5), () => { });

	[Fact]
	public async Task ActiveModelIsCancelledWithoutUsingToolGraceAndNoNewModelStarts() {
		using var dir = new TempDir();
		var model = new FakeBackend { Gate = new() };
		var manager = Manager(dir, model);
		var a = manager.CreateChat("active", dir.Path);
		var idle = manager.CreateChat("idle", dir.Path);
		a.Send(new Inbound("Sera", "go", SendMode.Queued));
		await Wait.Until(() => model.Requests.Count == 1);
		var drain = Drain(manager, 60_000);
		idle.Send(new Inbound("Sera", "too late", SendMode.Queued));
		await drain.WaitAsync(TimeSpan.FromSeconds(3));
		Assert.Single(model.Requests);
		Assert.DoesNotContain(a.Transcript.Entries, e => e.Kind == EntryKind.Idle);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task DrainsOrCancelsActiveToolWithoutStartingTheRestOfItsBatch(bool finish) {
		using var dir = new TempDir();
		var tool = new HeldTool();
		var model = new FakeBackend();
		var calls = new[] { "running", "not-started" }.Select(id => FakeBackend.Call(id, tool.Name, "{}").Items.Single()).ToList();
		model.Script.Enqueue(_ => new(calls, null));
		var manager = Manager(dir, model, new ToolRegistry().Add(tool));
		var a = manager.CreateChat("active", dir.Path);
		a.Send(new Inbound("Sera", "go", SendMode.Queued));
		await tool.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		var drain = Drain(manager, finish ? 60_000 : 150);
		if(finish) tool.Release.SetResult();
		await drain.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Single(model.Requests);
		Assert.Equal(1, tool.Calls);
		Assert.Equal(!finish, tool.Cancelled);

		var resumed = new FakeBackend();
		resumed.Script.Enqueue(_ => FakeBackend.Say("checked"));
		var reloaded = Manager(dir, resumed, new ToolRegistry().Add(tool));
		reloaded.LoadAll();
		var again = reloaded.Get(a.Info.Id)!;
		await Wait.Until(() => again.Transcript.Last?.Kind == EntryKind.Idle);
		var request = Assert.Single(resumed.Requests);
		var results = request.History.Where(e => e.Kind == EntryKind.ToolResult).ToList();
		Assert.Equal(2, results.Count);
		Assert.Equal(!finish, results[0].IsError == true);
		Assert.True(results[1].IsError);
		Assert.Contains("not re-run", results[1].Text);
		Assert.Contains(request.History, e => e.Kind == EntryKind.Message && e.Text!.Contains("Marten restarted"));
		Assert.Equal(1, tool.Calls);
	}

	[Fact]
	public void StartupAnnouncesRestartToIdleAndClosedAgentsWithoutWakingThem() {
		using var dir = new TempDir();
		var manager = Manager(dir, new FakeBackend());
		manager.CreateChat("idle", dir.Path);
		manager.CreateChat("closed", dir.Path).SetClosed(true);
		var restarted = new FakeBackend();
		var reloaded = Manager(dir, restarted);
		reloaded.LoadAll();
		Assert.All(reloaded.Agents, a => {
			var notice = Assert.Single(a.Transcript.Entries);
			Assert.Equal(EntryKind.Message, notice.Kind);
			Assert.Contains("Marten restarted", notice.Text);
		});
		Assert.Empty(restarted.Requests);
	}

	sealed class HeldTool : ITool {
		public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public int Calls;
		public bool Cancelled;
		public string Name => "held_tool";
		public string Description => "Wait for release or shutdown";
		public JsonObject Parameters => Schema.Object();
		public async Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
			Calls++;
			Started.TrySetResult();
			try { await Release.Task.WaitAsync(ct); }
			catch(OperationCanceledException) { Cancelled = true; throw; }
			return new("finished");
		}
	}
}
