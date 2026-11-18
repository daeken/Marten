namespace Marten.Storage;

/// <summary>Sera's settings. Agents don't reason about these; they live in ~/.marten/config.json.</summary>
public sealed record Config {
	/// <summary>Default model slug. Null means "first listed model" at first use.</summary>
	public string? Model { get; init; }
	/// <summary>Reasoning effort sent to the backend, or null for the backend default.</summary>
	public string? Effort { get; init; }
	public int Port { get; init; } = 7450;
	public int CommandTimeoutSeconds { get; init; } = 120;
	/// <summary>How long a stopped pipeline gets to return before its runspace is abandoned.</summary>
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
	/// <summary>Project memory size cap. An edit past it fails, so agents prune before adding.</summary>
	public int MemoryCapBytes { get; init; } = 16 * 1024;
	/// <summary>Extra model ids per backend (anthropic, zai, deepseek), listed ahead of what the provider reports.</summary>
	public Dictionary<string, List<string>> Models { get; init; } = new();
	/// <summary>Daily dollar caps by backend (anthropic, zai, deepseek) or "total". A capped backend waits until local midnight.</summary>
	public Dictionary<string, decimal> Budgets { get; init; } = new();
	/// <summary>Dollars per million tokens by model id, overriding the built-in Claude prices. Unpriced models count tokens only.</summary>
	public Dictionary<string, Marten.Models.Price> Prices { get; init; } = new();
	/// <summary>MCP tools go straight into each agent's tool list while there are at most this many; past it they're found with tool_search.</summary>
	public int DirectToolLimit { get; init; } = 12;
	/// <summary>MCP servers by name (stdio only). Their tools appear to agents as name__tool.</summary>
	public Dictionary<string, Marten.Mcp.McpServerConfig> Mcp { get; init; } = new();
	/// <summary>Extra Origin allowed to call the API, e.g. the Vite dev server.</summary>
	public string? DevOrigin { get; init; }

	public static Config Load(Home home) {
		var config = JsonIO.Load(home.ConfigPath, () => new Config());
		if(!File.Exists(home.ConfigPath)) JsonIO.Save(home.ConfigPath, config);
		return config;
	}

	public void Save(Home home) => JsonIO.Save(home.ConfigPath, this);
}
