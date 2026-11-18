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
}

/// <summary>What an agent carries between turns besides its log; state.json.</summary>
public sealed record AgentState {
	public Dictionary<string, string> FileHashes { get; init; } = new();
	public JsonNode? PowerShellState { get; init; }
	public List<AbandonedRunspace> AbandonedRunspaces { get; init; } = new();
}

public sealed record AbandonedRunspace(DateTimeOffset At, string Command);

public enum SendMode { Immediate, Queued }

public sealed record Inbound(string From, string Text, SendMode Mode);
