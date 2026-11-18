using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Marten.Agents;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Mcp;

/// <summary>A server's tool list as last read, and the config it was read with.</summary>
public sealed record McpSchema(string ConfigHash, DateTimeOffset ReadAt, List<JsonObject> Tools, string? Error);

/// <summary>
/// MCP is lazy and per agent. Each server is started once to read its tool list, which is cached on disk;
/// it is launched for real, one instance per agent, only when that agent first calls one of its tools.
/// </summary>
public sealed partial class McpHub(Func<AgentManager> manager) : IToolSource {
	readonly ConcurrentDictionary<string, McpSchema> schemas = new();
	readonly ConcurrentDictionary<(string Agent, string Server), Task<McpClient>> clients = new();
	bool loaded;

	AgentManager M => manager();
	string CacheDir => Path.Combine(M.Home.Root, "mcp");
	Dictionary<string, McpServerConfig> Servers => M.Config.Mcp.Where(kv => !kv.Value.Disabled && ValidServer().IsMatch(kv.Key)).ToDictionary();
	TimeSpan Timeout => TimeSpan.FromSeconds(M.Config.CommandTimeoutSeconds);

	[GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
	private static partial Regex ValidServer();

	static string Hash(McpServerConfig c) =>
		Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonIO.Serialize(c))))[..16];

	void EnsureLoaded() {
		if(loaded) return;
		loaded = true;
		if(!Directory.Exists(CacheDir)) return;
		foreach(var file in Directory.EnumerateFiles(CacheDir, "*.json"))
			if(JsonIO.Load<McpSchema?>(file, () => null) is { } s) schemas[Path.GetFileNameWithoutExtension(file)] = s;
	}

	public IReadOnlyDictionary<string, McpSchema> Schemas { get { EnsureLoaded(); return schemas; } }

	/// <summary>Read the tool list of every server whose config changed since its schema was cached.</summary>
	public async Task RefreshAsync(bool force, CancellationToken ct) {
		EnsureLoaded();
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		await Task.WhenAll(Servers.Select(async kv => {
			var (name, config) = kv;
			var hash = Hash(config);
			if(!force && schemas.TryGetValue(name, out var have) && have.ConfigHash == hash && have.Error == null) return;
			McpSchema schema;
			try {
				await using var client = await McpClient.StartAsync(name, config, home, Path.Combine(CacheDir, $"{name}.schema.log"), Timeout, ct);
				schema = new McpSchema(hash, DateTimeOffset.UtcNow, await client.ListToolsAsync(Timeout, ct), null);
			} catch(Exception e) when(e is McpException or IOException or TimeoutException) {
				schema = new McpSchema(hash, DateTimeOffset.UtcNow, schemas.TryGetValue(name, out var old) ? old.Tools : [], e.Message);
			}
			schemas[name] = schema;
			JsonIO.Save(Path.Combine(CacheDir, $"{name}.json"), schema);
		}));
		M.Publish(new { type = "tools" });
	}

	public IEnumerable<ITool> ToolsFor(IToolContext ctx) {
		EnsureLoaded();
		foreach(var (name, config) in Servers)
			if(schemas.TryGetValue(name, out var s))
				foreach(var t in s.Tools)
					if(t["name"]?.GetValue<string>() is { Length: > 0 } toolName)
						yield return new McpTool(this, name, toolName, t);
	}

	/// <summary>This agent's instance of a server, started on first use and restarted if it died.</summary>
	public async Task<McpClient> ClientFor(Agent agent, string server, CancellationToken ct) {
		if(!Servers.TryGetValue(server, out var config)) throw new ToolException($"MCP server {server} isn't configured.");
		var key = (agent.Info.Id, server);
		while(true) {
			var task = clients.GetOrAdd(key, _ => McpClient.StartAsync(server, config, agent.WorkingDirectory,
				Path.Combine(agent.Directory, "mcp", $"{server}.log"), Timeout, CancellationToken.None));
			McpClient client;
			try {
				client = await task.WaitAsync(ct);
			} catch(McpException e) {
				clients.TryRemove(KeyValuePair.Create(key, task));
				throw new ToolException($"MCP server {server} failed to start: {e.Message}");
			}
			if(!client.Exited) return client;
			clients.TryRemove(KeyValuePair.Create(key, task));
			await client.DisposeAsync();
		}
	}

	/// <summary>Running instances, for the UI.</summary>
	public IEnumerable<(string AgentId, string Server, int Pid)> Running =>
		clients.Where(kv => kv.Value.IsCompletedSuccessfully && !kv.Value.Result.Exited).Select(kv => (kv.Key.Agent, kv.Key.Server, kv.Value.Result.Pid));

	public async Task StopFor(string agentId) {
		foreach(var key in clients.Keys.Where(k => k.Agent == agentId).ToList())
			if(clients.TryRemove(key, out var task) && task.IsCompletedSuccessfully) await task.Result.DisposeAsync();
	}

	public async Task StopAll() {
		foreach(var key in clients.Keys.ToList())
			if(clients.TryRemove(key, out var task) && task.IsCompletedSuccessfully) await task.Result.DisposeAsync();
	}
}

