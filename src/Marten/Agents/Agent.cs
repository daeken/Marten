using System.Text.Json.Nodes;
using Marten.Models;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Agents;

/// <summary>
/// One agent: an append-only log, a little state, and a loop that runs turns. A turn runs from a
/// wake until the agent goes idle. Immediate messages land between model calls, at a tool
/// boundary; queued messages wait until the agent is idle and then wake it.
/// </summary>
public sealed class Agent : IPowerShellOwner {
	public AgentInfo Info { get; private set; }
	public string Directory { get; }
	public Transcript Transcript { get; }
	public FileTracker Files { get; }
	public AgentStatus Status { get; private set; } = AgentStatus.Idle;
	public string? StatusDetail { get; private set; }
	public string Instructions { get; }

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
		state = JsonIO.Load(StatePath, () => new AgentState());
		Files = new FileTracker(state.FileHashes);
		Files.Changed += SaveState;
	}

	string StatePath => Path.Combine(Directory, "state.json");
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
		lock(gate) inbox.Add(message);
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
				reason = resumeReason ?? (inbox.Count > 0 ? "message" : null);
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
			Deliver(Take(_ => true));
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
			if(!failed) SetStatus(AgentStatus.Idle);
		} catch(OperationCanceledException) when(cts.IsCancellationRequested) {
			AnswerDanglingCalls("Interrupted by Sera before this finished.");
			Append(new Entry { Kind = EntryKind.Idle, Reason = "interrupted" });
			SetStatus(AgentStatus.Idle, "Interrupted");
		} finally {
			turnCts = null;
		}
		if(PendingCount > 0) signal.Release();
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
		while(true) {
			ct.ThrowIfCancellationRequested();
			try {
				var model = Info.Model ?? await manager.DefaultModel(ct);
				var request = new ModelRequest(model, Info.Effort ?? manager.Config.Effort, Instructions,
					Transcript.Entries, manager.Tools.Specs, CacheKey: $"marten:{Info.Id}");
				SetStatus(AgentStatus.Running);
				return await manager.Backend.CompleteAsync(request,
					delta => manager.Publish(new { type = "delta", agentId = Info.Id, text = delta }), ct);
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
