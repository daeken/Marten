using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Marten.Models;
using Marten.Projects;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Agents;

/// <summary>A message an agent sent to Sera; shown in the UI inbox.</summary>
/// <summary>Something an agent put in front of Sera: a message, or a question card she answers.</summary>
public sealed record SeraMessage(string Id, DateTimeOffset At, string From, string FromName, string? ProjectId, string Text) {
	public bool Question { get; init; }
	/// <summary>Choices on a question card; Sera can also answer in her own words.</summary>
	public List<string>? Options { get; init; }
	public string? Context { get; init; }
	public string? Answer { get; init; }
	public DateTimeOffset? AnsweredAt { get; init; }
	/// <summary>Null for an agent's own question; "approval" or "tool_review" for cards the guard puts up.</summary>
	public string? Kind { get; init; }
	public string? Reference { get; init; }
}

/// <summary>A message one agent sent another, logged so the UI can show every exchange.</summary>
public sealed record AgentMessage(DateTimeOffset At, string FromId, string FromName, string ToId, string ToName, string? ProjectId, SendMode Mode, string Text, string? Reason);

public sealed class AgentManager {
	public Home Home { get; }
	public Config Config { get; private set; }
	public IModelBackend Backend { get; }
	internal Func<TimeSpan, CancellationToken, Task> ModelRetryDelay { get; init; } = Task.Delay;
	internal TimeProvider NetworkClock { get; }
	internal Func<double> NetworkJitter { get; init; } = Random.Shared.NextDouble;
	internal NetworkRecoveryPacer NetworkPacer { get; }
	public ToolRegistry Tools { get; }
	/// <summary>Starts agents' background processes; Program shares the PowerShell tool's child watcher with it.</summary>
	public ProcessLauncher Launcher { get; init; } = new();
	/// <summary>MCP servers' per-agent instances; set by Program.</summary>
	public Marten.Mcp.McpHub? Mcp { get; set; }
	internal RestartDrain Restart { get; init; } = new();
	public LoopBreaker Loops { get; }
	public Guard Guard { get; }
	public Outside Outside { get; }
	internal ResourceSlots Resources { get; }
	readonly ConcurrentDictionary<string, Agent> agents = new();
	readonly ConcurrentDictionary<string, Project> projects = new();
	readonly SemaphoreSlim modelGate = new(1, 1);
	readonly object inboxGate = new();
	string? resolvedDefaultModel;

	/// <summary>Raised for every change the UI shows: entries, deltas, status, flags.</summary>
	public event Action<object>? Events;

	public AgentManager(Home home, Config config, IModelBackend backend, ToolRegistry tools) : this(home, config, backend, tools, TimeProvider.System) { }

	internal AgentManager(Home home, Config config, IModelBackend backend, ToolRegistry tools, TimeProvider networkClock) {
		NetworkClock = networkClock;
		NetworkPacer = new(networkClock);
		Home = home;
		Config = config;
		Resources = new ResourceSlots(config);
		Backend = backend;
		Loops = new LoopBreaker(() => Config);
		Guard = new Guard(this);
		Outside = new Outside(this);
		Tools = tools;
		tools.Gate = Guard.Check;
		PausedAll = File.Exists(home.PausedPath);
		WriteDefaultPrompts();
	}

	// ---- Pause ----

	readonly object pauseGate = new();
	TaskCompletionSource pauseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

	volatile bool pausedAll;
	public bool PausedAll { get => pausedAll; private set => pausedAll = value; }

	/// <summary>Completes at the next pause or resume anywhere; agents holding on a pause wait on it.</summary>
	public Task PauseSignal { get { lock(pauseGate) return pauseTcs.Task; } }

	public void SetPausedAll(bool paused) {
		if(PausedAll == paused) return;
		PausedAll = paused;
		if(paused) File.WriteAllText(Home.PausedPath, DateTimeOffset.UtcNow.ToString("O"));
		else File.Delete(Home.PausedPath);
		PauseChanged();
	}

	public void SetProjectPaused(Project project, bool paused) {
		if(project.Info.Paused == paused) return;
		project.Update(i => i with { Paused = paused });
		PauseChanged();
	}

	public void PauseChanged() {
		TaskCompletionSource old;
		lock(pauseGate) {
			old = pauseTcs;
			pauseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
		}
		old.SetResult();
		foreach(var a in agents.Values) a.PauseChangedHere();
		Publish(new { type = "agents" });
	}

