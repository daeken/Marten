using System.Text.Json.Nodes;

namespace Marten.Agents;

public enum AgentKind { Chat, Orchestrator, Task }

public enum AgentStatus { Idle, Running, WaitingForQuota, Error }

/// <summary>Who an agent is. Written once at spawn; agent.json.</summary>
public sealed record AgentInfo {
	public required string Id { get; init; }
	public required AgentKind Kind { get; init; }
	public required string Name { get; init; }
	public required DateTimeOffset Created { get; init; }
	public required string WorkingDirectory { get; init; }
	/// <summary>Per-agent overrides of the configured model and effort.</summary>
	public string? Model { get; init; }
	public string? Effort { get; init; }
	/// <summary>The project an orchestrator or task agent belongs to; null for chat agents.</summary>
	public string? ProjectId { get; init; }
	/// <summary>The orchestrator that spawned a task agent.</summary>
	public string? ParentId { get; init; }
	/// <summary>A closed task agent is never nudged; only its orchestrator (or Sera) wakes it.</summary>
	public bool Closed { get; init; }
	/// <summary>The git worktree this agent works in, if it was spawned with one.</summary>
	public string? Worktree { get; init; }
}

/// <summary>What an agent carries between turns besides its log; state.json.</summary>
public sealed record AgentState {
	public Dictionary<string, string> FileHashes { get; init; } = new();
	public JsonNode? PowerShellState { get; init; }
	public List<AbandonedRunspace> AbandonedRunspaces { get; init; } = new();
	/// <summary>Project memory as this agent last saw it, so each turn can tell it what changed.</summary>
	public string? MemorySeen { get; init; }
	/// <summary>Nudges sent in a row with no task changing state.</summary>
	public int NudgeStreak { get; init; }
	public bool NudgesStopped { get; init; }
}

public enum TaskState { Open, Waiting, Blocked, Done }

/// <summary>One entry in an agent's task list. Lives outside the context, so it survives compaction.</summary>
public sealed record TaskItem {
	public required int Id { get; init; }
	public required string Title { get; init; }
	public TaskState State { get; init; } = TaskState.Open;
	/// <summary>Why it is waiting or blocked, or a closing note when done.</summary>
	public string? Reason { get; init; }
	/// <summary>The agent that added it; woken when the task turns done, waiting or blocked.</summary>
	public required string AssignedBy { get; init; }
	public DateTimeOffset Updated { get; init; }
}

public sealed record AbandonedRunspace(DateTimeOffset At, string Command);

public enum SendMode { Immediate, Queued }

public sealed record Inbound(string From, string Text, SendMode Mode);
