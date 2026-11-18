using System.Collections.Concurrent;
using System.Management.Automation.Language;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Marten.Models;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Agents;

/// <summary>
/// A tool an agent wrote: a PowerShell script whose param block is the schema and whose comment-based help is
/// the description. Called directly it runs in the caller's PowerShell; from PowerShell it is an inline function.
/// </summary>
public sealed class ScriptTool : ITool {
	public required string Name { get; init; }
	public required string Description { get; init; }
	public required JsonObject Parameters { get; init; }
	public required string Path { get; init; }
	public required string Script { get; init; }
	/// <summary>"global" or a project id.</summary>
	public required string Scope { get; init; }
	public string PowerShellBody => Script;

	public async Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		if(ctx is not IPowerShellOwner owner || ctx.Tools.Get("powershell") is not PowerShellTool ps)
			throw new ToolException("This tool needs PowerShell.");
		var bound = new JsonObject();
		foreach(var (k, v) in args) if(v != null) bound[k] = v.DeepClone();
		var json = bound.ToJsonString().Replace("'", "''");
		var path = Path.Replace("'", "''");
		var command = $"$__args = ConvertFrom-Json -AsHashtable '{json}'\n& '{path}' @__args";
		var timeout = ctx is Agent a ? a.CommandTimeout : TimeSpan.FromMinutes(10);
		return await ps.RunAsync(owner, command, timeout, ct);
	}

	static readonly Regex ValidName = new("^[a-z][a-z0-9_]{1,63}$");

	/// <summary>Parse a tool script, or explain what's wrong with it.</summary>
	public static ScriptTool Parse(string name, string script, string path, string scope) {
		if(!ValidName.IsMatch(name)) throw new ToolException($"Tool names are lower_snake_case: {name}");
		var ast = Parser.ParseInput(script, out _, out var errors);
		if(errors.Length > 0)
			throw new ToolException("The script doesn't parse:\n" + string.Join("\n", errors.Select(e => $"line {e.Extent.StartLineNumber}: {e.Message}")));
		var help = ast.GetHelpContent();
		var description = string.Join("\n\n", new[] { help?.Synopsis, help?.Description }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
		if(description.Length == 0)
			throw new ToolException("Give the tool a description: start the script with comment-based help (<# .SYNOPSIS ... .PARAMETER name ... #>).");
		var props = new JsonObject();
		var required = new JsonArray();
		foreach(var p in ast.ParamBlock?.Parameters ?? []) {
			var pname = p.Name.VariablePath.UserPath;
			var mandatory = p.Attributes.OfType<AttributeAst>().Any(a =>
				a.TypeName.Name.Equals("Parameter", StringComparison.OrdinalIgnoreCase) &&
				a.NamedArguments.Any(n => n.ArgumentName.Equals("Mandatory", StringComparison.OrdinalIgnoreCase) &&
					(n.ExpressionOmitted || n.Argument is VariableExpressionAst { VariablePath.UserPath: var v } && v.Equals("true", StringComparison.OrdinalIgnoreCase))));
			var schema = SchemaFor(p.StaticType, mandatory);
			var pdesc = help?.Parameters.TryGetValue(pname.ToUpperInvariant(), out var d) == true ? d.Trim() : "";
			schema["description"] = (pdesc.Length > 0 ? pdesc : pname) + (mandatory ? "" : " (optional; null to omit)");
			props[pname] = schema;
			required.Add(pname);
		}
		return new ScriptTool {
			Name = name, Description = description, Path = path, Script = script, Scope = scope,
			Parameters = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = required, ["additionalProperties"] = false },
		};
	}

	static JsonObject SchemaFor(Type type, bool mandatory) {
		JsonNode TypeName(string t) => mandatory ? t : new JsonArray(t, "null");
		if(type.IsArray && type != typeof(object)) {
			var item = SchemaFor(type.GetElementType()!, true);
			return new JsonObject { ["type"] = TypeName("array"), ["items"] = item };
		}
		var t = type == typeof(string) ? "string"
			: type == typeof(int) || type == typeof(long) || type == typeof(short) ? "integer"
			: type == typeof(double) || type == typeof(float) || type == typeof(decimal) ? "number"
			: type == typeof(bool) || type == typeof(System.Management.Automation.SwitchParameter) ? "boolean"
			: "string";
		return new JsonObject { ["type"] = TypeName(t) };
	}

	public string Hash() =>
		Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Name + "\n" + Description + "\n" + Parameters.ToJsonString())))[..16];
}

