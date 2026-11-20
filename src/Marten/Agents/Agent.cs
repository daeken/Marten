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
	volatile AgentInfo info;
	public AgentInfo Info { get => info; private set => info = value; }
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
	Turn? activeTurn;

	// Serialized by gate. CancelAsync marks the token immediately without running arbitrary
	// cancellation callbacks under the lifecycle lock. Drain those callbacks before disposal.
	sealed class Turn {
		public readonly CancellationTokenSource Cancellation = new();
		public Task Cancelled { get; private set; } = Task.CompletedTask;
		public bool Closed { get; private set; }
		public void Cancel(bool closed = false) {
			Closed |= closed;
			if(!Cancellation.IsCancellationRequested) Cancelled = Cancellation.CancelAsync();
		}
	}
	string? resumeReason;
	MartenApi? api;

	public Agent(AgentManager manager, string directory, AgentInfo info, string instructions) {
		this.manager = manager;
		Directory = directory;
		this.info = info;
		Instructions = instructions;
		Transcript = new Transcript(Path.Combine(directory, "transcript"));
		var entries = Transcript.Entries;
		if(entries.LastOrDefault(e => e.Kind is EntryKind.Compaction or EntryKind.Reload or EntryKind.ContextReset)?.Instructions is { } rebuilt)
			Instructions = rebuilt;
		state = JsonIO.Load(StatePath, () => new AgentState());
		if(entries.LastOrDefault(e => e.Kind == EntryKind.ContextReset) is { } reset && state.ContextResetSeq != reset.Seq)
			// The marker is durable even if shutdown interrupted the derived state writes. Never diff against old baselines.
			state = WithFreshContextBaselines(reset.Seq, null, null);
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
	readonly AsyncLocal<CancellationToken> toolTurn = new();
	public object MartenObjectForRun(CancellationToken ct) => new MartenApi(this, manager, ct, toolTurn.Value);

	internal void CheckNestedTool(CancellationToken turnToken, CancellationToken runToken) {
		lock(gate) {
			turnToken.ThrowIfCancellationRequested();
			runToken.ThrowIfCancellationRequested();
			if(Info.Closed || IsPaused) throw new ToolException("Cannot start another tool while the agent is closed or paused.");
		}
	}
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
			SaveClosed(closed);
			if(closed) {
				resumeReason = null;
				activeTurn?.Cancel(closed: true);
			}
		}
		if(closed) {
			_ = StopAllProcesses();
			_ = manager.Mcp?.StopFor(Info.Id);
		} else signal.Release();
		manager.Publish(new { type = "agents" });
	}

	// Called with gate held so a deliberate reopen and its message are one lifecycle transition.
	void SaveClosed(bool closed) {
		var updated = Info with { Closed = closed };
		JsonIO.Save(Path.Combine(Directory, "agent.json"), updated);
		Info = updated;
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
		var entries = ContextEntries();
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
		var entries = ContextEntries();
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
		if(message.From == "Sera") manager.Loops.Reset(Info.Id);
		if(message.FromId is { } fromId && manager.Get(fromId) is { } sender)
			manager.LogAgentMessage(new AgentMessage(DateTimeOffset.UtcNow, fromId, sender.Info.Name, Info.Id, Info.Name,
				Info.ProjectId ?? sender.Info.ProjectId, message.Mode, message.Text, message.Reason));
		bool reopened = false;
		lock(gate) {
			if(Info.Closed) {
				if(message.From == NudgeSender && message.Reason == null) return;
				// Turn errors, schedule output, answers to old questions, etc. cannot reopen an agent.
				if(message.Reason == null && (message.From == "Sera" ||
					(message.FromId != null && message.FromId == Info.ParentId))) {
					SaveClosed(false);
					reopened = true;
				}
			}
			inbox.Add(message);
			if(message.From == "Sera" && (state.NudgeStreak != 0 || state.NudgesStopped))
				state = state with { NudgeStreak = 0, NudgesStopped = false };
		}
		if(reopened) manager.Publish(new { type = "agents" });
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
	public void Interrupt() { lock(gate) activeTurn?.Cancel(); }

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
			using var operation = manager.Restart.Enter();
			if(operation == null) return;
			string reason;
			Turn turn;
			lock(gate) {
				if(activeTurn != null || Info.Closed || (resumeReason == null && inbox.Count == 0)) continue;
				var reasons = inbox.Select(m => m.Reason ?? (m.From == NudgeSender ? "nudge" : "message")).Distinct().ToList();
				reason = resumeReason ?? (reasons.Count == 1 ? reasons[0] : "message");
				resumeReason = null;
				activeTurn = turn = new Turn();
			}
			try {
				await RunTurn(reason, turn);
			} catch(OperationCanceledException) when(manager.Restart.IsStopping) {
				// Leave the Wake and any unanswered calls for Recover, not the Sera interrupt path.
				return;
			} finally {
				lock(gate) activeTurn = null;
				await turn.Cancelled.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
				turn.Cancellation.Dispose();
			}
			if(PendingCount > 0 && !Info.Closed) signal.Release();
		}
	}

	async Task RunTurn(string reason, Turn turn) {
		var token = turn.Cancellation.Token;
		using var slots = new TurnSlots(this, token);
		using var modelCts = CancellationTokenSource.CreateLinkedTokenSource(token, manager.Restart.Stopping);
		using var toolCts = CancellationTokenSource.CreateLinkedTokenSource(token, manager.Restart.CancelTools);
		try {
			await WaitWhilePaused(modelCts.Token);
			lock(gate) {
				slots.ThrowIfStopped(modelCts.Token);
				SetStatus(AgentStatus.Running);
				Append(new Entry { Kind = EntryKind.Wake, Reason = reason });
				ReloadChangedPrompt();
				NoteMemoryChanges();
				NoteToolChanges();
				Deliver(Take(_ => true));
			}
			var changesAtStart = taskChanges;
			while(true) {
				manager.Restart.ThrowIfStopping();
				var (result, view) = await CallModel(slots, modelCts.Token);
				var calls = new List<Entry>();
				await CommitModelOutput(slots, modelCts.Token, () => {
					RecordUsage(result, view);
					foreach(var item in result.Items) {
						var e = Append(item);
						if(e.Kind == EntryKind.ToolCall) calls.Add(e);
					}
				});
				// No tool boundary left in this turn: anything that arrived during the reply wakes a new one.
				if(calls.Count == 0) break;
				foreach(var call in calls) {
					await RunTool(call, slots, toolCts.Token);
				}
				lock(gate) {
					slots.ThrowIfStopped(modelCts.Token);
					Deliver(Take(m => m.Mode == SendMode.Immediate));
				}
			}
			lock(gate) {
				slots.ThrowIfStopped(modelCts.Token);
				Append(new Entry { Kind = EntryKind.Idle });
				SetStatus(AgentStatus.Idle);
				activeTurn = null;
			}
			MaybeNudge(changesAtStart);
		} catch(OperationCanceledException) when(manager.Restart.IsStopping) {
			throw;
		} catch(Exception) when(token.IsCancellationRequested && !manager.Restart.IsStopping) {
			AnswerDanglingCalls(turn.Closed ? "Cancelled because the agent was closed. Check for partial effects before retrying." : "Interrupted by Sera before this finished.");
			Append(new Entry { Kind = EntryKind.Idle, Reason = "interrupted" });
			SetStatus(AgentStatus.Idle, turn.Closed ? "Closed" : "Interrupted");
		} catch(Exception e) {
			FailTurn(e);
		}
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

	/// <summary>
	/// The system prompt is fixed at spawn for caching, but a rule Sera edits into the project prompt (or
	/// marten.md, or the kind prompt) has to take effect. When one of those changed since the prompt was
	/// built, rebuild it now, once, and say so at the end of the context. Costs one cache miss.
	/// </summary>
	void ReloadChangedPrompt() {
		var current = manager.ReadPromptSnapshot(Info);
		PromptSnapshot? seen;
		lock(gate) seen = state.PromptSeen;
		// No baseline means we cannot tell whether old rules were removed. Rebuild once even if all sources are empty.
		if(seen == current) return;
		var instructions = manager.RebuildInstructions(this, current, out var memory);
		Append(new Entry { Kind = EntryKind.Reload, Instructions = instructions });
		Instructions = instructions;
		AtomicFile.Write(Path.Combine(Directory, "system.md"), instructions);
		if(memory is { } snapshot) SawMemory(snapshot.Index, snapshot.Topics);
		var reason = seen == null ? "there was no saved prompt baseline" : "its source prompts changed";
		var text = $"Your system prompt was just reloaded because {reason}. The new version replaces the old one: where it differs from instructions or rules you followed earlier in this conversation, the new system prompt wins.";
		if(seen != null) {
			text += DescribePromptChange("marten.md", seen.Marten, current.Marten);
			text += DescribePromptChange(current.KindFile, seen.Kind, current.Kind);
			text += DescribePromptChange("project prompt.md", seen.Project, current.Project);
		}
		Append(new Entry { Kind = EntryKind.Message, From = NudgeSender, Text = text });
		MarkPromptSeen(current);
		manager.Publish(new { type = "agents" });
	}

	static string DescribePromptChange(string label, string? before, string? after) {
		if(before == after) return "";
		var (added, removed) = LineDiff.Of(before ?? "", after ?? "");
		var lines = removed.Select(l => $"- removed: {l}").Concat(added.Select(l => $"+ added: {l}"));
		var diff = string.Join("\n", lines);
		return diff.Length == 0 ? $"\n\n{label} changed." : $"\n\n{label}:\n{diff}";
	}

	internal void MarkPromptSeen(PromptSnapshot prompts) {
		lock(gate) state = state with { PromptSeen = prompts };
		SaveState();
	}

	/// <summary>Called after this agent edits memory itself, so its own edit isn't reported back to it.</summary>
	public void SawMemory(string text, List<string>? topics = null) {
		lock(gate) state = state with { MemorySeen = text, MemoryTopicsSeen = topics ?? state.MemoryTopicsSeen };
		SaveState();
	}

	async Task RunTool(Entry call, TurnSlots slots, CancellationToken ct) {
		async Task Admit() {
			while(true) {
				await slots.EnsureAgent(ct);
				lock(gate) {
					slots.ThrowIfStopped(ct);
					manager.Restart.ThrowIfStopping();
					if(IsPaused) continue;
					return;
				}
			}
		}
		ToolOutput output;
		await Admit();
		manager.Publish(new { type = "tool_start", agentId = Info.Id, callId = call.CallId, name = call.Name });
		TakeViewedImages();
		toolTurn.Value = slots.TurnToken;
		try {
			output = await manager.Tools.InvokeAsync(this, call.Name ?? "", call.Arguments, ct, Admit);
		} catch(OperationCanceledException) when(ct.IsCancellationRequested || slots.TurnToken.IsCancellationRequested || manager.Restart.IsStopping) {
			throw;
		} catch(Exception e) {
			output = ToolOutput.Error($"Tool crashed: {e.GetType().Name}: {e.Message}");
		} finally {
			toolTurn.Value = default;
		}
		lock(gate) {
			slots.ThrowIfStopped(ct);
			var (text, full) = Truncate(call.CallId ?? Guid.NewGuid().ToString("N"), output.Text);
			List<ImageRef> images = [.. output.Images ?? [], .. TakeViewedImages()];
			Append(new Entry {
				Kind = EntryKind.ToolResult, CallId = call.CallId, Name = call.Name, Text = text,
				IsError = output.IsError ? true : null, FullOutput = full, Images = images.Count > 0 ? images : null,
			});
		}
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

	// A completion's model lease has already been returned. Don't retain the agent lease
	// while its reply is buffered; close must still invalidate this turn before commit.
	async Task CommitModelOutput(TurnSlots slots, CancellationToken ct, Action commit) {
		bool yielded = false;
		while(true) {
			lock(gate) {
				slots.ThrowIfStopped(ct);
				if(!IsPaused) {
					if(yielded) SetStatus(AgentStatus.Running);
					commit();
					return;
				}
			}
			slots.ReleaseAgent();
			yielded = true;
			await WaitWhilePaused(ct);
		}
	}

	ModelRequest AgentRequest(string model, IReadOnlyList<Entry> history) {
		var agent = Info;
		var global = manager.Config.Effort;
		return new(model, agent.Effort ?? global, Instructions, history, RequestTools(), $"marten:{agent.Id}") {
			EffortSource = agent.Effort != null ? "agent" : global != null ? "global" : "provider-default",
		};
	}

	async Task<(ModelResult Result, List<Entry> View)> CallModel(TurnSlots slots, CancellationToken ct) {
		var transientFailures = 0;
		var quotaWait = TimeSpan.FromSeconds(30);
		var recoveringOverflow = false;
		int? lastOversizedReply = null;
		var retainedSuffix = false;
		var forceCompaction = false;
		while(true) {
			manager.Restart.ThrowIfStopping();
			ct.ThrowIfCancellationRequested();
			try {
				await slots.EnsureAgent(ct);
				var model = Info.Model ?? await RecoverNetwork(slots, async (recovery, token) => {
					await slots.EnsureAgent(token);
					recovery?.CheckDeadline(token);
					return await manager.DefaultModel(token);
				}, ct);
				if(forceCompaction) {
					retainedSuffix = await Compact(model, slots, ct);
					forceCompaction = false;
				} else if(!recoveringOverflow) await ManageContext(model, slots, ct);
				var view = View();
				var request = AgentRequest(model, view);
				SetStatus(AgentStatus.Running);
				var result = await slots.Complete(request, ModelCallKind.Reply,
					delta => manager.Publish(new { type = "delta", agentId = Info.Id, text = delta }), null, ct);
				// Keep each call's usage in the log, on its last item.
				if(result.Usage != null && result.Items.Count > 0)
					result = result with { Items = [.. result.Items.SkipLast(1), result.Items[^1] with { Usage = result.Usage }] };
				return (result, view);
			} catch(Exception) when(ct.IsCancellationRequested || slots.TurnToken.IsCancellationRequested) {
				slots.ThrowIfStopped(ct);
				throw;
			} catch(ContextFullException e) when(!recoveringOverflow || (retainedSuffix && e.RequestBytes < lastOversizedReply)) {
				// A partial summary may need another pass, but only if the exact reply bytes fell.
				lastOversizedReply = e.RequestBytes;
				retainedSuffix = false;
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