/// <summary>One tool of one MCP server, as an agent sees it. Named server__tool.</summary>
public sealed partial class McpTool(McpHub hub, string server, string tool, JsonObject definition) : ITool {
	public string Server => server;
	public string Name { get; } = Sanitize($"{server}__{tool}");
	public string Description { get; } = $"[{server} MCP] {definition["description"]?.GetValue<string>() ?? definition["title"]?.GetValue<string>() ?? tool}";
	public JsonObject Parameters { get; } = SchemaOf(definition);
	public bool Strict => false;

	[GeneratedRegex("[^A-Za-z0-9_-]")]
	private static partial Regex Invalid();
	static string Sanitize(string name) { var s = Invalid().Replace(name, "_"); return s.Length > 64 ? s[..64] : s; }

	static JsonObject SchemaOf(JsonObject definition) {
		var schema = definition["inputSchema"] is JsonObject o ? (JsonObject) o.DeepClone() : new JsonObject();
		schema["type"] = "object";
		schema["properties"] ??= new JsonObject();
		return schema;
	}

	public async Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		var agent = AgentTools.Self(ctx);
		var client = await hub.ClientFor(agent, server, ct);
		JsonObject result;
		try {
			result = await client.CallToolAsync(tool, args, TimeSpan.FromSeconds(agent.CommandTimeout.TotalSeconds), ct);
		} catch(McpException e) {
			throw new ToolException(e.Message);
		}
		var text = new StringBuilder();
		var images = new List<ImageRef>();
		foreach(var item in result["content"] as JsonArray ?? []) {
			switch(item?["type"]?.GetValue<string>()) {
				case "text":
					text.Append(item["text"]?.GetValue<string>()).Append('\n');
					break;
				case "image" when item["data"]?.GetValue<string>() is { } data:
					var mime = item["mimeType"]?.GetValue<string>() ?? "image/png";
					try {
						var img = agent.SaveImage(Convert.FromBase64String(data), mime);
						images.Add(img);
						text.Append($"[image: {img.Path}]\n");
					} catch(FormatException) {
						text.Append("[image with invalid data]\n");
					}
					break;
				case "resource" when item["resource"] is JsonObject r:
					text.Append(r["text"]?.GetValue<string>() is { } rt ? rt : $"[resource {r["uri"]} ({r["mimeType"]}), binary]").Append('\n');
					break;
				case "resource_link":
					text.Append($"[resource {item["name"]}: {item["uri"]}]\n");
					break;
				case { } other:
					text.Append($"[{other} content omitted]\n");
					break;
			}
		}
		if(text.Length == 0 && result["structuredContent"] is { } structured) text.Append(structured.ToJsonString());
		var output = text.Length == 0 ? "[no output]" : text.ToString();
		return new ToolOutput(output, result["isError"]?.GetValue<bool>() == true) { Images = images.Count > 0 ? images : null };
	}
}
