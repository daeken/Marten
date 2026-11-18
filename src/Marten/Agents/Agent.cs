using System.Text.Json.Nodes;
using Marten.Models;
using Marten.Projects;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Agents;

/// <summary>
/// One agent: an append-only log, a little state, and a loop that runs turns. A turn runs from a
/// wake until the agent goes idle. Immediate messages land between model calls, at a tool
/// boundary; queued messages wait until the agent is idle and then wake it.
/// </summary>
public sealed partial class Agent : IPowerShellOwner {
	public AgentInfo Info { get; private set; }
	public string Directory { get; }
	public Transcript Transcript { get; }
	public FileTracker Files { get; }
	public AgentStatus Status { get; private set; } = AgentStatus.Idle;
	public string? StatusDetail { get; private set; }
	public string Instructions { get; private set; }

	readonly AgentManager manager;
	readonly object gate = new();
	readonly List<Inbound> inbox = new();
	readonly SemaphoreSlim signal = new(0);
	AgentState state;
	CancellationTokenSource? turnCts;
	string? resumeReason;
	MartenApi? api;

	public Agent(AgentManager manager, string directory, AgentInfo info, string instructions) {
		this.manager = manager;
		Directory = directory;
		Info = info;
		Instructions = instructions;
		Transcript = new Transcript(Path.Combine(directory, "transcript"));
		if(Transcript.Entries.LastOrDefault(e => e.Kind == EntryKind.Compaction)?.Instructions is { } rebuilt) Instructions = rebuilt;
		state = JsonIO.Load(StatePath, () => new AgentState());
		Files = new FileTracker(state.FileHashes);
		Files.Changed += SaveState;
	}

	string StatePath => Path.Combine(Directory, "state.json");
	string TasksPath => Path.Combine(Directory, "tasks.json");
	List<TaskItem>? tasks;
	/// <summary>Bumped whenever a task on this agent's list changes state; nudging watches it.</summary>
	int taskChanges;
	string OutputsDir => Path.Combine(Directory, "outputs");

	void SaveState() {
		lock(gate) {
			state = state with { FileHashes = Files.Snapshot.ToDictionary() };
			JsonIO.Save(StatePath, state);
		}
	}

	// IPowerShellOwner / IToolContext
	public string AgentId => Info.Id;
	public string WorkingDirectory => Info.WorkingDirectory;
	public ToolRegistry Tools => manager.Tools;
	public object MartenObject => api ??= new MartenApi(this, manager);
	public IReadOnlyList<AbandonedRunspace> AbandonedRunspaces => state.AbandonedRunspaces;
	public bool NudgesStopped => state.NudgesStopped;
	public Project? Project => manager.ProjectOf(this);

	// ---- Task list ----

	public IReadOnlyList<TaskItem> Tasks {
		get {
			lock(gate) return (tasks ??= JsonIO.Load(TasksPath, () => new List<TaskItem>())).ToList();
		}
	}

	public TaskItem AddTask(string title, string assignedBy) {
		TaskItem item;
		lock(gate) {
			tasks ??= JsonIO.Load(TasksPath, () => new List<TaskItem>());
			item = new TaskItem { Id = tasks.Count == 0 ? 1 : tasks.Max(t => t.Id) + 1, Title = title.Trim(), AssignedBy = assignedBy, Updated = DateTimeOffset.UtcNow };
			tasks.Add(item);
			JsonIO.Save(TasksPath, tasks);
			taskChanges++;
		}
		manager.Publish(new { type = "tasks", agentId = Info.Id });
		return item;
	}

	public (TaskItem Before, TaskItem After) UpdateTask(int id, TaskState taskState, string? reason) {
		TaskItem before, after;
		lock(gate) {
			tasks ??= JsonIO.Load(TasksPath, () => new List<TaskItem>());
			var i = tasks.FindIndex(t => t.Id == id);
			if(i < 0) throw new ToolException($"{Info.Name} has no task {id}.");
			before = tasks[i];
			after = tasks[i] = before with { State = taskState, Reason = reason, Updated = DateTimeOffset.UtcNow };
			JsonIO.Save(TasksPath, tasks);
			if(before.State != taskState) {
				taskChanges++;
				state = state with { NudgeStreak = 0, NudgesStopped = false };
			}
		}
		SaveState();
		manager.Publish(new { type = "tasks", agentId = Info.Id });
		return (before, after);
	}