	public IEnumerable<Agent> Agents => agents.Values.OrderBy(a => a.Info.Created);
	public Agent? Get(string id) => agents.GetValueOrDefault(id);
	public IEnumerable<Project> Projects => projects.Values.OrderBy(p => p.Info.Created);
	public Project? GetProject(string? id) => id == null ? null : projects.GetValueOrDefault(id);
	public Project? ProjectOf(Agent agent) => GetProject(agent.Info.ProjectId);

	public void Publish(object ev) => Events?.Invoke(ev);

	public void UpdateConfig(Func<Config, Config> change) {
		Config = change(Config);
		Config.Save(Home);
		if(Config.Model != null) resolvedDefaultModel = null;
		Publish(new { type = "config" });
	}

	/// <summary>The default model if known without a network call.</summary>
	public string? KnownDefaultModel => Config.Model ?? resolvedDefaultModel;

	public async Task<string> DefaultModel(CancellationToken ct) {
		if(Config.Model != null) return Config.Model;
		if(resolvedDefaultModel != null) return resolvedDefaultModel;
		await modelGate.WaitAsync(ct);
		try {
			Restart.ThrowIfStopping();
			resolvedDefaultModel ??= (await Backend.ListModelsAsync(ct)).FirstOrDefault()
				?? throw new InvalidOperationException("The backend lists no models. Set one in config.json.");
			return resolvedDefaultModel;
		} finally {
			modelGate.Release();
		}
	}

	/// <summary>Load every project and agent from disk, repair interrupted turns, and start their loops.</summary>
	public void LoadAll() {
		foreach(var dir in Directory.GetDirectories(Home.Projects))
			if(Project.Load(dir) is { } project) projects[project.Info.Id] = project;
		foreach(var dir in Directory.GetDirectories(Home.Agents)) {
			var infoPath = Path.Combine(dir, "agent.json");
			if(!File.Exists(infoPath)) continue;
			var info = JsonIO.Load<AgentInfo?>(infoPath, () => null);
			if(info == null) continue;
			var instructions = File.ReadAllText(Path.Combine(dir, "system.md"));
			var agent = new Agent(this, dir, info, instructions);
			agents[info.Id] = agent;
		}
		foreach(var agent in agents.Values) agent.Recover();
		foreach(var agent in agents.Values) agent.Start();
	}

	static string NewId(string prefix) => $"{prefix}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

	public Agent CreateChat(string? name, string? workingDirectory = null) =>
		CreateAgent(new AgentInfo {
			Id = NewId("chat"), Kind = AgentKind.Chat, Name = string.IsNullOrWhiteSpace(name) ? "Chat" : name.Trim(),
			Created = DateTimeOffset.UtcNow,
			WorkingDirectory = workingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
		});