/// <summary>Global tools in ~/.marten/tools; project tools in the project's tools/ directory, which win on a name clash.</summary>
public sealed class ScriptToolSource(Func<AgentManager> manager) : IToolSource {
	readonly ConcurrentDictionary<string, (DateTime Stamp, ScriptTool? Tool)> cache = new();

	public string GlobalDir => System.IO.Path.Combine(manager().Home.Root, "tools");
	public static string ProjectDir(Projects.Project p) => System.IO.Path.Combine(p.Directory, "tools");

	public string DirFor(string scope) =>
		scope == "global" ? GlobalDir : manager().GetProject(scope) is { } p ? ProjectDir(p) : throw new ToolException($"No project {scope}.");

	public IEnumerable<ITool> ToolsFor(IToolContext ctx) {
		var found = new Dictionary<string, ScriptTool>();
		foreach(var t in Load(GlobalDir, "global")) found[t.Name] = t;
		if(ctx is Agent { Project: { } project })
			foreach(var t in Load(ProjectDir(project), project.Info.Id)) found[t.Name] = t;
		return found.Values.OrderBy(t => t.Name);
	}

	IEnumerable<ScriptTool> Load(string dir, string scope) {
		if(!System.IO.Directory.Exists(dir)) yield break;
		foreach(var file in System.IO.Directory.EnumerateFiles(dir, "*.ps1")) {
			var stamp = File.GetLastWriteTimeUtc(file);
			if(!cache.TryGetValue(file, out var hit) || hit.Stamp != stamp) {
				ScriptTool? tool = null;
				try { tool = ScriptTool.Parse(System.IO.Path.GetFileNameWithoutExtension(file), File.ReadAllText(file), file, scope); } catch { }
				cache[file] = hit = (stamp, tool);
			}
			if(hit.Tool != null && manager().Guard.ToolApproved(file, hit.Tool.Script)) yield return hit.Tool;
		}
	}
}

public sealed class DefineToolTool(Func<AgentManager> manager, ScriptToolSource source) : ITool {
	public string Name => "define_tool";
	public string Description =>
		"Write a new tool, or replace one, as a PowerShell script. Its param block is the schema ([Parameter(Mandatory)] for " +
		"required ones; [string], [int], [double], [bool], [switch] and arrays of those), and its comment-based help is the " +
		"description (.SYNOPSIS, .DESCRIPTION, .PARAMETER). scope \"project\" shares it with your project's agents; " +
		"\"global\" with every agent. It runs as whoever calls it. It's a PowerShell function right away; other agents " +
		"get it as a direct tool after their next compaction.";
	public JsonObject Parameters => Schema.Object(
		("name", Schema.Str("lower_snake_case name.")),
		("script", Schema.Str("The whole script: comment-based help, a param block, then the body.")),
		("scope", Schema.Enum("Who gets it.", "project", "global")));
	public bool CallableFromPowerShell => true;

	public Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		var self = AgentTools.Self(ctx);
		var name = args.ReqString("name");
		var scope = args.ReqString("scope") == "global" ? "global" : self.Info.ProjectId ?? throw new ToolException("You're not in a project; use scope \"global\".");
		if(manager().Tools.IsBuiltIn(name)) throw new ToolException($"{name} is a built-in tool; pick another name.");
		var dir = source.DirFor(scope);
		var path = Path.Combine(dir, name + ".ps1");
		var tool = ScriptTool.Parse(name, args.ReqString("script"), path, scope);
		var existed = File.Exists(path);
		// Ask before writing, so turning review on can't grandfather in the tool being defined.
		var approved = manager().Guard.ToolApproved(path, tool.Script);
		Directory.CreateDirectory(dir);
		AtomicFile.Write(path, tool.Script);
		manager().Publish(new { type = "tools" });
		var paramList = string.Join(", ", (tool.Parameters["properties"] as JsonObject)!.Select(p => p.Key));
		var saved = $"{(existed ? "Replaced" : "Defined")} {name} ({(scope == "global" ? "global" : "project")}) at {path}; parameters: {(paramList.Length > 0 ? paramList : "none")}.";
		if(!approved) {
			var card = manager().Guard.RequestToolReview(self, name, path, tool.Script, scope);
			saved += $" Sera reviews new and changed tools: it can't be called until she approves it (card {card.Id}). Her answer arrives as a message.";
		}
		return Task.FromResult(new ToolOutput(saved));
	}
}

