using System.Text.Json;
using System.Text.Json.Nodes;
using Marten.Agents;
using Marten.Models;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Tests;

public class ContextTests {
	static AgentManager Manager(TempDir dir, IModelBackend backend, Config? config = null) {
		AgentManager? m = null;
		Func<AgentManager> get = () => m!;
		var tools = new ToolRegistry()
			.Add(new ReadFileTool()).Add(new EditFileTool(new MemoryGuard(get)))
			.Add(new SendMessageTool(get)).Add(new TaskAddTool(get)).Add(new TaskUpdateTool(get))
			.Add(new SpawnAgentTool(get)).Add(new CloseAgentTool(get))
			.Add(new ElideResultsTool()).Add(new SetModelTool(get));
		return m = new AgentManager(new Home(dir.File("home")), config ?? new Config(), backend, tools);
	}

	static bool Idle(Agent a) => a.Status != AgentStatus.Running && a.Transcript.Last?.Kind == EntryKind.Idle && a.PendingCount == 0;

	static void ToolRound(Agent a, string id, string output) {
		a.Transcript.Append(new Entry { Kind = EntryKind.ToolCall, CallId = id, Name = "read_file", Arguments = "{}" });
		a.Transcript.Append(new Entry { Kind = EntryKind.ToolResult, CallId = id, Text = output });
	}

	static void Exchange(Agent a, string question, string answer) {
		a.Transcript.Append(new Entry { Kind = EntryKind.Wake, Reason = "message" });
		a.Transcript.Append(new Entry { Kind = EntryKind.Message, From = "Sera", Text = question });
		a.Transcript.Append(new Entry { Kind = EntryKind.Assistant, Text = answer });
		a.Transcript.Append(new Entry { Kind = EntryKind.Idle });
	}

	static ModelResult WithUsage(ModelResult r, long inputTokens) =>
		r with { Usage = JsonSerializer.SerializeToElement(new { input_tokens = inputTokens }) };

	[Fact]
	public async Task ElideNowReplacesResultsInTheViewButKeepsTheLogAndTheFullText() {
		using var dir = new TempDir();
		var agent = Manager(dir, new FakeBackend()).CreateChat("t", dir.Path);
		Exchange(agent, "go", "ok");
		ToolRound(agent, "a", "alpha output");
		ToolRound(agent, "b", "beta output");
		ToolRound(agent, "c", "gamma output");

		var r = await new ElideResultsTool().InvokeAsync(agent, new JsonObject { ["count"] = 2 }, default);
		Assert.Equal("Elided 2 results.", r.Text);

		var results = agent.View().Where(e => e.Kind == EntryKind.ToolResult).Select(e => e.Text!).ToList();
		Assert.Equal("alpha output", results[0]);
		Assert.All(results.Skip(1), t => Assert.StartsWith("[Elided", t));
		var path = results[2].Split(" is in ")[1].Split("; ")[0];
		Assert.Equal("gamma output", File.ReadAllText(path));
		// The log still holds every result verbatim.
		Assert.Contains(agent.Transcript.Entries, e => e.Text == "gamma output");
		// Already-elided results don't count again: the next one back is alpha.
		await new ElideResultsTool().InvokeAsync(agent, new JsonObject { ["count"] = 1 }, default);
		Assert.StartsWith("[Elided", agent.View().First(e => e.Kind == EntryKind.ToolResult).Text);
		Assert.Equal("Nothing to elide.", (await new ElideResultsTool().InvokeAsync(agent, new JsonObject { ["count"] = 1 }, default)).Text);
	}

	[Fact]
	public async Task OldResultsAreElidedInOneBatchPastTheirBudget() {
		using var dir = new TempDir();
		var backend = new FakeBackend();
		backend.Script.Enqueue(_ => FakeBackend.Say("done"));
		// Budget: 20k * 0.3 = 6k tokens = 24k chars. Eight 5k-char results = 10k tokens.
		var agent = Manager(dir, backend, new Config { ContextTokens = 20_000, ElideAt = 0.3, CompactAt = 0.9 }).CreateChat("t", dir.Path);
		Exchange(agent, "go", "ok");
		for(var i = 0; i < 8; i++) ToolRound(agent, $"c{i}", new string((char) ('a' + i), 5_000));
		agent.Send(new Inbound("Sera", "next", SendMode.Queued));
		await Wait.Until(() => Idle(agent));

		var elides = agent.Transcript.Entries.Where(e => e.Kind == EntryKind.Elide).ToList();
		Assert.Single(elides);
		// Down to half the budget (3k tokens), but the 4 most recent are always kept.
		Assert.Equal(4, elides[0].Seqs!.Count);
		var sent = backend.Requests[0].History.Where(e => e.Kind == EntryKind.ToolResult).Select(e => e.Text!).ToList();
		Assert.All(sent.Take(4), t => Assert.StartsWith("[Elided", t));
		Assert.All(sent.Skip(4), t => Assert.Equal(5_000, t.Length));
	}

	[Fact]
	public async Task ChatAgentsRollOverAtAnInputBoundary() {
		using var dir = new TempDir();
		var backend = new FakeBackend();
		backend.Script.Enqueue(_ => FakeBackend.Say("SUMMARY: talked about a and b"));
		var agent = Manager(dir, backend).CreateChat("t", dir.Path);
		Exchange(agent, new string('a', 1_000), "answer a");
		Exchange(agent, new string('b', 1_000), "answer b");
		Exchange(agent, "c", "answer c");
		Exchange(agent, "d", "answer d");

		await agent.Compact("fake-1", default);

		var ask = backend.Requests[0].History[^1];
		Assert.Equal("Marten", ask.From);
		Assert.Contains("replaced by a summary", ask.Text);
		var compaction = agent.Transcript.Entries.Single(e => e.Kind == EntryKind.Compaction);
		Assert.Equal("rollover", compaction.Reason);
		var view = agent.View();
		Assert.Contains("SUMMARY: talked about a and b", view[0].Text);
		Assert.Equal(EntryKind.Wake, view[1].Kind);
		Assert.DoesNotContain(view, e => e.Text == "answer a");
		Assert.Contains(view, e => e.Text == "answer d");
		Assert.Equal(compaction.Instructions, agent.Instructions);
		Assert.Equal(agent.Instructions, File.ReadAllText(Path.Combine(agent.Directory, "system.md")));
	}

