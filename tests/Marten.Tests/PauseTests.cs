using Marten.Agents;
using Marten.Models;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Tests;

public class PauseTests {
	static AgentManager Manager(TempDir dir, IModelBackend backend) {
		AgentManager? m = null;
		Func<AgentManager> get = () => m!;
		var tools = new ToolRegistry().Add(new ReadFileTool()).Add(new EditFileTool(new MemoryGuard(get)))
			.Add(new TaskAddTool(get)).Add(new TaskUpdateTool(get)).Add(new SpawnAgentTool(get));
		return m = new AgentManager(new Home(dir.File("home")), new Config(), backend, tools);
	}

	static bool Idle(Agent a) => a.Status == AgentStatus.Idle && a.Transcript.Last?.Kind == EntryKind.Idle && a.PendingCount == 0;

	[Fact]
	public async Task APausedAgentQueuesMessagesAndAnswersWhenResumed() {
		using var dir = new TempDir();
		var backend = new FakeBackend();
		backend.Script.Enqueue(_ => FakeBackend.Say("hello"));
		var agent = Manager(dir, backend).CreateChat("t", dir.Path);
		agent.SetPaused(true);
		agent.Send(new Inbound("Sera", "hi", SendMode.Queued));
		await Wait.Until(() => agent.Status == AgentStatus.Paused);
		await Task.Delay(100);
		Assert.Empty(backend.Requests);
		Assert.Equal(1, agent.PendingCount);

		agent.SetPaused(false);
		await Wait.Until(() => Idle(agent));
		Assert.Contains(agent.Transcript.Entries, e => e.Text == "hello");
	}

	[Fact]
	public async Task PausingDuringTheModelHoldsBeforeTheFirstTool() {
		using var dir = new TempDir();
		File.WriteAllText(dir.File("f.txt"), "content\n");
		var backend = new FakeBackend { Gate = new() };
		backend.Script.Enqueue(_ => FakeBackend.Call("c1", "read_file", $"{{\"path\":\"{dir.File("f.txt")}\"}}"));
		backend.Script.Enqueue(_ => FakeBackend.Say("read it"));
		var agent = Manager(dir, backend).CreateChat("t", dir.Path);
		agent.Send(new Inbound("Sera", "read f", SendMode.Queued));
		await Wait.Until(() => backend.Requests.Count == 1);
		agent.SetPaused(true);
		backend.Gate.SetResult();
		await Wait.Until(() => agent.Status == AgentStatus.Paused);
		Assert.DoesNotContain(agent.Transcript.Entries, e => e.Kind is EntryKind.ToolCall or EntryKind.ToolResult);
		Assert.Single(backend.Requests);

		agent.SetPaused(false);
		await Wait.Until(() => Idle(agent));
		Assert.Contains(agent.Transcript.Entries, e => e.Text == "read it");
	}

	[Fact]
	public async Task InterruptStillWorksWhilePaused() {
		using var dir = new TempDir();
		var backend = new FakeBackend { Gate = new() };
		backend.Script.Enqueue(_ => FakeBackend.Call("c1", "read_file", "{\"path\":\"missing\"}"));
		var agent = Manager(dir, backend).CreateChat("t", dir.Path);
		agent.Send(new Inbound("Sera", "go", SendMode.Queued));
		await Wait.Until(() => backend.Requests.Count == 1);
		agent.SetPaused(true);
		backend.Gate.SetResult();
		await Wait.Until(() => agent.Status == AgentStatus.Paused);
		agent.Interrupt();
		await Wait.Until(() => agent.Transcript.Last?.Reason == "interrupted");
	}

	[Fact]
	public async Task ProjectAndGlobalPauseCoverTheirAgentsAndPersist() {
		using var dir = new TempDir();
		Directory.CreateDirectory(dir.File("repo"));
		var m = Manager(dir, new FakeBackend());
		var (project, orch) = m.CreateProject("p", [dir.File("repo")], null, false);
		var task = m.SpawnTask(orch, "t", false, null, null);
		var chat = m.CreateChat("c", dir.Path);

		m.SetProjectPaused(project, true);
		Assert.True(orch.IsPaused && task.IsPaused);
		Assert.False(chat.IsPaused);
		m.SetProjectPaused(project, false);
		m.SetPausedAll(true);
		Assert.True(orch.IsPaused && task.IsPaused && chat.IsPaused);
		Assert.False(chat.Info.Paused);

		var reloaded = Manager(dir, new FakeBackend());
		reloaded.LoadAll();
		Assert.True(reloaded.PausedAll);
		Assert.True(reloaded.Get(chat.Info.Id)!.IsPaused);
		reloaded.SetPausedAll(false);
		Assert.False(Manager(dir, new FakeBackend()).PausedAll);
		await Task.CompletedTask;
	}

	[Fact]
	public async Task NoNudgesWhilePausedAndTheMissedNudgeComesOnResume() {
		using var dir = new TempDir();
		var backend = new FakeBackend();
		backend.Script.Enqueue(_ => FakeBackend.Say("working on it"));
		var m = Manager(dir, backend);
		var agent = m.CreateChat("t", dir.Path);
		m.SetPausedAll(true);
		agent.AddTask("write the parser", agent.Info.Id);
		await Task.Delay(100);
		Assert.Empty(backend.Requests);

		m.SetPausedAll(false);
		await Wait.Until(() => backend.Requests.Count >= 1);
		Assert.Contains(agent.Transcript.Entries, e => e.Kind == EntryKind.Wake && e.Reason == "nudge");
	}
}
