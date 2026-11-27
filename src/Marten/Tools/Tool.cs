using System.Text.Json.Nodes;
using Marten.Models;
using Marten.Storage;

namespace Marten.Tools;

public sealed record ToolOutput(string Text, bool IsError = false) {
	/// <summary>Images to show the model alongside the text, already saved to disk.</summary>
	public List<ImageRef>? Images { get; init; }
	public IReadOnlyList<ArtifactRef>? Artifacts { get; init; }
	public static ToolOutput Error(string text) => new(text, true);
}

/// <summary>What a tool sees of the agent calling it.</summary>
public interface IToolContext {
	string AgentId { get; }
	string WorkingDirectory { get; }
	FileTracker Files { get; }
	ToolRegistry Tools { get; }
}

/// <summary>A tool written in C# and built into the runtime. Every tool is callable directly and from PowerShell.</summary>
public interface ITool {
	string Name { get; }
	string Description { get; }
	/// <summary>Strict JSON schema: every property listed in required, optional ones nullable.</summary>
	JsonObject Parameters { get; }
	/// <summary>False for tools that can't be reached from inside PowerShell (powershell itself would deadlock).</summary>
	bool CallableFromPowerShell => true;
	/// <summary>Whether this agent gets the tool at all, e.g. spawning is for orchestrators only.</summary>
	bool AvailableTo(IToolContext ctx) => true;
	/// <summary>Script to define the tool's PowerShell function inline, instead of a call back through $Marten.</summary>
	string? PowerShellBody => null;
	/// <summary>False for schemas the backend can't enforce strictly, like MCP servers' own.</summary>
	bool Strict => true;
	Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct);
}

/// <summary>Tools that come and go at runtime: tools agents write, MCP servers' tools.</summary>
public interface IToolSource {
	IEnumerable<ITool> ToolsFor(IToolContext ctx);
}

public sealed class ToolRegistry {
	readonly Dictionary<string, ITool> tools = new();
	readonly List<IToolSource> sources = new();
	// Delegation (call_tool) must not bypass the originating turn's admission check.
	readonly AsyncLocal<Func<Task>?> currentAdmission = new();

	public ToolRegistry Add(ITool tool) {
		tools[tool.Name] = tool;
		return this;
	}

	public ToolRegistry AddSource(IToolSource source) {
		sources.Add(source);
		return this;
	}

	public IEnumerable<ITool> All => tools.Values;
	/// <summary>Sera's filter: returns the result to give instead of running the call, or null to let it run.</summary>
	public Func<IToolContext, ITool, JsonObject, ToolOutput?>? Gate { get; set; }
	/// <summary>A built-in tool by name.</summary>
	public ITool? Get(string name) => tools.GetValueOrDefault(name);
	public bool IsBuiltIn(string name) => tools.ContainsKey(name);
	public IReadOnlyList<ToolSpec> Specs => tools.Values.Select(Spec).ToList();
	public static ToolSpec Spec(ITool t) => new(t.Name, t.Description, t.Parameters, t.Strict);

	/// <summary>Built-in tools first; a dynamic tool never shadows one.</summary>
	public IEnumerable<ITool> For(IToolContext ctx) =>
		tools.Values.Where(t => t.AvailableTo(ctx))
			.Concat(sources.SelectMany(s => s.ToolsFor(ctx)).Where(t => !tools.ContainsKey(t.Name)).DistinctBy(t => t.Name));
	public IEnumerable<ITool> DynamicFor(IToolContext ctx) => For(ctx).Where(t => !tools.ContainsKey(t.Name));
	public ITool? Find(IToolContext ctx, string name) =>
		tools.TryGetValue(name, out var t) ? (t.AvailableTo(ctx) ? t : null) : DynamicFor(ctx).FirstOrDefault(d => d.Name == name);
	public IReadOnlyList<ToolSpec> SpecsFor(IToolContext ctx) => For(ctx).Select(Spec).ToList();
	public IReadOnlyList<ToolSpec> BuiltInSpecsFor(IToolContext ctx) => tools.Values.Where(t => t.AvailableTo(ctx)).Select(Spec).ToList();

	public async Task<ToolOutput> InvokeAsync(IToolContext ctx, string name, string? arguments, CancellationToken ct, Func<Task>? admit = null) {
		if(Find(ctx, name) is not { } tool) return ToolOutput.Error($"Unknown tool: {name}");
		JsonObject args;
		try {
			args = JsonNode.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments) as JsonObject
				?? throw new ArgumentException("Arguments must be a JSON object.");
		} catch(Exception e) when(e is System.Text.Json.JsonException or ArgumentException) {
			return ToolOutput.Error($"Invalid arguments for {name}: {e.Message}");
		}
		var previous = currentAdmission.Value;
		admit ??= previous;
		try {
			if(admit != null) await admit();
			if(Gate is { } guard) {
				if(guard(ctx, tool, args) is { } blocked) return blocked;
				if(admit != null) await admit();
			}
			ct.ThrowIfCancellationRequested();
			currentAdmission.Value = admit;
			return await tool.InvokeAsync(ctx, args, ct);
		} catch(ToolException e) {
			return ToolOutput.Error(e.Message);
		} finally {
			currentAdmission.Value = previous;
		}
	}
}

/// <summary>An expected tool failure, reported to the model as an error result.</summary>
public sealed class ToolException(string message) : Exception(message);

public static class Schema {
	public static JsonObject Object(params (string Name, JsonObject Schema)[] props) => new() {
		["type"] = "object",
		["properties"] = new JsonObject(props.Select(p => KeyValuePair.Create(p.Name, (JsonNode?) p.Schema))),
		["required"] = new JsonArray(props.Select(p => (JsonNode) p.Name).ToArray()),
		["additionalProperties"] = false,
	};
	public static JsonObject Str(string description, bool nullable = false) => Typed("string", description, nullable);
	public static JsonObject Int(string description, bool nullable = false) => Typed("integer", description, nullable);
	public static JsonObject Enum(string description, params string[] values) => new() {
		["type"] = "string", ["description"] = description, ["enum"] = new JsonArray(values.Select(v => (JsonNode) v).ToArray()),
	};
	public static JsonObject Array(string description, JsonObject items) => new() {
		["type"] = "array", ["description"] = description, ["items"] = items,
	};
	static JsonObject Typed(string type, string description, bool nullable) => new() {
		["type"] = nullable ? new JsonArray(type, "null") : type, ["description"] = description,
	};

	public static string? OptString(this JsonObject args, string name) =>
		args[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
	public static string ReqString(this JsonObject args, string name) =>
		args.OptString(name) ?? throw new ToolException($"Missing required argument: {name}");
	public static int? OptInt(this JsonObject args, string name) =>
		args[name] is JsonValue v ? v.TryGetValue<int>(out var i) ? i : v.TryGetValue<long>(out var l) ? (int) l
			: v.TryGetValue<double>(out var d) ? (int) d : v.TryGetValue<string>(out var s) && int.TryParse(s, out var p) ? p : null : null;
}
