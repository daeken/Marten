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
	public async Task FreshContextIsAnAppendOnlyBoundaryAcrossRestart() {
		using var dir = new TempDir();
		Directory.CreateDirectory(dir.File("repo"));
		var backend = new FakeBackend();
		backend.Script.Enqueue(_ => FakeBackend.Say("old summary"));
		backend.Script.Enqueue(_ => WithUsage(FakeBackend.Say("old reply"), 999_999));
		var m = Manager(dir, backend);
		var (project, agent) = m.CreateProject("p", [dir.File("repo")], "obsolete rule", false);
		var child = m.SpawnTask(agent, "child", false, null, null);
		var task = agent.AddTask("keep this", child.Info.Id);
		var schedule = agent.AddSchedule("Write-Output later", null, DateTimeOffset.UtcNow.AddHours(1), null, null);
		agent.UpdateTask(task.Id, TaskState.Waiting, "waiting intentionally");
		Exchange(agent, "summarize this old question", "old answer");
		await agent.Compact("fake-1", default);
		agent.Send(new Inbound("Sera", "old question", SendMode.Queued));
		await Wait.Until(() => Idle(agent) && agent.Transcript.Entries.Any(e => e.Text == "old reply"));
		Assert.True(agent.EstimateTokens(agent.View()) >= 999_999);
		for(var i = 0; i < 100; i++)
			agent.Transcript.Append(new Entry { Kind = EntryKind.Message, Text = $"archive {i}" });
		// Repair on restart must not bring a pre-boundary orphan into the new context.
		agent.Transcript.Append(new Entry { Kind = EntryKind.ToolCall, CallId = "old orphan" });
		var before = agent.Transcript.Last!.Seq;
		File.WriteAllText(project.PromptPath, "current project rule");
		File.WriteAllText(project.MemoryPath, "current project memory");
		File.WriteAllText(dir.File("repo/AGENTS.md"), "current repo rule");
		File.WriteAllText(Path.Combine(m.Home.Prompts, "orchestrator.md"), "current kind rule");
		Assert.Null(agent.StartFreshContext());
		var boundary = agent.Transcript.Last!;
		Assert.Equal(before + 1, boundary.Seq);
		Assert.Equal(EntryKind.ContextReset, boundary.Kind);
		Assert.Empty(agent.View());
		Assert.Equal(agent.Instructions, boundary.Instructions);
		Assert.Equal(agent.Instructions, File.ReadAllText(Path.Combine(agent.Directory, "system.md")));
		var state = JsonIO.Load(Path.Combine(agent.Directory, "state.json"), () => new AgentState());
		Assert.Equal(0, state.LastInputTokens);
		Assert.Equal(0, state.LastInputSeq);
		Assert.Equal("current project rule", state.PromptSeen!.Project);
		Assert.Equal("current project memory", state.MemorySeen);
		Assert.Equal(2, backend.Requests.Count);
		await m.Restart.DrainAsync(TimeSpan.Zero, TimeSpan.FromSeconds(5), () => {});

		var restarted = new FakeBackend();
		restarted.Script.Enqueue(_ => FakeBackend.Say("fresh reply"));
		var loaded = Manager(dir, restarted);
		loaded.LoadAll();
		var same = loaded.Get(agent.Info.Id)!;
		Assert.Equal(agent.Instructions, same.Instructions);
		Assert.Equal(agent.Info, same.Info);
		Assert.Equal(agent.Info.Id, loaded.Get(child.Info.Id)!.Info.ParentId);
		Assert.Equal(agent.Tasks, same.Tasks);
		Assert.Equal(schedule, Assert.Single(same.Schedules));
		Assert.Empty(restarted.Requests);
		Assert.Single(same.View(), e => e.Text!.StartsWith("Marten restarted."));
		var page = same.Transcript.ReadPage(80);
		Assert.Contains(boundary, page.Entries);
		Assert.True(page.HasOlder);
		Assert.Contains(same.Transcript.ReadPage(80, page.Entries[0].Seq).Entries, e => e.Text == "old summary");
		same.Send(new Inbound("Sera", "fresh question", SendMode.Queued));
		await Wait.Until(() => Idle(same));
		var request = Assert.Single(restarted.Requests);
		Assert.All(request.History, e => Assert.True(e.Seq > boundary.Seq));
		Assert.DoesNotContain(request.History, e => e.Kind == EntryKind.ToolResult || e.Text?.Contains("reloaded") == true || e.Text?.Contains("memory changed") == true);
		Assert.Contains(request.History, e => e.Text == "fresh question");
		foreach(var included in new[] { "current project rule", "current project memory", "current repo rule", "current kind rule" })
			Assert.Contains(included, same.Instructions);
		Assert.DoesNotContain("obsolete rule", same.Instructions);
	}

	[Fact]
	public async Task RestartWithStaleStateDoesNotReplayPreResetPromptOrMemoryDiffs() {
		using var dir = new TempDir();
		var m = Manager(dir, new FakeBackend());
		var (project, agent) = m.CreateProject("p", [dir.Path], "pre-reset rule", false);
		File.WriteAllText(project.MemoryPath, "pre-reset memory");
		agent.SawMemory(project.Memory);
		var path = Path.Combine(agent.Directory, "state.json");
		var staleState = File.ReadAllText(path);
		File.WriteAllText(project.PromptPath, "new rule");
		File.WriteAllText(project.MemoryPath, "new memory");
		Assert.Null(agent.StartFreshContext());
		await m.Restart.DrainAsync(TimeSpan.Zero, TimeSpan.FromSeconds(5), () => {});
		// Simulate a stop after the append reached disk but before either derived file was saved.
		File.WriteAllText(path, staleState);
		File.WriteAllText(Path.Combine(agent.Directory, "system.md"), "pre-reset system file");
		var backend = new FakeBackend();
		backend.Script.Enqueue(_ => FakeBackend.Say("ok"));
		var restarted = Manager(dir, backend);
		restarted.LoadAll();
		var same = restarted.Get(agent.Info.Id)!;
		Assert.Equal(agent.Instructions, same.Instructions);
		same.Send(new Inbound("Sera", "new question", SendMode.Queued));
		await Wait.Until(() => Idle(same));
		var request = Assert.Single(backend.Requests);
		Assert.Contains("new rule", request.Instructions);
		Assert.Contains("new memory", request.Instructions);
		Assert.DoesNotContain("pre-reset", request.Instructions);
		Assert.DoesNotContain(request.History, e => e.Text?.Contains("pre-reset") == true);
	}

	[Fact]
	public async Task ResetDuringAnIdleGapPreservesQueuedInputAndCanBeRepeated() {
		using var dir = new TempDir();
		var backend = new FakeBackend();
		backend.Script.Enqueue(_ => FakeBackend.Say("fresh reply"));
		var agent = Manager(dir, backend).CreateChat("t", dir.Path);
		agent.SetClosed(true);
		var waiting = new Inbound("peer", "queued input", SendMode.Queued);
		agent.Send(waiting); // A peer cannot reopen the agent, so admission cannot consume this yet.
		Assert.Null(agent.StartFreshContext());
		agent.Transcript.Append(new Entry { Kind = EntryKind.Compaction, Text = "discard this second summary", UpTo = agent.Transcript.Last!.Seq });
		Assert.Null(agent.StartFreshContext());
		var latest = agent.Transcript.Last!.Seq;
		Assert.Empty(agent.View());
		Assert.True(agent.Holds(waiting));
		Assert.True(agent.Info.Closed);
		agent.Send(new Inbound("Sera", "reopen", SendMode.Queued));
		await Wait.Until(() => Idle(agent));
		var request = Assert.Single(backend.Requests);
		Assert.All(request.History, e => Assert.True(e.Seq > latest));
		Assert.Contains(request.History, e => e.Text == waiting.Text);
	}

	[Fact]
	public async Task ResetRejectsEffectivePausesStandaloneCompactionAndShutdown() {
		using var dir = new TempDir();
		var backend = new FakeBackend { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
		backend.Script.Enqueue(_ => FakeBackend.Say("summary"));
		var m = Manager(dir, backend);
		var (project, agent) = m.CreateProject("p", [dir.Path], null, false);
		agent.SetPaused(true);
		Assert.Contains("paused", agent.StartFreshContext());
		agent.SetPaused(false);
		m.SetProjectPaused(project, true);
		Assert.Contains("paused", agent.StartFreshContext());
		m.SetProjectPaused(project, false);
		m.SetPausedAll(true);
		Assert.Contains("paused", agent.StartFreshContext());
		m.SetPausedAll(false);
		Exchange(agent, "before", "answer");
		var compact = agent.Compact("fake-1", default);
		await Wait.Until(() => agent.ModelCall is { Kind: ModelCallKind.Compaction, EndedAt: null });
		Assert.Contains("active", agent.StartFreshContext());
		agent.SetPaused(true);
		backend.Gate.SetResult();
		await Wait.Until(() => agent.Status == AgentStatus.Paused);
		Assert.Contains("active", agent.StartFreshContext());
		agent.SetPaused(false);
		await compact.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Null(agent.StartFreshContext());
		var boundary = agent.Transcript.Last;
		await m.Restart.DrainAsync(TimeSpan.Zero, TimeSpan.FromSeconds(5), () => {});
		Assert.Contains("stopping", agent.StartFreshContext());
		Assert.Equal(boundary, agent.Transcript.Last);
	}

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
	public async Task OversizedSummaryRetriesWithoutInlineImagesOrReplay() {
		using var dir = new TempDir();
		var backend = new FakeBackend();
		var m = Manager(dir, backend);
		var (_, orch) = m.CreateProject("p", [dir.Path], null, false);
		var task = m.SpawnTask(orch, "worker", false, null, null);
		Exchange(task, "first", "answer");
		task.Transcript.Append(new Entry { Kind = EntryKind.Reasoning, Text = "readable", Raw = JsonSerializer.SerializeToElement("opaque") });
		task.Transcript.Append(new Entry { Kind = EntryKind.ToolCall, CallId = "x", Name = "read", Arguments = "{}" });
		task.Transcript.Append(new Entry { Kind = EntryKind.ToolResult, CallId = "x", Text = "figure", Images = [new("saved.png", "image/png")] });
		backend.Script.Enqueue(_ => throw new ContextFullException(32_000_001));
		backend.Script.Enqueue(_ => FakeBackend.Say("SUMMARY"));
		await task.Compact("fake-1", default);

		var retry = backend.Requests[1];
		Assert.Empty(retry.Tools);
		Assert.All(retry.History, e => { Assert.Null(e.Raw); Assert.Null(e.Images); });
		Assert.Contains(retry.History, e => e.Kind == EntryKind.Assistant && e.Text == "readable");
		Assert.Contains(retry.History, e => e.CallId == "x" && e.Text == "figure\nSaved images: saved.png");
		Assert.Contains("SUMMARY", Assert.Single(task.View()).Text);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task OversizedReplyAndSummaryCompactOnlyThePrefixThatFit(bool needsAnotherPass) {
		using var dir = new TempDir();
		var backend = new FakeBackend();
		var m = Manager(dir, backend);
		var (_, orch) = m.CreateProject("p", [dir.Path], null, false);
		var task = m.SpawnTask(orch, "worker", false, null, null);
		Exchange(task, "old", "old answer");
		ToolRound(task, "paired", "old result");
		Exchange(task, "recent", "recent answer");
		for(var i = 0; i < 3; i++) backend.Script.Enqueue(_ => throw new ContextFullException(50_000_000));
		backend.Script.Enqueue(_ => FakeBackend.Say("PREFIX SUMMARY"));
		if(needsAnotherPass) {
			backend.Script.Enqueue(_ => throw new ContextFullException(32_000_000 + 1));
			backend.Script.Enqueue(_ => FakeBackend.Say("CUMULATIVE SUMMARY"));
		}
		backend.Script.Enqueue(_ => FakeBackend.Say("reply"));
		task.Send(new Inbound("Sera", "now", SendMode.Queued));
		await Wait.Until(() => Idle(task));

		Assert.Equal(needsAnotherPass ? 7 : 5, backend.Requests.Count);
		var summarized = backend.Requests[3].History.SkipLast(1).ToList();
		Assert.Contains(summarized, e => e.Text == "old result");
		var reply = backend.Requests[4].History;
		Assert.Contains("PREFIX SUMMARY", reply[0].Text);
		Assert.Equal(backend.Requests[0].History.Skip(summarized.Count), reply.Skip(1));
		Assert.Contains(reply, e => e.Text == "recent");
		Assert.Contains(reply, e => e.Text == "now");
		Assert.Equal("rollover", task.Transcript.Entries.First(e => e.Kind == EntryKind.Compaction).Reason);
		Assert.Contains(task.Transcript.Entries, e => e.Text == "reply");
	}

	[Fact]
	public async Task IndivisibleOversizedSummaryLeavesViewIntact() {
		using var dir = new TempDir();
		var backend = new FakeBackend();
		var agent = Manager(dir, backend).CreateChat("t", dir.Path);
		Exchange(agent, "single input", "answer");
		var before = agent.View();
		for(var i = 0; i < 2; i++) backend.Script.Enqueue(_ => throw new ContextFullException(32_000_025));
		var error = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Compact("fake-1", default));
		Assert.Contains("requestBytes=32000025", error.Message);
		Assert.Equal(2, backend.Requests.Count);
		Assert.Equal(before, agent.View());
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
