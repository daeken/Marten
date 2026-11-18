using System.Diagnostics;
using System.Text.Json;
using Marten.Agents;
using Marten.Models;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Tests;

/// <summary>Scripts per agent kind (by id prefix), so concurrent agents don't steal each other's replies.</summary>
public sealed class RoutedBackend : IModelBackend {
	public readonly Dictionary<string, Queue<Func<ModelRequest, ModelResult>>> Scripts = new();
	public readonly List<ModelRequest> Requests = new();
	public string Provider => "fake";
	public bool Connected => true;
	public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["fake-1"]);

	public void Add(string prefix, Func<ModelRequest, ModelResult> step) {
		lock(Scripts) {
			if(!Scripts.TryGetValue(prefix, out var q)) Scripts[prefix] = q = new();
			q.Enqueue(step);
		}
	}

	public Task<ModelResult> CompleteAsync(ModelRequest request, Action<string> onTextDelta, CancellationToken ct) {
		var id = request.CacheKey["marten:".Length..];
		lock(Scripts) {
			Requests.Add(request);
			foreach(var (prefix, q) in Scripts)
				if(id.StartsWith(prefix) && q.Count > 0) return Task.FromResult(q.Dequeue()(request));
		}
		return Task.FromResult(FakeBackend.Say("(nothing scripted)"));
	}
}

public class ProjectTests {
	static AgentManager Manager(TempDir dir, IModelBackend backend, Config? config = null) {
		AgentManager? m = null;
		Func<AgentManager> get = () => m!;
		var tools = new ToolRegistry()
			.Add(new ReadFileTool()).Add(new EditFileTool(new MemoryGuard(get)))
			.Add(new SendMessageTool(get)).Add(new TaskAddTool(get)).Add(new TaskUpdateTool(get))
			.Add(new SpawnAgentTool(get)).Add(new CloseAgentTool(get)).Add(new CreateProjectTool(get))
			.Add(new ElideResultsTool()).Add(new SetModelTool(get));
		return m = new AgentManager(new Home(dir.File("home")), config ?? new Config(), backend, tools);
	}

	static string Args(object o) => JsonSerializer.Serialize(o);
	static bool Idle(Agent a) => a.Status != AgentStatus.Running && a.Transcript.Last?.Kind == EntryKind.Idle && a.PendingCount == 0;

	static TempDir RepoDir() {
		var dir = new TempDir();
		Directory.CreateDirectory(dir.File("repo"));
		File.WriteAllText(dir.File("repo/AGENTS.md"), "Always run the tests.\n");
		return dir;
	}

	[Fact]
	public void ProjectsCarryBindingsAgentsMdAndMemoryIntoThePrompt() {
		using var dir = RepoDir();
		var m = Manager(dir, new RoutedBackend());
		var (project, orch) = m.CreateProject("Open Thedas", [dir.File("repo")], "Ship the renderer.", false);
		Assert.StartsWith("open-thedas-", project.Info.Id);
		Assert.Equal(orch.Info.Id, project.Info.OrchestratorId);
		Assert.Equal(dir.File("repo"), orch.WorkingDirectory);
		Assert.Contains("Ship the renderer.", orch.Instructions);
		Assert.Contains("Always run the tests.", orch.Instructions);
		Assert.Contains(project.MemoryPath, orch.Instructions);
		Assert.Contains("spawn_agent", m.Tools.SpecsFor(orch).Select(t => t.Name));
		Assert.DoesNotContain("create_project", m.Tools.SpecsFor(orch).Select(t => t.Name));
		Assert.Throws<ArgumentException>(() => m.CreateProject("x", [dir.File("missing")], null, false));
	}