	// ---- Lifecycle ----

	public void SetClosed(bool closed) {
		lock(gate) {
			if(Info.Closed == closed) return;
			Info = Info with { Closed = closed };
			JsonIO.Save(Path.Combine(Directory, "agent.json"), Info);
		}
		manager.Publish(new { type = "agents" });
	}

	public JsonNode? PowerShellState {
		get => state.PowerShellState?.DeepClone();
		set {
			lock(gate) state = state with { PowerShellState = value?.DeepClone() };
			SaveState();
		}
	}

	public void RunspaceAbandoned(string command) {
		lock(gate) state = state with { AbandonedRunspaces = [.. state.AbandonedRunspaces, new(DateTimeOffset.UtcNow, command)] };
		SaveState();
		manager.Publish(new { type = "flag", agentId = Info.Id, flag = "abandoned_runspace", command });
	}

	Entry Append(Entry entry) {
		var e = Transcript.Append(entry);
		manager.Publish(new { type = "entry", agentId = Info.Id, entry = e });
		return e;
	}

	void SetStatus(AgentStatus status, string? detail = null) {
		Status = status;
		StatusDetail = detail;
		manager.Publish(new { type = "status", agentId = Info.Id, status, detail });
	}

	/// <summary>Repair the log after a restart and decide whether a turn was cut short.</summary>
	public void Recover() {
		AnswerDanglingCalls("Interrupted: Marten stopped while this was running. It was not re-run; check its effects before retrying.");
		var entries = Transcript.Entries;
		var lastWake = entries.LastOrDefault(e => e.Kind == EntryKind.Wake)?.Seq ?? 0;
		var lastIdle = entries.LastOrDefault(e => e.Kind == EntryKind.Idle)?.Seq ?? 0;
		if(lastWake > lastIdle) {
			if(Transcript.Last?.Kind == EntryKind.Assistant) Append(new Entry { Kind = EntryKind.Idle });
			else resumeReason = "restart";
		}
	}

	/// <summary>Every tool call needs a result before the next model call; give unanswered ones an error.</summary>
	void AnswerDanglingCalls(string text) {
		var entries = Transcript.Entries;
		var answered = entries.Where(e => e.Kind == EntryKind.ToolResult).Select(e => e.CallId).ToHashSet();
		foreach(var call in entries.Where(e => e.Kind == EntryKind.ToolCall && !answered.Contains(e.CallId)))
			Append(new Entry {
				Kind = EntryKind.ToolResult, CallId = call.CallId, Name = call.Name, IsError = true,
				Text = text,
			});
	}

	public void Start() {
		_ = Task.Run(Loop);
		if(resumeReason != null) signal.Release();
	}

	public void Send(Inbound message) {
		if(Info.Closed) SetClosed(false);
		lock(gate) {
			inbox.Add(message);
			if(message.From == "Sera" && (state.NudgeStreak != 0 || state.NudgesStopped))
				state = state with { NudgeStreak = 0, NudgesStopped = false };
		}
		manager.Publish(new { type = "inbox", agentId = Info.Id, pending = PendingCount });
		signal.Release();
	}

	/// <summary>Change this agent's model or effort; null means the configured default. Takes effect at the next model call.</summary>
	public void UpdateSettings(string? model, string? effort) {
		lock(gate) {
			Info = Info with { Model = model, Effort = effort };
			JsonIO.Save(Path.Combine(Directory, "agent.json"), Info);
		}
		manager.Publish(new { type = "agents" });
	}

	public int PendingCount { get { lock(gate) return inbox.Count; } }

	/// <summary>Stop the current turn. The in-flight model call or tool is cancelled and the agent goes idle.</summary>
	public void Interrupt() => turnCts?.Cancel();

	List<Inbound> Take(Func<Inbound, bool> which) {
		lock(gate) {
			var taken = inbox.Where(which).ToList();
			inbox.RemoveAll(m => taken.Contains(m));
			return taken;
		}
	}

	void Deliver(IEnumerable<Inbound> messages) {
		foreach(var m in messages) Append(new Entry { Kind = EntryKind.Message, From = m.From, Text = m.Text });
		manager.Publish(new { type = "inbox", agentId = Info.Id, pending = PendingCount });
	}