	[Fact]
	public async Task TaskAgentsCompactWholeAndPickUpCurrentMemoryAndNotes() {
		using var dir = new TempDir();
		Directory.CreateDirectory(dir.File("repo"));
		var backend = new FakeBackend();
		backend.Script.Enqueue(_ => FakeBackend.Say("SUMMARY: parser half done"));
		var m = Manager(dir, backend);
		var (project, orch) = m.CreateProject("p", [dir.File("repo")], null, false);
		var task = m.SpawnTask(orch, "parser", false, null, null);
		Assert.DoesNotContain("the lexer is in src/lex", task.Instructions);
		Exchange(task, "write the parser", "on it");
		ToolRound(task, "x", "lots of output");
		File.WriteAllText(Path.Combine(task.Directory, "NOTES.md"), "the lexer is in src/lex\n");
		File.WriteAllText(project.MemoryPath, "- builds need net10\n");

		await task.Compact("fake-1", default);

		Assert.Contains("carry on with no other history", backend.Requests[0].History[^1].Text);
		Assert.Equal("whole", task.Transcript.Entries.Single(e => e.Kind == EntryKind.Compaction).Reason);
		var view = task.View();
		Assert.Single(view);
		Assert.Contains("SUMMARY: parser half done", view[0].Text);
		Assert.Contains("the lexer is in src/lex", task.Instructions);
		Assert.Contains("builds need net10", task.Instructions);

		// A restart picks the rebuilt prompt back up from the log.
		var reloaded = new AgentManager(new Home(dir.File("home")), new Config(), backend, m.Tools);
		reloaded.LoadAll();
		Assert.Equal(task.Instructions, reloaded.Get(task.Info.Id)!.Instructions);
	}

	[Fact]
	public async Task ReportedUsagePastTheThresholdCompactsBeforeTheNextCall() {
		using var dir = new TempDir();
		var backend = new FakeBackend();
		backend.Script.Enqueue(_ => WithUsage(FakeBackend.Say("one"), 190_000));
		backend.Script.Enqueue(_ => FakeBackend.Say("SUMMARY"));
		backend.Script.Enqueue(_ => FakeBackend.Say("two"));
		var agent = Manager(dir, backend).CreateChat("t", dir.Path);
		agent.Send(new Inbound("Sera", "first", SendMode.Queued));
		await Wait.Until(() => Idle(agent));
		agent.Send(new Inbound("Sera", "second", SendMode.Queued));
		await Wait.Until(() => Idle(agent) && backend.Requests.Count == 3);

		Assert.Equal("Marten", backend.Requests[1].History[^1].From);
		Assert.Contains("SUMMARY", backend.Requests[2].History[0].Text);
		Assert.Contains(backend.Requests[2].History, e => e.Text == "second");
		Assert.Contains(agent.Transcript.Entries, e => e.Kind == EntryKind.Assistant && e.Text == "two");
	}

	[Fact]
	public async Task AContextFullErrorCompactsAndRetriesOnce() {
		using var dir = new TempDir();
		var backend = new FakeBackend();
		backend.Script.Enqueue(_ => throw new ContextFullException());
		backend.Script.Enqueue(_ => FakeBackend.Say("SUMMARY"));
		backend.Script.Enqueue(_ => FakeBackend.Say("answer"));
		var agent = Manager(dir, backend).CreateChat("t", dir.Path);
		Exchange(agent, "earlier", "earlier answer");
		agent.Send(new Inbound("Sera", "now", SendMode.Queued));
		await Wait.Until(() => Idle(agent) && backend.Requests.Count == 3);

		Assert.Single(agent.Transcript.Entries, e => e.Kind == EntryKind.Compaction);
		Assert.Contains(agent.Transcript.Entries, e => e.Kind == EntryKind.Assistant && e.Text == "answer");
		Assert.DoesNotContain(agent.Transcript.Entries, e => e.Kind == EntryKind.Error);
	}

	[Fact]
	public async Task OrchestratorsSetTheirTaskAgentsModelsButNotTheOtherWayRound() {
		using var dir = new TempDir();
		Directory.CreateDirectory(dir.File("repo"));
		var m = Manager(dir, new FakeBackend());
		var (_, orch) = m.CreateProject("p", [dir.File("repo")], null, false);
		var task = m.SpawnTask(orch, "parser", false, null, null);
		var tool = new SetModelTool(() => m);

		var r = await tool.InvokeAsync(orch, new JsonObject { ["agent"] = "parser", ["model"] = "gpt-big", ["effort"] = "high" }, default);
		Assert.Contains("parser now uses gpt-big", r.Text);
		Assert.Equal(("gpt-big", "high"), (task.Info.Model, task.Info.Effort));

		await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(task, new JsonObject { ["agent"] = orch.Info.Id, ["model"] = "x" }, default));
		await tool.InvokeAsync(task, new JsonObject { ["model"] = null, ["effort"] = "low" }, default);
		Assert.Equal((null, "low"), (task.Info.Model, task.Info.Effort));
	}
}