	[Fact]
	public async Task OrchestratorSpawnsATaskAgentAndIsWokenWhenItsTaskIsDone() {
		using var dir = RepoDir();
		var backend = new RoutedBackend();
		var m = Manager(dir, backend);
		var (_, orch) = m.CreateProject("p", [dir.File("repo")], null, false);
		backend.Add("orch", _ => FakeBackend.Call("o1", "spawn_agent", Args(new { name = "parser", brief = "Write the parser.", task = "Parser", worktree = (bool?) null, model = (string?) null, effort = (string?) null })));
		backend.Add("orch", _ => FakeBackend.Say("Spawned."));
		backend.Add("task", _ => FakeBackend.Call("t1", "task_update", Args(new { id = 1, state = "done", reason = "parser written" })));
		backend.Add("task", _ => FakeBackend.Say("Done."));
		backend.Add("orch", _ => FakeBackend.Say("Great."));
		orch.Send(new Inbound("Sera", "build a parser", SendMode.Queued));

		await Wait.Until(() => m.Agents.Any(a => a.Info.Kind == AgentKind.Task));
		var task = m.Agents.Single(a => a.Info.Kind == AgentKind.Task);
		await Wait.Until(() => Idle(task) && Idle(orch) && orch.Transcript.Entries.Any(e => e.Text == "Great."));

		Assert.Equal(orch.Info.Id, task.Info.ParentId);
		Assert.Contains(task.Transcript.Entries, e => e.Kind == EntryKind.Message && e.Text == "Write the parser.");
		Assert.Equal(TaskState.Done, task.Tasks.Single().State);
		Assert.Contains(orch.Transcript.Entries, e => e.Kind == EntryKind.Message && e.Text!.Contains("\"Parser\" is now done: parser written"));
		Assert.Contains("NOTES.md", task.Instructions);
	}

	[Fact]
	public void MessagingFollowsTheSpecTable() {
		using var dir = RepoDir();
		var m = Manager(dir, new RoutedBackend());
		var (_, orchA) = m.CreateProject("a", [dir.File("repo")], null, false);
		var (_, orchB) = m.CreateProject("b", [dir.File("repo")], null, false);
		var chat = m.CreateChat("c");
		var taskA = m.SpawnTask(orchA, "ta", false, null, null);
		var taskA2 = m.SpawnTask(orchA, "ta2", false, null, null);
		var taskB = m.SpawnTask(orchB, "tb", false, null, null);

		Assert.Null(MessagingRules.Check(chat, orchA, SendMode.Immediate));
		Assert.NotNull(MessagingRules.Check(chat, taskA, SendMode.Queued));
		Assert.Null(MessagingRules.Check(orchA, taskA, SendMode.Immediate));
		Assert.NotNull(MessagingRules.Check(orchA, taskB, SendMode.Queued));
		Assert.Null(MessagingRules.Check(orchA, orchB, SendMode.Queued));
		Assert.NotNull(MessagingRules.Check(orchA, orchB, SendMode.Immediate));
		Assert.Null(MessagingRules.Check(orchA, chat, SendMode.Queued));
		Assert.Null(MessagingRules.Check(taskA, taskA2, SendMode.Queued));
		Assert.Null(MessagingRules.Check(taskA, orchA, SendMode.Queued));
		Assert.NotNull(MessagingRules.Check(taskA, orchA, SendMode.Immediate));
		Assert.NotNull(MessagingRules.Check(taskA, taskB, SendMode.Queued));

		taskA.SetClosed(true);
		Assert.NotNull(MessagingRules.Check(taskA2, taskA, SendMode.Queued));
		Assert.Null(MessagingRules.Check(orchA, taskA, SendMode.Queued));
	}

	[Fact]
	public async Task MemoryEnforcesCitationsAndCapAndAudits() {
		using var dir = RepoDir();
		var m = Manager(dir, new RoutedBackend(), new Config { MemoryCapBytes = 300 });
		var (project, orch) = m.CreateProject("p", [dir.File("repo")], null, false);
		var task = m.SpawnTask(orch, "t", false, null, null);
		File.WriteAllText(project.MemoryPath, "- WINE PAUSED (Sera, 2026-11-18 02:54Z, msg_1): no Wine.\n- NOTE: builds are slow.\n");
		async Task<ToolOutput> Edit(Agent who, object edits) {
			await m.Tools.InvokeAsync(who, "read_file", Args(new { path = project.MemoryPath, start_line = (int?) null, end_line = (int?) null }), default);
			return await m.Tools.InvokeAsync(who, "edit_file", Args(new { path = project.MemoryPath, edits = new[] { edits }, reason = "test" }), default);
		}

		var denied = await Edit(task, new { op = "delete", anchor = "- WINE PAUSED (Sera, 2026-11-18 02:54Z, msg_1): no Wine.\n", text = (string?) null });
		Assert.True(denied.IsError);
		Assert.Contains("cited", denied.Text);

		var ok = await Edit(task, new { op = "replace", anchor = "builds are slow", text = "builds take 4 minutes" });
		Assert.False(ok.IsError, ok.Text);

		var tooBig = await Edit(task, new { op = "insert_after", anchor = "4 minutes.\n", text = string.Concat(Enumerable.Repeat("- FILLER: xxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\n", 10)) });
		Assert.True(tooBig.IsError);
		Assert.True(tooBig.Text.Contains("cap"), tooBig.Text);

		var removed = await Edit(orch, new { op = "delete", anchor = "- WINE PAUSED (Sera, 2026-11-18 02:54Z, msg_1): no Wine.\n", text = (string?) null });
		Assert.False(removed.IsError, removed.Text);
		Assert.Contains(m.SeraInbox(), msg => msg.Text.Contains("WINE PAUSED"));

		var log = project.MemoryLog();
		Assert.Equal(2, log.Count);
		Assert.Equal(task.Info.Id, log[0].AgentId);
		Assert.Equal(["- NOTE: builds take 4 minutes."], log[0].Added);
	}

