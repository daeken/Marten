namespace Marten.Storage;

/// <summary>Sera's settings. Agents don't reason about these; they live in ~/.marten/config.json.</summary>
public sealed record Config {
	/// <summary>Default model slug. Null means "first listed model" at first use.</summary>
	public string? Model { get; init; }
	/// <summary>Reasoning effort sent to the backend, or null for the backend default.</summary>
	public string? Effort { get; init; }
	public int Port { get; init; } = 7450;
	/// <summary>Concurrent model completions (including compaction). Zero means unlimited.</summary>
	public int MaxConcurrentModelCalls { get; init; }
	/// <summary>Concurrent active agent turns. Zero means unlimited.</summary>
	public int MaxRunningAgents { get; init; }
	/// <summary>Named, opt-in PowerShell command limits, shared across all agents.</summary>
	public Dictionary<string, int> Slots { get; init; } = new();
	public int CommandTimeoutSeconds { get; init; } = 120;
	/// <summary>How long to wait for the next bytes of a model response, not for the whole response.</summary>
	public int ModelIdleTimeoutSeconds { get; init; } = Marten.Models.ModelHttp.DefaultIdleTimeoutSeconds;
	/// <summary>Shutdown tool-drain grace, and how long a stopped pipeline gets before its runspace is abandoned.</summary>
	public int StopGraceSeconds { get; init; } = 5;
	/// <summary>Tool results longer than this are truncated on arrival, with a pointer to the full text.</summary>
	public int ToolResultMaxChars { get; init; } = 30_000;
	/// <summary>Context window in tokens for models not listed in ContextWindows.</summary>
	public int ContextTokens { get; init; } = 200_000;
	/// <summary>Per-model context windows, by model slug.</summary>
	public Dictionary<string, int> ContextWindows { get; init; } = new();
	/// <summary>Compact when the context passes this fraction of the window.</summary>
	public double CompactAt { get; init; } = 0.75;
	/// <summary>Elide old tool results, in one batch, when they pass this fraction of the window.</summary>
	public double ElideAt { get; init; } = 0.3;
	/// <summary>Size cap on the project memory index (MEMORY.md), excluding topic files.</summary>
	public int MemoryCapBytes { get; init; } = 16 * 1024;
	/// <summary>Extra model ids per backend (anthropic, zai, deepseek), listed ahead of what the provider reports.</summary>
	public Dictionary<string, List<string>> Models { get; init; } = new();
	/// <summary>Daily dollar caps by backend (anthropic, zai, deepseek) or "total". A capped backend waits until local midnight.</summary>
	public Dictionary<string, decimal> Budgets { get; init; } = new();
	/// <summary>Dollars per million tokens by model id, overriding the built-in Claude prices. Unpriced models count tokens only.</summary>
	public Dictionary<string, Marten.Models.Price> Prices { get; init; } = new();
	/// <summary>MCP tools go straight into each agent's tool list while there are at most this many; past it they're found with tool_search.</summary>
	public int DirectToolLimit { get; init; } = 12;
	/// <summary>How long a task's completion check may run before it counts as failed.</summary>
	public int CheckTimeoutSeconds { get; init; } = 900;
	/// <summary>Loop breaker: two agents exchanging this many messages within LoopWindowMinutes, without Sera, are both paused. 0 turns it off.</summary>
	public int LoopMessages { get; init; } = 20;
	public double LoopWindowMinutes { get; init; } = 10;
	/// <summary>Snapshots of ~/.marten: where, how often, how many to keep.</summary>
	public BackupSettings Backup { get; init; } = new();
	/// <summary>Sera's filter on tool calls: the first matching rule allows, denies, or asks her. Empty means wide open.</summary>
	public List<Marten.Agents.GuardRule> Rules { get; init; } = new();
	/// <summary>Agent-written tools load only once Sera approves their exact text. Tools that exist when this is turned on are grandfathered.</summary>
	public bool ReviewTools { get; init; }
	/// <summary>MCP servers by name (stdio only). Their tools appear to agents as name__tool.</summary>
	public Dictionary<string, Marten.Mcp.McpServerConfig> Mcp { get; init; } = new();
	/// <summary>Host names Marten is also reached by through an HTTPS proxy on this machine, e.g. "mac.tailnet.ts.net" for `tailscale serve`. Marten itself still listens only on 127.0.0.1.</summary>
	public List<string> RemoteHosts { get; init; } = new();
	/// <summary>Extra Origin allowed to call the API, e.g. the Vite dev server.</summary>
	public string? DevOrigin { get; init; }

	public static Config Load(Home home) {
		var config = JsonIO.Load(home.ConfigPath, () => new Config());
		if(!File.Exists(home.ConfigPath)) JsonIO.Save(home.ConfigPath, config);
		return config;
	}

	public void Save(Home home) => JsonIO.Save(home.ConfigPath, this);
}