public sealed class RemoveToolTool(Func<AgentManager> manager, ScriptToolSource source) : ITool {
	public string Name => "remove_tool";
	public string Description => "Remove a tool written with define_tool.";
	public JsonObject Parameters => Schema.Object(
		("name", Schema.Str("The tool's name.")),
		("scope", Schema.Enum("Where it lives.", "project", "global")));

	public Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		var self = AgentTools.Self(ctx);
		var name = args.ReqString("name");
		var scope = args.ReqString("scope") == "global" ? "global" : self.Info.ProjectId ?? throw new ToolException("You're not in a project.");
		var path = Path.Combine(source.DirFor(scope), name + ".ps1");
		if(!File.Exists(path)) throw new ToolException($"No {(scope == "global" ? "global" : "project")} tool called {name}.");
		File.Delete(path);
		manager().Publish(new { type = "tools" });
		return Task.FromResult(new ToolOutput($"Removed {name}."));
	}
}

public sealed partial class Agent {
	public TimeSpan CommandTimeout => TimeSpan.FromSeconds(manager.Config.CommandTimeoutSeconds);

	/// <summary>
	/// Tools sent with each request: built-ins plus dynamic tools as they stood at spawn or the last compaction,
	/// so the cached prefix holds. Changes since then reach the agent as a note at the start of a turn.
	/// </summary>
	public IReadOnlyList<ToolSpec> RequestTools() {
		List<ToolSpec>? snapshot;
		lock(gate) snapshot = state.DynamicTools;
		if(snapshot == null) {
			snapshot = SnapshotDynamicTools();
		}
		return [.. manager.Tools.BuiltInSpecsFor(this), .. snapshot];
	}

	/// <summary>
	/// Agent-written tools are always direct. MCP tools are direct only while there are no more than
	/// DirectToolLimit of them; past that they stay behind tool_search and call_tool.
	/// </summary>
	List<ToolSpec> SnapshotDynamicTools() {
		var tools = manager.Tools.DynamicFor(this).ToList();
		var mcp = tools.OfType<Marten.Mcp.McpTool>().ToList();
		var direct = mcp.Count > manager.Config.DirectToolLimit ? tools.Where(t => t is not Marten.Mcp.McpTool) : tools;
		var specs = direct.Select(ToolRegistry.Spec).ToList();
		lock(gate) state = state with { DynamicTools = specs, ToolsNoted = tools.ToDictionary(t => t.Name, HashOf) };
		SaveState();
		return specs;
	}

	static string HashOf(ITool t) =>
		t is ScriptTool s ? s.Hash() : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(t.Description + t.Parameters.ToJsonString())))[..16];

	void NoteToolChanges() {
		Dictionary<string, string>? noted;
		lock(gate) noted = state.ToolsNoted;
		if(noted == null) {
			SnapshotDynamicTools();
			return;
		}
		var current = manager.Tools.DynamicFor(this).ToList();
		var now = current.ToDictionary(t => t.Name, HashOf);
		var added = current.Where(t => !noted.ContainsKey(t.Name)).ToList();
		var changed = current.Where(t => noted.TryGetValue(t.Name, out var h) && h != now[t.Name]).ToList();
		var removed = noted.Keys.Where(n => !now.ContainsKey(n)).ToList();
		if(added.Count + changed.Count + removed.Count == 0) return;
		lock(gate) state = state with { ToolsNoted = now };
		SaveState();
		string Line(ITool t) {
			var ps = string.Join(" ", (t.Parameters["properties"] as JsonObject)?.Select(p => $"-{p.Key} <{p.Key}>") ?? []);
			return $"{t.Name}: {t.Description.Split('\n')[0]}\n  From PowerShell: {t.Name} {ps}";
		}
		var parts = new List<string>();
		if(added.Count > 0) parts.Add("New:\n" + string.Join("\n", added.Select(Line)));
		if(changed.Count > 0) parts.Add("Changed:\n" + string.Join("\n", changed.Select(Line)));
		if(removed.Count > 0) parts.Add("Removed: " + string.Join(", ", removed));
		Append(new Entry {
			Kind = EntryKind.Message, From = NudgeSender,
			Text = $"Tools changed since your last turn. Call new or changed ones with call_tool (tool_search shows their parameters) or from PowerShell; your next compaction makes them direct tools, except MCP tools past the direct limit.\n\n{string.Join("\n\n", parts)}",
		});
	}
}
