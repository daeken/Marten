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
	public AgentStatus Status => CurrentStatus.Status;
	public string? StatusDetail => CurrentStatus.Detail;
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
	public object MartenObjectForRun(CancellationToken ct) => new MartenApi(this, manager, ct);
	public IReadOnlyList<AbandonedRunspace> AbandonedRunspaces => state.AbandonedRunspaces;
	public bool NudgesStopped => state.NudgesStopped;
	public Project? Project => manager.ProjectOf(this);

	// ---- Task list ----

	public IReadOnlyList<TaskItem> Tasks {
		get {
			lock(gate) return (tasks ??= JsonIO.Load(TasksPath, () => new List<TaskItem>())).ToList();
		}
	}

	public TaskItem AddTask(string title, string assignedBy, string? check = null) {
		TaskItem item;
		lock(gate) {
			tasks ??= JsonIO.Load(TasksPath, () => new List<TaskItem>());
			item = new TaskItem { Id = tasks.Count == 0 ? 1 : tasks.Max(t => t.Id) + 1, Title = title.Trim(), AssignedBy = assignedBy, Updated = DateTimeOffset.UtcNow,
				Check = string.IsNullOrWhiteSpace(check) ? null : check.Trim() };
			tasks.Add(item);
			JsonIO.Save(TasksPath, tasks);
			taskChanges++;
		}
		manager.Publish(new { type = "tasks", agentId = Info.Id });
		return item;
	}

	public TaskItem? GetTask(int id) => Tasks.FirstOrDefault(t => t.Id == id);

	public void RecordCheck(int id, CheckRun run) {
		lock(gate) {
			tasks ??= JsonIO.Load(TasksPath, () => new List<TaskItem>());
			var i = tasks.FindIndex(t => t.Id == id);
			if(i < 0) return;
			tasks[i] = tasks[i] with { LastCheck = run };
			JsonIO.Save(TasksPath, tasks);
		}
		manager.Publish(new { type = "tasks", agentId = Info.Id });
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
		if(closed) {
			_ = StopAllProcesses();
			_ = manager.Mcp?.StopFor(Info.Id);
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

	/// <summary>Repair the log after a restart and decide whether a turn was cut short.</summary>
	public void Recover() {
		AnswerDanglingCalls("Interrupted: Marten stopped while this was running. It was not re-run; check its effects before retrying.");
		var entries = Transcript.Entries;
		var lastWake = entries.LastOrDefault(e => e.Kind == EntryKind.Wake)?.Seq ?? 0;
		var lastIdle = entries.LastOrDefault(e => e.Kind == EntryKind.Idle)?.Seq ?? 0;
		var interruptedTurn = lastWake > lastIdle && Transcript.Last?.Kind != EntryKind.Assistant;
		if(lastWake > lastIdle && !interruptedTurn) Append(new Entry { Kind = EntryKind.Idle });
		var interruptedOperations = RecoverSchedules() + RecoverNotebookReplays();
		Append(new Entry { Kind = EntryKind.Message, From = NudgeSender, Text =
			"Marten restarted. Interrupted calls were not re-run. Check logs, files, and external state before retrying; partial effects are possible." +
			(interruptedTurn ? " Your previous turn was interrupted." : "") + interruptedOperations });
		if(!Info.Closed && (interruptedTurn || interruptedOperations.Length > 0)) resumeReason = "restart";
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
		if(message.From == "Sera") manager.Loops.Reset(Info.Id);
		if(message.FromId is { } fromId && manager.Get(fromId) is { } sender)
			manager.LogAgentMessage(new AgentMessage(DateTimeOffset.UtcNow, fromId, sender.Info.Name, Info.Id, Info.Name,
				Info.ProjectId ?? sender.Info.ProjectId, message.Mode, message.Text, message.Reason));
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
		var stopping = manager.Restart.Stopping;
		while(!manager.Restart.IsStopping) {
			try { await signal.WaitAsync(stopping); }
			catch(OperationCanceledException) when(stopping.IsCancellationRequested) { return; }
			string? reason;
			lock(gate) {
				var reasons = inbox.Select(m => m.Reason ?? (m.From == NudgeSender ? "nudge" : "message")).Distinct().ToList();
				reason = resumeReason ?? reasons.Count switch { 0 => null, 1 => reasons[0], _ => "message" };
				resumeReason = null;
			}
			if(reason == null) continue;
			using var turn = manager.Restart.Enter();
			if(turn == null) return;
			try {
				await WaitWhilePaused(stopping);
				manager.Restart.ThrowIfStopping();
				await RunTurn(reason);
			} catch(OperationCanceledException) when(manager.Restart.IsStopping) {
				// Leave the Wake and any unanswered calls for Recover, not the Sera interrupt path.
				return;
			} catch(Exception e) {
				FailTurn(e);
			}
		}
	}

	async Task RunTurn(string reason) {
		using var cts = new CancellationTokenSource();
		turnCts = cts;
		using var slots = new TurnSlots(this);

		using var modelCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, manager.Restart.Stopping);
		using var toolCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, manager.Restart.CancelTools);
		try {
			SetStatus(AgentStatus.Running);
			Append(new Entry { Kind = EntryKind.Wake, Reason = reason });
			NoteMemoryChanges();
			NoteToolChanges();
			Deliver(Take(_ => true));
			var changesAtStart = taskChanges;
			while(true) {
				manager.Restart.ThrowIfStopping();
				var result = await CallModel(slots, modelCts.Token);
				modelCts.Token.ThrowIfCancellationRequested();
				var calls = new List<Entry>();
				foreach(var item in result.Items) {
					var e = Append(item);
					if(e.Kind == EntryKind.ToolCall) calls.Add(e);
				}
				// No tool boundary left in this turn: anything that arrived during the reply wakes a new one.
				if(calls.Count == 0) break;
				foreach(var call in calls) {
					manager.Restart.ThrowIfStopping();
					await RunTool(call, toolCts.Token);
				}
				Deliver(Take(m => m.Mode == SendMode.Immediate));
			}
			Append(new Entry { Kind = EntryKind.Idle });
			SetStatus(AgentStatus.Idle);
			MaybeNudge(changesAtStart);
		} catch(OperationCanceledException) when(cts.IsCancellationRequested && !manager.Restart.IsStopping) {
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
		if(manager.Restart.IsStopping || Info.Closed || IsPaused || PendingCount > 0) return;
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
		var topics = project.MemoryTopics();
		string? seen;
		List<string>? topicsSeen;
		lock(gate) (seen, topicsSeen) = (state.MemorySeen, state.MemoryTopicsSeen);
		var topicsChanged = topicsSeen == null || !topicsSeen.SequenceEqual(topics);
		if(seen == current && !topicsChanged) return;
		SawMemory(current, topics);
		if(seen != null && seen != current) {
			var (added, removed) = LineDiff.Of(seen, current);
			if(added.Count > 0 || removed.Count > 0) {
				var lines = removed.Select(l => $"- removed: {l}").Concat(added.Select(l => $"+ added: {l}"));
				Append(new Entry { Kind = EntryKind.Message, From = NudgeSender, Text = $"Project memory changed since your last turn:\n{string.Join("\n", lines)}" });
			}
		}
		if(topicsChanged)
			Append(new Entry { Kind = EntryKind.Message, From = NudgeSender, Text = AgentManager.DescribeMemoryTopics(project, topics) });
	}

	/// <summary>Called after this agent edits memory itself, so its own edit isn't reported back to it.</summary>
	public void SawMemory(string text, List<string>? topics = null) {
		lock(gate) state = state with { MemorySeen = text, MemoryTopicsSeen = topics ?? state.MemoryTopicsSeen };
		SaveState();
	}

	async Task RunTool(Entry call, CancellationToken ct) {
		manager.Publish(new { type = "tool_start", agentId = Info.Id, callId = call.CallId, name = call.Name });
		ToolOutput output;
		TakeViewedImages();
		try {
			output = await manager.Tools.InvokeAsync(this, call.Name ?? "", call.Arguments, ct);
		} catch(OperationCanceledException) when(ct.IsCancellationRequested) {
			throw;
		} catch(Exception e) {
			output = ToolOutput.Error($"Tool crashed: {e.GetType().Name}: {e.Message}");
		}
		manager.Restart.CancelTools.ThrowIfCancellationRequested();
		ct.ThrowIfCancellationRequested();
		var (text, full) = Truncate(call.CallId ?? Guid.NewGuid().ToString("N"), output.Text);
		List<ImageRef> images = [.. output.Images ?? [], .. TakeViewedImages()];
		Append(new Entry {
			Kind = EntryKind.ToolResult, CallId = call.CallId, Name = call.Name, Text = text,
			IsError = output.IsError ? true : null, FullOutput = full, Images = images.Count > 0 ? images : null,
		});
	}

	/// <summary>Truncate an oversized result on arrival, keeping its head and tail and saving the full text.</summary>
	internal (string Text, string? FullOutput) Truncate(string callId, string text) {
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

	async Task<ModelResult> CallModel(TurnSlots slots, CancellationToken ct) {
		var transientFailures = 0;
		var quotaWait = TimeSpan.FromSeconds(30);
		var recoveringOverflow = false;
		var forceCompaction = false;
		while(true) {
			manager.Restart.ThrowIfStopping();
			ct.ThrowIfCancellationRequested();
			try {
				await slots.EnsureAgent(ct);
				var model = Info.Model ?? await manager.DefaultModel(ct);
				if(forceCompaction) {
					await Compact(model, slots, ct);
					forceCompaction = false;
				} else if(!recoveringOverflow) await ManageContext(model, slots, ct);
				var view = View();
				var request = new ModelRequest(model, Info.Effort ?? manager.Config.Effort, Instructions,
					view, RequestTools(), CacheKey: $"marten:{Info.Id}");
				SetStatus(AgentStatus.Running);
				var result = await slots.Complete(request, ModelCallKind.Reply,
					delta => manager.Publish(new { type = "delta", agentId = Info.Id, text = delta }), null, ct);
				RecordUsage(result, view);
				// Keep each call's usage in the log, on its last item.
				if(result.Usage != null && result.Items.Count > 0)
					result = result with { Items = [.. result.Items.SkipLast(1), result.Items[^1] with { Usage = result.Usage }] };
				return result;
			} catch(Exception) when(ct.IsCancellationRequested) {
				ct.ThrowIfCancellationRequested();
				throw;
			} catch(ContextFullException) when(!recoveringOverflow) {
				// Keep the forced compaction inside the retry loop, including its own quota/transient failures.
				recoveringOverflow = forceCompaction = true;
			} catch(RateLimitedException e) {
				slots.ReleaseAgent();
				// Wait it out; never fail and never switch models.
				var wait = e.RetryAfter ?? quotaWait;
				SetStatus(AgentStatus.WaitingForQuota, $"{e.Message} Retrying at {DateTimeOffset.Now + wait:HH:mm}.");
				await manager.ModelRetryDelay(wait, ct);
				quotaWait = TimeSpan.FromSeconds(Math.Min(quotaWait.TotalSeconds * 2, 15 * 60));
			} catch(TransientModelException e) when(++transientFailures <= 5) {
				slots.ReleaseAgent();
				SetStatus(AgentStatus.Running, $"{e.Message} Retrying.");
				await manager.ModelRetryDelay(TimeSpan.FromSeconds(Math.Pow(2, transientFailures)), ct);
			}
		}
	}
}