	async Task Loop() {
		while(true) {
			await signal.WaitAsync();
			string? reason;
			lock(gate) {
				reason = resumeReason ?? (inbox.Count > 0 ? (inbox.All(m => m.From == NudgeSender) ? "nudge" : "message") : null);
				resumeReason = null;
			}
			if(reason == null) continue;
			try {
				await RunTurn(reason);
			} catch(Exception e) {
				Append(new Entry { Kind = EntryKind.Error, Text = $"Turn failed: {e.Message}" });
				Append(new Entry { Kind = EntryKind.Idle });
				SetStatus(AgentStatus.Error, e.Message);
			}
		}
	}

	async Task RunTurn(string reason) {
		using var cts = new CancellationTokenSource();
		turnCts = cts;
		try {
			SetStatus(AgentStatus.Running);
			Append(new Entry { Kind = EntryKind.Wake, Reason = reason });
			NoteMemoryChanges();
			Deliver(Take(_ => true));
			var changesAtStart = taskChanges;
			var failed = false;
			while(true) {
				var result = await CallModel(cts.Token);
				if(result == null) {
					failed = true;
					break;
				}
				var calls = new List<Entry>();
				foreach(var item in result.Items) {
					var e = Append(item);
					if(e.Kind == EntryKind.ToolCall) calls.Add(e);
				}
				// No tool boundary left in this turn: anything that arrived during the reply wakes a new one.
				if(calls.Count == 0) break;
				foreach(var call in calls) await RunTool(call, cts.Token);
				Deliver(Take(m => m.Mode == SendMode.Immediate));
			}
			Append(new Entry { Kind = EntryKind.Idle });
			if(!failed) {
				SetStatus(AgentStatus.Idle);
				MaybeNudge(changesAtStart);
			}
		} catch(OperationCanceledException) when(cts.IsCancellationRequested) {
			AnswerDanglingCalls("Interrupted by Sera before this finished.");
			Append(new Entry { Kind = EntryKind.Idle, Reason = "interrupted" });
			SetStatus(AgentStatus.Idle, "Interrupted");
		} finally {
			turnCts = null;
		}
		if(PendingCount > 0) signal.Release();
	}

	public const string NudgeSender = "Marten";

	/// <summary>
	/// An agent that goes idle with an open task is nudged awake. After three nudges in a row with no
	/// task changing state, nudging stops and the agent is flagged. Closed agents are never nudged.
	/// </summary>
	void MaybeNudge(int changesAtStart) {
		if(Info.Closed || PendingCount > 0) return;
		var open = Tasks.Where(t => t.State == TaskState.Open).ToList();
		if(open.Count == 0) return;
		bool stop;
		lock(gate) {
			var streak = taskChanges != changesAtStart ? 1 : state.NudgeStreak + 1;
			stop = streak > 3;
			state = state with { NudgeStreak = stop ? state.NudgeStreak : streak, NudgesStopped = stop };
		}
		SaveState();
		if(stop) {
			manager.Publish(new { type = "flag", agentId = Info.Id, flag = "nudges_stopped" });
			manager.Publish(new { type = "agents" });
			return;
		}
		var list = string.Join("\n", open.Select(t => $"- #{t.Id} {t.Title}"));
		Send(new Inbound(NudgeSender,
			$"You went idle with open tasks:\n{list}\nKeep going, or mark each one waiting, blocked or done with task_update if you mean to stop.",
			SendMode.Queued));
	}

	/// <summary>
	/// Memory sits in the system prompt as it stood at spawn. Later edits arrive here, at the start of a
	/// turn, as a note at the end of the context, so the cached prefix is never disturbed.
	/// </summary>
	void NoteMemoryChanges() {
		if(Project is not { } project) return;
		var current = project.Memory;
		string? seen;
		lock(gate) seen = state.MemorySeen;
		if(seen == null) {
			lock(gate) state = state with { MemorySeen = current };
			SaveState();
			return;
		}
		if(seen == current) return;
		var (added, removed) = LineDiff.Of(seen, current);
		lock(gate) state = state with { MemorySeen = current };
		SaveState();
		if(added.Count == 0 && removed.Count == 0) return;
		var lines = removed.Select(l => $"- removed: {l}").Concat(added.Select(l => $"+ added: {l}"));
		Append(new Entry { Kind = EntryKind.Message, From = NudgeSender, Text = $"Project memory changed since your last turn:\n{string.Join("\n", lines)}" });
	}