	[Fact]
	public async Task LinkedMemoryHolds170KbAcross21TopicsWithoutInjectingBodies() {
		using var dir = RepoDir();
		var backend = new RoutedBackend();
		var m = Manager(dir, backend);
		var (project, orch) = m.CreateProject("p", [dir.File("repo")], null, false);
		var originalInstructions = orch.Instructions;
		Assert.Equal(0, new FileInfo(project.MemoryPath).Length);
		await m.Tools.InvokeAsync(orch, "read_file", Args(new { path = project.MemoryPath }), default);
		const string pointer = "- RENDERER: [See topic](memory/topic-00.md)\n";
		var bootstrap = await EditMemory(m, orch, project.MemoryPath, "replace", "", pointer);
		Assert.False(bootstrap.IsError, bootstrap.Text);
		Assert.Equal(pointer, project.Memory);
		Assert.Equal("MEMORY.md", Assert.Single(project.MemoryLog()).Path);

		const string body = "PRIVATE_TOPIC_BODY_FOR_TEST";
		for(var i = 20; i >= 0; i--) {
			var path = Path.Combine(project.MemoryTopicsDirectory, $"topic-{i:D2}.md");
			var created = await EditMemory(m, orch, path, "create", null, body.PadRight(i == 0 ? 20_000 : 7_500, 'x'));
			Assert.False(created.IsError, created.Text);
		}
		Assert.Equal(170_000, Directory.GetFiles(project.MemoryTopicsDirectory).Sum(p => new FileInfo(p).Length));
		var topics = Enumerable.Range(0, 21).Select(i => $"memory/topic-{i:D2}.md").ToArray();
		Assert.Equal(topics, project.MemoryTopics());
		var task = m.SpawnTask(orch, "reader", false, null, null);
		Assert.Contains(pointer.Trim(), task.Instructions);
		Assert.All(topics, t => Assert.Contains(t, task.Instructions));
		Assert.DoesNotContain(body, task.Instructions);
		var rebuilt = m.RebuildInstructions(orch, out _);
		Assert.All(topics, t => Assert.Contains(t, rebuilt));
		Assert.DoesNotContain(body, rebuilt);
		orch.Send(new Inbound("Sera", "check topics", SendMode.Queued));
		await Wait.Until(() => Idle(orch));
		var note = Assert.Single(orch.Transcript.Entries, e => e.From == Agent.NudgeSender);
		Assert.All(topics, t => Assert.Contains(t, note.Text));
		Assert.DoesNotContain(body, note.Text);
		Assert.Equal(originalInstructions, Assert.Single(backend.Requests).Instructions);
		var read = await m.Tools.InvokeAsync(task, "read_file", Args(new { path = Path.Combine(project.MemoryTopicsDirectory, "topic-00.md") }), default);
		Assert.Contains(body, read.Text);
	}

