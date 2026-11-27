using Marten.Models;
using System.Text.Json.Nodes;

namespace Marten.Agents;

public enum AgentKind { Chat, Orchestrator, Task }

public enum AgentStatus { Idle, Running, WaitingForQuota, Paused, Error, WaitingForSlot, WaitingForNetwork }

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
	/// <summary>Sera paused this agent: it holds before its next model call and isn't nudged.</summary>
	public bool Paused { get; init; }
}

/// <summary>The labeled, normalized prompt sources used for one system prompt. A null project means no project.</summary>
public sealed record PromptSnapshot(string Marten, string KindFile, string Kind, string? Project);

/// <summary>What an agent carries between turns besides its log; state.json.</summary>
public sealed record AgentState {
	public Dictionary<string, string> FileHashes { get; init; } = new();
	public JsonNode? PowerShellState { get; init; }
	public List<AbandonedRunspace> AbandonedRunspaces { get; init; } = new();
	/// <summary>Project memory as this agent last saw it, so each turn can tell it what changed.</summary>
	public string? MemorySeen { get; init; }
	/// <summary>The hand-edited prompts (marten.md, the kind prompt, the project prompt) as the system prompt last carried them.</summary>
	public PromptSnapshot? PromptSeen { get; init; }
	/// <summary>Reset whose rebuilt context baselines this state belongs to.</summary>
	public long ContextResetSeq { get; init; }
	/// <summary>Topic paths last shown to the agent. Null in pre-topic state files.</summary>
	public List<string>? MemoryTopicsSeen { get; init; }
	/// <summary>Nudges sent in a row with no task changing state.</summary>
	public int NudgeStreak { get; init; }
	public bool NudgesStopped { get; init; }
	/// <summary>Aggregate child turn progress at the last delivered project-idle notice. Cleared on material waiting-list edits.</summary>
	public long? ProjectIdleWorkSeen { get; init; }
	/// <summary>Input tokens the backend reported for the last request, and the last log entry it included.</summary>
	public long LastInputTokens { get; init; }
	public long LastInputSeq { get; init; }
	/// <summary>Dynamic tools (agent-written, MCP) sent with requests: fixed at spawn and compaction for caching.</summary>
	public List<ToolSpec>? DynamicTools { get; init; }
	/// <summary>Dynamic tools the agent has been told about, by name, with a hash of their spec.</summary>
	public Dictionary<string, string>? ToolsNoted { get; init; }
}

public enum TaskState { Open, Waiting, Blocked, Done }

public sealed record TaskDependency(string AgentId, int TaskId);

/// <summary>One entry in an agent's task list. Lives outside the context, so it survives compaction.</summary>
public sealed record TaskItem {
	public required int Id { get; init; }
	public required string Title { get; init; }
	public TaskState State { get; init; } = TaskState.Open;
	/// <summary>Why it is waiting or blocked, or a closing note when done.</summary>
	public string? Reason { get; init; }
	public TaskDependency? WaitingOn { get; init; }
	// Outbox: commit the wake in the same atomic write that removes WaitingOn. Cleared only
	// after delivery to the durable transcript; older tasks.json files have neither field.
	[System.Text.Json.Serialization.JsonInclude]
	internal string[]? PendingDependencyWakes { get; init; }
	/// <summary>The agent that added it; woken when the task turns done, waiting or blocked.</summary>
	public required string AssignedBy { get; init; }
	public DateTimeOffset Updated { get; init; }
	/// <summary>Completion contract: a shell command that must exit 0 before the task can be marked done. Only its assigner sets it.</summary>
	public string? Check { get; init; }
	/// <summary>The last time the check ran: when, whether it passed, and the tail of its output.</summary>
	public CheckRun? LastCheck { get; init; }
}

public sealed record CheckRun(DateTimeOffset At, bool Passed, int? ExitCode, string Output);

public sealed record AbandonedRunspace(DateTimeOffset At, string Command);

public enum SendMode { Immediate, Queued }

internal sealed record PendingMessage(string Id, string Text, SendMode Mode);

/// <summary>A message waiting for an agent. Reason, if set, is recorded as the wake reason ("schedule", say).</summary>
public sealed record Inbound(string From, string Text, SendMode Mode, string? Reason = null, string? FromId = null) {
	internal string? ComposerId { get; init; }

	/// <summary>A message from another agent; these are logged so the UI shows every exchange.</summary>
	public static Inbound FromAgent(Agent from, string text, SendMode mode) => new(AgentManager.Label(from), text, mode, FromId: from.Info.Id);
}