	/// <summary>Create a project bound to existing directories, and spawn its orchestrator.</summary>
	public (Project Project, Agent Orchestrator) CreateProject(string name, IEnumerable<string> bindings, string? prompt, bool worktreesByDefault) {
		if(string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A project needs a name.");
		var resolved = bindings.Select(b => b.Trim()).Where(b => b.Length > 0).Select(ExpandHome).Select(Path.GetFullPath).Distinct().ToList();
		if(resolved.Count == 0) throw new ArgumentException("Bind the project to at least one directory or repo.");
		foreach(var b in resolved)
			if(!Directory.Exists(b)) throw new ArgumentException($"{b} doesn't exist.");
		var project = Project.Create(Home.Projects, name, resolved, prompt, worktreesByDefault);
		projects[project.Info.Id] = project;
		var orchestrator = CreateAgent(new AgentInfo {
			Id = NewId("orch"), Kind = AgentKind.Orchestrator, Name = "Orchestrator", Created = DateTimeOffset.UtcNow,
			WorkingDirectory = resolved[0], ProjectId = project.Info.Id,
		});
		project.Update(p => p with { OrchestratorId = orchestrator.Info.Id });
		Publish(new { type = "projects" });
		return (project, orchestrator);
	}

	/// <summary>Spawn a task agent under an orchestrator, optionally in its own git worktree.</summary>
	public Agent SpawnTask(Agent orchestrator, string name, bool? worktree, string? model, string? effort) {
		var project = ProjectOf(orchestrator) ?? throw new InvalidOperationException("Only a project's orchestrator can spawn task agents.");
		var id = NewId("task");
		var workdir = project.Info.Bindings[0];
		string? worktreePath = null;
		if(worktree ?? project.Info.WorktreesByDefault) {
			worktreePath = Path.Combine(project.WorktreesDir, id);
			Directory.CreateDirectory(project.WorktreesDir);
			Git(workdir, "worktree", "add", "-b", $"marten/{id}", worktreePath);
			workdir = worktreePath;
		}
		return CreateAgent(new AgentInfo {
			Id = id, Kind = AgentKind.Task, Name = string.IsNullOrWhiteSpace(name) ? "Task" : name.Trim(),
			Created = DateTimeOffset.UtcNow, WorkingDirectory = workdir, ProjectId = project.Info.Id,
			ParentId = orchestrator.Info.Id, Worktree = worktreePath, Model = model, Effort = effort,
		});
	}

	static void Git(string repo, params string[] args) {
		var psi = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
		foreach(var a in args) psi.ArgumentList.Add(a);
		using var p = Marten.Tools.ChildProcessWatcher.StartExcluded(psi)!;
		var err = p.StandardError.ReadToEnd();
		p.StandardOutput.ReadToEnd();
		p.WaitForExit();
		if(p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {err.Trim()}");
	}

	static string ExpandHome(string path) =>
		path == "~" || path.StartsWith("~/")
			? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "")
			: path;

	Agent CreateAgent(AgentInfo info) {
		var dir = Home.AgentDir(info.Id);
		Directory.CreateDirectory(dir);
		if(info.Kind == AgentKind.Task) AtomicFile.Write(Path.Combine(dir, "NOTES.md"), "");
		// The system prompt is fixed at spawn so the cached prefix stays stable; compaction rebuilds it.
		var instructions = BuildInstructions(info, dir, out var memory);
		AtomicFile.Write(Path.Combine(dir, "system.md"), instructions);
		JsonIO.Save(Path.Combine(dir, "agent.json"), info);
		var agent = new Agent(this, dir, info, instructions);
		if(memory is { } snapshot) agent.SawMemory(snapshot.Index, snapshot.Topics);
		agents[info.Id] = agent;
		agent.Start();
		Publish(new { type = "agents" });
		return agent;
	}

	/// <summary>A fresh system prompt for compaction: current project prompt, AGENTS.md, memory and notes.</summary>
	internal string RebuildInstructions(Agent agent, out (string Index, List<string> Topics)? memory) =>
		BuildInstructions(agent.Info, agent.Directory, out memory);

	/// <summary>The hand-edited prompts an agent's system prompt is built from, so a change can trigger a reload.</summary>
	public List<string> PromptSources(AgentInfo info) {
		var sources = new List<string> { ReadPrompt("marten.md"), ReadPrompt($"{info.Kind.ToString().ToLowerInvariant()}.md") };
		if(GetProject(info.ProjectId) is { } project) sources.Add(project.Prompt);
		return sources.Where(s => s.Length > 0).ToList();
	}

	string BuildInstructions(AgentInfo info, string dir, out (string Index, List<string> Topics)? memory) {
		memory = null;
		var kind = info.Kind.ToString().ToLowerInvariant();
		var parts = new List<string> { ReadPrompt("marten.md"), ReadPrompt($"{kind}.md") };
		var project = GetProject(info.ProjectId);
		if(project != null) {
			var sb = new StringBuilder();
			sb.AppendLine($"# Project: {project.Info.Name} ({project.Info.Id})");
			sb.AppendLine("Bound locations:");
			foreach(var b in project.Info.Bindings) sb.AppendLine($"- {b}");
			if(info.Worktree != null) sb.AppendLine($"You work in your own git worktree: {info.Worktree} (branch marten/{info.Id}).");
			if(project.Prompt.Length > 0) sb.AppendLine().AppendLine(project.Prompt);
			parts.Add(sb.ToString().TrimEnd());
			foreach(var (path, text) in project.AgentsFiles())
				if(text.Length > 0) parts.Add($"# {path}\n\n{text}");
			var index = project.Memory;
			var topics = project.MemoryTopics();
			memory = (index, topics);
			parts.Add($"# Project memory\n\nThe shared index lives at {project.MemoryPath}. This is how it stood when this prompt was built; changes since then reach you as notes at the start of a turn.\n\n{(index.Trim() is { Length: > 0 } m ? m : "(empty)")}\n\n{DescribeMemoryTopics(project, topics)}");
			if(info.Kind == AgentKind.Task) {
				var notesPath = Path.Combine(dir, "NOTES.md");
				var notes = File.Exists(notesPath) ? File.ReadAllText(notesPath).Trim() : "";
				parts.Add($"# Your notes\n\nYour own NOTES.md is {notesPath}. Keep traces, open leads and how your tools behave there; it survives compaction. Promote facts that matter beyond you to project memory.\n\n{(notes.Length > 0 ? notes : "(empty so far)")}");
			}
		}
		parts.Add($"""
			# Environment
			- You are {info.Id} ({kind}), named "{info.Name}"{(info.ParentId != null ? $", spawned by orchestrator {info.ParentId}" : "")}
			- Working directory: {info.WorkingDirectory}
			- OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})
			- Marten home: {Home.Root}
			- Spawned: {info.Created:yyyy-MM-dd HH:mm} UTC
			""".Trim());
		return string.Join("\n\n", parts);
	}

	internal static string DescribeMemoryTopics(Project project, List<string> topics) =>
		$"Memory topics (paths relative to the index's directory, {project.Directory}; use read_file for current contents):\n" +
		(topics.Count == 0 ? "(none yet; use edit_file to create a file under memory/ and link it from the index)" :
			string.Join("\n", topics.Select(t => $"- {JsonIO.Serialize(t)}")));

	string ReadPrompt(string file) {
		var path = Path.Combine(Home.Prompts, file);
		return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
	}

	void WriteDefaultPrompts() {
		var asm = Assembly.GetExecutingAssembly();
		foreach(var resource in asm.GetManifestResourceNames().Where(n => n.Contains(".Prompts.") && n.EndsWith(".md"))) {
			var file = resource[(resource.IndexOf(".Prompts.") + ".Prompts.".Length)..];
			var path = Path.Combine(Home.Prompts, file);
			using var stream = asm.GetManifestResourceStream(resource)!;
			using var reader = new StreamReader(stream);
			var text = reader.ReadToEnd();
			// Keep Sera's edits; refresh prompts she never touched when Marten ships new defaults.
			var stampPath = path + ".default";
			var current = File.Exists(path) ? File.ReadAllText(path) : null;
			var pristine = current == null
				|| (File.Exists(stampPath) ? current == File.ReadAllText(stampPath) : LegacyDefaults.Contains(current.Trim()));
			if(!pristine) continue;
			AtomicFile.Write(path, text);
			AtomicFile.Write(stampPath, text);
		}
	}

	/// <summary>Defaults shipped before prompts were stamped, so untouched copies can still be upgraded.</summary>
	static readonly HashSet<string> LegacyDefaults = [
		"You are a project's orchestrator. You run the project and spawn task agents to own tasks or portions of the work.",
		"You are a task agent. You own one task or one portion of a project's work, and report to your orchestrator.",
		"""
		You are an agent running inside Marten, Sera's agent runtime. Sera trusts you: there are no approvals and no sandbox, so act directly and own the consequences.

		How to act on the world:
		- Your tools are callable directly. Prefer the direct tools for reading and editing files.
		- `powershell` runs PowerShell 7 in-process, in a fresh runspace each time. Only `$State` survives between runs, and it must stay plain JSON data. `$Marten` exposes the runtime. Every Marten tool is also a PowerShell function with the same name.
		- PowerShell runs inside Marten itself. Environment variables you set and assemblies you load are shared with every other agent and with Marten, so avoid changing process-wide state unless you mean to.
		- Commands time out. Pass `timeout_seconds` for work that legitimately takes long, such as a full test run.
		- `edit_file` applies anchored edits atomically and refuses if the file changed since you read it. Read first, then edit.
		- Oversized tool output is truncated with a pointer to the full text. Read ranges of it rather than re-running the command.

		Be direct and concise with Sera. Say what you did and what you found, not what you are about to do.
		""",
		"You are a chat agent: someone Sera talks to outside any project. You are not bound to a repository; your working directory is Sera's home directory unless she says otherwise.",
	];

	// ---- Messages to Sera ----

	public void MessageSera(Agent from, string text) {
		var msg = new SeraMessage(Guid.NewGuid().ToString("N")[..12], DateTimeOffset.UtcNow, from.Info.Id, from.Info.Name, from.Info.ProjectId, text);
		lock(inboxGate) File.AppendAllText(Home.SeraInboxPath, JsonIO.Serialize(msg) + "\n");
		Publish(new { type = "sera_message", message = msg });
	}

	public SeraMessage AskSera(Agent from, string question, List<string>? options, string? context, string? kind = null, string? reference = null) {
		var msg = new SeraMessage(Guid.NewGuid().ToString("N")[..12], DateTimeOffset.UtcNow, from.Info.Id, from.Info.Name, from.Info.ProjectId, question) {
			Question = true, Options = options is { Count: > 0 } ? options : null, Context = context, Kind = kind, Reference = reference,
		};
		lock(inboxGate) File.AppendAllText(Home.SeraInboxPath, JsonIO.Serialize(msg) + "\n");
		Publish(new { type = "sera_message", message = msg });
		return msg;
	}

	/// <summary>Record Sera's answer on a card and send it to the agent that asked, queued.</summary>
	public SeraMessage AnswerQuestion(string id, int? choice, string? text) {
		SeraMessage answered;
		bool firstOption;
		string? note;
		lock(inboxGate) {
			var all = SeraInbox();
			var i = all.FindIndex(m => m.Id == id && m.Question);
			if(i < 0) throw new KeyNotFoundException($"No question {id}.");
			if(all[i].Answer != null) throw new InvalidOperationException("That question is already answered.");
			var picked = choice is int c && all[i].Options is { } opts && c >= 0 && c < opts.Count ? opts[c] : null;
			var words = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
			if(picked == null && words == null) throw new ArgumentException("Pick an option or write an answer.");
			var answer = picked != null && words != null ? $"{picked}: {words}" : picked ?? words!;
			firstOption = choice == 0 && picked != null;
			note = words;
			answered = all[i] = all[i] with { Answer = answer, AnsweredAt = DateTimeOffset.UtcNow };
			AtomicFile.Write(Home.SeraInboxPath, string.Concat(all.Select(m => JsonIO.Serialize(m) + "\n")));
		}
		Publish(new { type = "sera_message", message = answered });
		if(Guard.Answered(answered, firstOption, note)) return answered;
		if(Get(answered.From) is { } asker)
			asker.Send(new Inbound("Sera", $"Sera answered your question \"{answered.Text}\":\n\n{answered.Answer}", SendMode.Queued, "answer"));
		return answered;
	}

	// ---- Messages between agents ----

	string AgentMessagesPath => Path.Combine(Home.Root, "messages.jsonl");
	readonly object messagesGate = new();

	internal void LogAgentMessage(AgentMessage m) {
		lock(messagesGate) File.AppendAllText(AgentMessagesPath, JsonIO.Serialize(m) + "\n");
		Publish(new { type = "agent_message", message = m });
	}

	public List<AgentMessage> AgentMessages(string? agentId = null, string? projectId = null, int limit = 500) {
		lock(messagesGate) {
			if(!File.Exists(AgentMessagesPath)) return [];
			return File.ReadLines(AgentMessagesPath).Where(l => l.Length > 0)
				.Select(l => { try { return JsonIO.Deserialize<AgentMessage>(l); } catch { return null; } })
				.OfType<AgentMessage>()
				.Where(m => agentId == null || m.FromId == agentId || m.ToId == agentId)
				.Where(m => projectId == null || m.ProjectId == projectId)
				.TakeLast(limit).ToList();
		}
	}

	public List<SeraMessage> SeraInbox() {
		lock(inboxGate) {
			if(!File.Exists(Home.SeraInboxPath)) return [];
			return File.ReadLines(Home.SeraInboxPath).Where(l => l.Length > 0)
				.Select(l => { try { return JsonIO.Deserialize<SeraMessage>(l); } catch { return null; } })
				.OfType<SeraMessage>().ToList();
		}
	}

	/// <summary>Name an agent in messages: "Name (id)".</summary>
	public static string Label(Agent a) => $"{a.Info.Name} ({a.Info.Id})";
}
