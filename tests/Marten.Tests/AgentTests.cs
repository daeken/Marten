using Marten.Agents;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Tests;

public class AgentTests {
	static AgentManager Manager(TempDir dir, FakeBackend backend, Config? config = null) {
		var home = new Home(dir.File("home"));
		var tools = new ToolRegistry().Add(new ReadFileTool()).Add(new EditFileTool());
		return new AgentManager(home, config ?? new Config(), backend, tools);
	}

	static bool Idle(Agent a) => a.Status != AgentStatus.Running && a.Transcript.Last?.Kind == EntryKind.Idle && a.PendingCount == 0;

	[Fact]
	public async Task RunsToolsUntilTheModelAnswers() {
		using var dir = new TempDir();
		File.WriteAllText(dir.File("note.txt"), "remember the milk\n");
		var backend = new FakeBackend();
		backend.Script.Enqueue(_ => FakeBackend.Call("c1", "read_file", $"{{\"path\":\"{dir.File("note.txt")}\",\"start_line\":null,\"end_line\":null}}"));
		backend.Script.Enqueue(_ => FakeBackend.Say("It says to remember the milk."));
		var agent = Manager(dir, backend).CreateChat("t", dir.Path);
		agent.Send(new Inbound("Sera", "what's in note.txt?", SendMode.Queued));
		await Wait.Until(() => Idle(agent));

		Assert.Equal([EntryKind.Wake, EntryKind.Message, EntryKind.ToolCall, EntryKind.ToolResult, EntryKind.Assistant, EntryKind.Idle],
			agent.Transcript.Entries.Select(e => e.Kind));
		Assert.Contains("remember the milk", agent.Transcript.Entries[3].Text);
		// The second call saw the tool result.
		Assert.Contains(backend.Requests[1].History, e => e.Kind == EntryKind.ToolResult);
		Assert.Equal("fake-1", backend.Requests[0].Model);
	}

	[Fact]
	public async Task ImmediateMessagesLandAtTheNextToolBoundaryQueuedOnesWaitForIdle() {
		using var dir = new TempDir();
		var backend = new FakeBackend { Gate = new() };
		backend.Script.Enqueue(_ => FakeBackend.Call("c1", "read_file", "{\"path\":\"missing\"}"));
		backend.Script.Enqueue(_ => FakeBackend.Say("first answer"));
		backend.Script.Enqueue(_ => FakeBackend.Say("second answer"));
		var agent = Manager(dir, backend).CreateChat("t", dir.Path);
		agent.Send(new Inbound("Sera", "go", SendMode.Queued));
		await Wait.Until(() => backend.Requests.Count == 1);
		agent.Send(new Inbound("Sera", "steer", SendMode.Immediate));
		agent.Send(new Inbound("Sera", "later", SendMode.Queued));
		backend.Gate.SetResult();
		await Wait.Until(() => backend.Requests.Count == 3 && Idle(agent));

		var kinds = agent.Transcript.Entries.Select(e => (e.Kind, e.Text)).ToList();
		var steer = kinds.IndexOf((EntryKind.Message, "steer"));
		var result = kinds.FindIndex(k => k.Kind == EntryKind.ToolResult);
		var first = kinds.IndexOf((EntryKind.Assistant, "first answer"));
		var later = kinds.IndexOf((EntryKind.Message, "later"));
		Assert.True(result < steer && steer < first, string.Join(", ", kinds));
		Assert.True(first < later, string.Join(", ", kinds));
		// The queued message arrived after the agent went idle, in a new turn.
		Assert.Equal(EntryKind.Idle, kinds[later - 2].Kind);
	}

	[Fact]
	public async Task RestartAnswersInterruptedCallsAndResumesTheTurn() {
		using var dir = new TempDir();
		var backend = new FakeBackend { Gate = new() };
		var manager = Manager(dir, backend);
		var agent = manager.CreateChat("t", dir.Path);
		agent.Transcript.Append(new Entry { Kind = EntryKind.Wake, Reason = "message" });
		agent.Transcript.Append(new Entry { Kind = EntryKind.Message, From = "Sera", Text = "run the tests" });
		agent.Transcript.Append(new Entry { Kind = EntryKind.ToolCall, CallId = "c1", Name = "powershell", Arguments = "{}" });

		var restarted = new FakeBackend();
		restarted.Script.Enqueue(_ => FakeBackend.Say("The run was interrupted; rerunning is up to you."));
		var manager2 = Manager(dir, restarted);
		manager2.LoadAll();
		var again = manager2.Get(agent.Info.Id)!;
		await Wait.Until(() => Idle(again));

		var result = again.Transcript.Entries.Single(e => e.Kind == EntryKind.ToolResult);
		Assert.True(result.IsError);
		Assert.Contains("not re-run", result.Text);
		Assert.Single(restarted.Requests);
		Assert.Equal("restart", again.Transcript.Entries.Last(e => e.Kind == EntryKind.Wake).Reason);
	}

	[Fact]
	public async Task OversizedResultsAreTruncatedWithAPointer() {
		using var dir = new TempDir();
		File.WriteAllText(dir.File("big.txt"), string.Join("\n", Enumerable.Range(0, 5000).Select(i => $"line {i}")));
		var backend = new FakeBackend();
		backend.Script.Enqueue(_ => FakeBackend.Call("c1", "read_file", "{\"path\":\"big.txt\"}"));
		backend.Script.Enqueue(_ => FakeBackend.Say("ok"));
		var agent = Manager(dir, backend, new Config { ToolResultMaxChars = 1000 }).CreateChat("t", dir.Path);
		agent.Send(new Inbound("Sera", "read it", SendMode.Queued));
		await Wait.Until(() => Idle(agent));
		var result = agent.Transcript.Entries.Single(e => e.Kind == EntryKind.ToolResult);
		Assert.True(result.Text!.Length < 1300);
		Assert.Contains("truncated", result.Text);
		Assert.True(File.Exists(Path.Combine(agent.Directory, result.FullOutput!)));
	}
}
