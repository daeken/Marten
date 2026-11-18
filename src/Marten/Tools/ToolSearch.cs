using System.Text;
using System.Text.Json.Nodes;
using Marten.Mcp;

namespace Marten.Tools;

/// <summary>
/// Finds tools that aren't in the model's tool list: MCP tools once there are more than the
/// direct limit, and anything added since the last snapshot. Returns full schemas for call_tool.
/// </summary>
public sealed class ToolSearchTool : ITool {
	public string Name => "tool_search";
	public string Description =>
		"Search every tool you can use, including MCP tools that aren't in your tool list. Returns each match's name, " +
		"description and parameter schema; call it with call_tool. An empty query lists the MCP servers and your " +
		"agent-written tools; \"server:<name>\" lists every tool of one server.";
	public JsonObject Parameters => Schema.Object(
		("query", Schema.Str("Words to match against tool names and descriptions, \"server:<name>\", or empty for an overview.")),
		("limit", Schema.Int("Most matches to return, up to 25. Null for 8.", nullable: true)));

	public Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		var query = (args.OptString("query") ?? "").Trim();
		var limit = Math.Clamp(args.OptInt("limit") ?? 8, 1, 25);
		var tools = ctx.Tools.For(ctx).ToList();
		if(query.Length == 0) return Task.FromResult(new ToolOutput(Overview(ctx, tools)));

		List<ITool> matches;
		if(query.StartsWith("server:", StringComparison.OrdinalIgnoreCase)) {
			var server = query["server:".Length..].Trim();
			matches = tools.OfType<McpTool>().Where(t => t.Server.Equals(server, StringComparison.OrdinalIgnoreCase)).Cast<ITool>().ToList();
			limit = Math.Max(limit, matches.Count);
		} else {
			var words = query.ToLowerInvariant().Split([' ', ',', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
			matches = tools
				.Select(t => (Tool: t, Score: Score(t, words)))
				.Where(x => x.Score > 0)
				.OrderByDescending(x => x.Score).ThenBy(x => x.Tool.Name)
				.Select(x => x.Tool).ToList();
		}
		if(matches.Count == 0) return Task.FromResult(new ToolOutput($"No tools match \"{query}\". Try fewer or broader words, or an empty query for an overview."));
		var sb = new StringBuilder();
		foreach(var t in matches.Take(limit)) {
			sb.Append("## ").AppendLine(t.Name).AppendLine(t.Description);
			sb.Append("Parameters: ").AppendLine(t.Parameters.ToJsonString()).AppendLine();
		}
		if(matches.Count > limit) sb.AppendLine($"{matches.Count - limit} more match; raise limit or narrow the query.");
		return Task.FromResult(new ToolOutput(sb.ToString().TrimEnd()));
	}

	/// <summary>Any word can match: a name hit counts most, a description hit less, so tools matching more words rank first.</summary>
	static int Score(ITool t, string[] words) {
		var name = t.Name.ToLowerInvariant();
		var description = t.Description.ToLowerInvariant();
		var score = 0;
		foreach(var w in words) {
			if(name.Contains(w)) score += 3;
			else if(description.Contains(w)) score += 1;
		}
		return score;
	}

	static string Overview(IToolContext ctx, List<ITool> tools) {
		var sb = new StringBuilder();
		var servers = tools.OfType<McpTool>().GroupBy(t => t.Server).OrderBy(g => g.Key).ToList();
		sb.AppendLine(servers.Count == 0 ? "No MCP servers." : "MCP servers (search \"server:<name>\" for their tools):");
		foreach(var g in servers) sb.AppendLine($"- {g.Key}: {g.Count()} tools, e.g. {string.Join(", ", g.Take(4).Select(t => t.Name))}");
		var written = tools.Where(t => !ctx.Tools.IsBuiltIn(t.Name) && t is not McpTool).ToList();
		sb.AppendLine(written.Count == 0 ? "No agent-written tools." : "Agent-written tools: " + string.Join(", ", written.Select(t => t.Name)));
		return sb.ToString().TrimEnd();
	}
}

/// <summary>Calls any tool by name, for tools found with tool_search that aren't in the model's tool list.</summary>
public sealed class CallToolTool : ITool {
	public string Name => "call_tool";
	public string Description =>
		"Call a tool by name with its arguments, for tools found with tool_search that aren't in your tool list. " +
		"Results, including images, come back as if you had called the tool directly.";
	public JsonObject Parameters => new() {
		["type"] = "object",
		["properties"] = new JsonObject {
			["name"] = new JsonObject { ["type"] = "string", ["description"] = "The tool's exact name." },
			["arguments"] = new JsonObject { ["type"] = "object", ["description"] = "Arguments matching the tool's parameter schema." },
		},
		["required"] = new JsonArray("name", "arguments"),
	};
	public bool Strict => false;
	public bool CallableFromPowerShell => false;

	public Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		var name = args.OptString("name") ?? throw new ToolException("Missing required argument: name");
		if(name == Name) throw new ToolException("call_tool can't call itself.");
		var arguments = args["arguments"] switch {
			JsonObject o => o.ToJsonString(),
			JsonValue v when v.TryGetValue<string>(out var s) => s,
			null => "{}",
			_ => throw new ToolException("arguments must be an object."),
		};
		return ctx.Tools.InvokeAsync(ctx, name, arguments, ct);
	}
}