	/// <summary>Called after this agent edits memory itself, so its own edit isn't reported back to it.</summary>
	public void SawMemory(string text) {
		lock(gate) state = state with { MemorySeen = text };
		SaveState();
	}

	async Task RunTool(Entry call, CancellationToken ct) {
		manager.Publish(new { type = "tool_start", agentId = Info.Id, callId = call.CallId, name = call.Name });
		ToolOutput output;
		try {
			output = await manager.Tools.InvokeAsync(this, call.Name ?? "", call.Arguments, ct);
		} catch(OperationCanceledException) when(ct.IsCancellationRequested) {
			throw;
		} catch(Exception e) {
			output = ToolOutput.Error($"Tool crashed: {e.GetType().Name}: {e.Message}");
		}
		ct.ThrowIfCancellationRequested();
		var (text, full) = Truncate(call.CallId ?? Guid.NewGuid().ToString("N"), output.Text);
		Append(new Entry {
			Kind = EntryKind.ToolResult, CallId = call.CallId, Name = call.Name, Text = text,
			IsError = output.IsError ? true : null, FullOutput = full,
		});
	}

	/// <summary>Truncate an oversized result on arrival, keeping its head and tail and saving the full text.</summary>
	(string Text, string? FullOutput) Truncate(string callId, string text) {
		var max = manager.Config.ToolResultMaxChars;
		if(text.Length <= max) return (text, null);
		System.IO.Directory.CreateDirectory(OutputsDir);
		var safe = string.Concat(callId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
		var path = Path.Combine(OutputsDir, $"{safe}.txt");
		AtomicFile.Write(path, text);
		var head = text[..(max * 2 / 3)];
		var tail = text[^(max / 3)..];
		var note = $"\n[... truncated: {text.Length:N0} characters in all. The full output is in {path}; read ranges of it with read_file ...]\n";
		return (head + note + tail, Path.GetRelativePath(Directory, path));
	}

	async Task<ModelResult?> CallModel(CancellationToken ct) {
		var transientFailures = 0;
		var quotaWait = TimeSpan.FromSeconds(30);
		var compactedForOverflow = false;
		while(true) {
			ct.ThrowIfCancellationRequested();
			try {
				var model = Info.Model ?? await manager.DefaultModel(ct);
				await ManageContext(model, ct);
				var view = View();
				var request = new ModelRequest(model, Info.Effort ?? manager.Config.Effort, Instructions,
					view, manager.Tools.SpecsFor(this), CacheKey: $"marten:{Info.Id}");
				SetStatus(AgentStatus.Running);
				var result = await manager.Backend.CompleteAsync(request,
					delta => manager.Publish(new { type = "delta", agentId = Info.Id, text = delta }), ct);
				RecordUsage(result, view);
				return result;
			} catch(ContextFullException) when(!compactedForOverflow) {
				// The estimate was off; compact now and try once more.
				compactedForOverflow = true;
				await Compact(Info.Model ?? await manager.DefaultModel(ct), ct);
			} catch(RateLimitedException e) {
				// Wait it out; never fail and never switch models.
				var wait = e.RetryAfter ?? quotaWait;
				SetStatus(AgentStatus.WaitingForQuota, $"{e.Message} Retrying at {DateTimeOffset.Now + wait:HH:mm}.");
				await Task.Delay(wait, ct);
				quotaWait = TimeSpan.FromSeconds(Math.Min(quotaWait.TotalSeconds * 2, 15 * 60));
			} catch(TransientModelException e) when(++transientFailures <= 5) {
				SetStatus(AgentStatus.Running, $"{e.Message} Retrying.");
				await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, transientFailures)), ct);
			} catch(Exception e) when(e is TransientModelException or NotConnectedException or ContextFullException or InvalidOperationException) {
				Append(new Entry { Kind = EntryKind.Error, Text = e.Message });
				SetStatus(AgentStatus.Error, e.Message);
				return null;
			}
		}
	}
}