	[Fact]
	public async Task ExistingAgentDiscoversTopicsWithoutChangingItsSavedPrompt() {
		using var dir = RepoDir();
		var old = Manager(dir, new RoutedBackend());
		var (project, orch) = old.CreateProject("p", [dir.File("repo")], null, false);
		const string legacyPrompt = "An existing agent's prompt without linked memory";
		File.WriteAllText(Path.Combine(orch.Directory, "system.md"), legacyPrompt);
		JsonIO.Save(Path.Combine(orch.Directory, "state.json"), new AgentState { MemorySeen = "" });
		Directory.CreateDirectory(project.MemoryTopicsDirectory);
		File.WriteAllText(Path.Combine(project.MemoryTopicsDirectory, "existing.md"), "Existing topic payload");
		var backend = new RoutedBackend();
		var m = Manager(dir, backend);
		m.LoadAll();
		var loaded = m.Get(orch.Info.Id)!;
		loaded.Send(new Inbound("Sera", "hi", SendMode.Queued));
		await Wait.Until(() => Idle(loaded));
		loaded.Send(new Inbound("Sera", "once more", SendMode.Queued));
		await Wait.Until(() => Idle(loaded));
		var note = Assert.Single(loaded.Transcript.Entries,
			e => e.From == Agent.NudgeSender && e.Text?.StartsWith("Memory topics (", StringComparison.Ordinal) == true);
		Assert.Contains("memory/existing.md", note.Text);
		Assert.DoesNotContain("Existing topic payload", note.Text);
		Assert.All(backend.Requests, r => Assert.Equal(legacyPrompt, r.Instructions));
	}

	static Task<ToolOutput> EditMemory(AgentManager m, Agent who, string path, string op, string? anchor, string? text) =>
		m.Tools.InvokeAsync(who, "edit_file", Args(new { path, edits = new[] { new { op, anchor, text } }, reason = "test" }), default);

	[Fact]
	public async Task TopicsEnforceProjectMembershipAndCitationsAndAuditTheirPaths() {
		using var dir = RepoDir();
		var m = Manager(dir, new RoutedBackend());
		var (project, orch) = m.CreateProject("p", [dir.File("repo")], null, false);
		var (_, outsider) = m.CreateProject("other", [dir.File("repo")], null, false);
		var task = m.SpawnTask(orch, "t", false, null, null);
		var path = Path.Combine(project.MemoryTopicsDirectory, "decisions.md");
		const string cited = "- RULE (Sera, 2026-11-18 02:54Z, msg_1): no Wine.\n";
		var deniedCreate = await EditMemory(m, outsider, path, "create", null, cited);
		Assert.True(deniedCreate.IsError);
		Assert.Contains("Only agents in this project", deniedCreate.Text);
		Assert.False(File.Exists(path));
		Assert.False((await EditMemory(m, task, path, "create", null, cited + "- NOTE: slow\n")).IsError);
		Assert.Contains("cited", (await EditMemory(m, task, path, "delete", cited, null)).Text);
		await m.Tools.InvokeAsync(orch, "read_file", Args(new { path }), default);
		Assert.False((await EditMemory(m, task, path, "replace", "slow", "fast")).IsError);
		Assert.Contains("changed since", (await EditMemory(m, orch, path, "delete", cited, null)).Text);
		await m.Tools.InvokeAsync(orch, "read_file", Args(new { path }), default);
		Assert.False((await EditMemory(m, orch, path, "delete", cited, null)).IsError);
		Assert.Equal("- NOTE: fast\n", File.ReadAllText(path));
		Assert.Contains(m.SeraInbox(), msg => msg.Text.Contains("memory/decisions.md") && msg.Text.Contains(cited.Trim()));
		var log = project.MemoryLog();
		Assert.Equal(3, log.Count);
		Assert.All(log, e => Assert.Equal("memory/decisions.md", e.Path));
		Assert.Equal([cited.Trim()], log[^1].Removed);
	}

	[Fact]
	public async Task LinkedMemoryRejectsSymlinkStorageAndGuardsDirectoryAliases() {
		if(OperatingSystem.IsWindows()) return; // Creating symlinks may require elevated privileges.
		using var dir = RepoDir();
		var m = Manager(dir, new RoutedBackend());
		var (project, orch) = m.CreateProject("p", [dir.File("repo")], null, false);
		var (_, outsider) = m.CreateProject("other", [dir.File("repo")], null, false);
		var repo = dir.File("repo");
		Directory.CreateSymbolicLink(project.MemoryTopicsDirectory, repo);
		var result = await EditMemory(m, orch, Path.Combine(project.MemoryTopicsDirectory, "escape.md"), "create", null, "escape");
		Assert.Contains("cannot be a link", result.Text);
		Assert.False(File.Exists(Path.Combine(repo, "escape.md")));
		Assert.Empty(project.MemoryTopics());
		Directory.Delete(project.MemoryTopicsDirectory);
		Directory.CreateDirectory(project.MemoryTopicsDirectory);
		var leaf = Path.Combine(project.MemoryTopicsDirectory, "link.md");
		File.CreateSymbolicLink(leaf, dir.File("no-target.md"));
		Assert.Contains("cannot be links", (await EditMemory(m, orch, leaf, "create", null, "overwrite link")).Text);
		Assert.Empty(project.MemoryTopics());

		var alias = dir.File("alias");
		Directory.CreateSymbolicLink(alias, project.MemoryTopicsDirectory);
		var topic = Path.Combine(alias, "topic.md");
		Assert.Contains("Only agents in this project", (await EditMemory(m, outsider, topic, "create", null, "bypass")).Text);
		Assert.False((await EditMemory(m, orch, topic, "create", null, "valid")).IsError);
		Assert.Equal(["memory/topic.md"], project.MemoryTopics());
		Assert.Equal("memory/topic.md", Assert.Single(project.MemoryLog()).Path);

		var sibling = Path.Combine(project.Directory, "memory-other", "file.md");
		Assert.False((await EditMemory(m, outsider, sibling, "create", null, "not project memory")).IsError);
		Assert.Single(project.MemoryLog());
	}

	[Fact]
	public async Task OtherAgentsSeeMemoryChangesAsATailNote() {
		using var dir = RepoDir();
		var backend = new RoutedBackend();
		var m = Manager(dir, backend);
		var (project, orch) = m.CreateProject("p", [dir.File("repo")], null, false);
		File.WriteAllText(project.MemoryPath, "- NOTE: hello\n");
		orch.Send(new Inbound("Sera", "hi", SendMode.Queued));
		await Wait.Until(() => Idle(orch));
		var note = orch.Transcript.Entries.Single(e => e.From == Agent.NudgeSender);
		Assert.Contains("+ added: - NOTE: hello", note.Text);
		// The system prompt didn't change, so the cached prefix is intact.
		Assert.All(backend.Requests, r => Assert.Equal(orch.Instructions, r.Instructions));
	}

	[Fact]
	public async Task IdleWithOpenTasksIsNudgedThreeTimesThenFlagged() {
		using var dir = RepoDir();
		var backend = new RoutedBackend();
		var m = Manager(dir, backend);
		var chat = m.CreateChat("c", dir.Path);
		chat.AddTask("something", chat.Info.Id);
		chat.Send(new Inbound("Sera", "hi", SendMode.Queued));
		await Wait.Until(() => chat.NudgesStopped && Idle(chat));
		Assert.Equal(3, chat.Transcript.Entries.Count(e => e.Kind == EntryKind.Wake && e.Reason == "nudge"));

		// A closed or done list stops nudges without flagging.
		var chat2 = m.CreateChat("c2", dir.Path);
		chat2.AddTask("x", chat2.Info.Id);
		chat2.UpdateTask(1, TaskState.Waiting, "on someone");
		chat2.Send(new Inbound("Sera", "hi", SendMode.Queued));
		await Wait.Until(() => Idle(chat2));
		await Task.Delay(200);
		Assert.DoesNotContain(chat2.Transcript.Entries, e => e.Reason == "nudge");
	}

	[Fact]
	public void WorktreeSpawnsGetTheirOwnBranch() {
		using var dir = RepoDir();
		void Git(params string[] args) {
			var psi = new ProcessStartInfo("git") { WorkingDirectory = dir.File("repo"), RedirectStandardOutput = true, RedirectStandardError = true };
			foreach(var a in args) psi.ArgumentList.Add(a);
			using var p = Marten.Tools.ChildProcessWatcher.StartExcluded(psi)!;
			p.WaitForExit();
			Assert.Equal(0, p.ExitCode);
		}
		Git("init", "-q");
		Git("-c", "user.email=t@t", "-c", "user.name=t", "add", ".");
		Git("-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "init");
		var m = Manager(dir, new RoutedBackend());
		var (project, orch) = m.CreateProject("p", [dir.File("repo")], null, worktreesByDefault: true);
		var task = m.SpawnTask(orch, "t", null, null, null);
		Assert.NotNull(task.Info.Worktree);
		Assert.True(File.Exists(Path.Combine(task.WorkingDirectory, "AGENTS.md")));
		Assert.StartsWith(project.WorktreesDir, task.WorkingDirectory);
		Assert.Contains("own git worktree", task.Instructions);
	}
}
