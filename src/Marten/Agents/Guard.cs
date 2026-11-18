using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Agents;

public enum GuardAction { Allow, Deny, Ask }

/// <summary>
/// One line of Sera's filter. The first rule that matches a call decides it; with none matching, the call goes ahead.
/// </summary>
public sealed record GuardRule {
	/// <summary>Tool name, with * as a wildcard, e.g. "powershell" or "playwright__*".</summary>
	public string Tool { get; init; } = "*";
	/// <summary>Regex tried against the call's string arguments (for powershell, the command), case-insensitive. Null matches every call.</summary>
	public string? Match { get; init; }
	/// <summary>Only these kinds of agent (chat, orchestrator, task); null for all.</summary>
	public List<AgentKind>? Agents { get; init; }
	/// <summary>Only agents in these projects; null for all.</summary>
	public List<string>? Projects { get; init; }
	public GuardAction Action { get; init; } = GuardAction.Ask;
	/// <summary>Shown to the agent and to Sera.</summary>
	public string? Reason { get; init; }
}

/// <summary>A call Sera was asked about, by fingerprint of agent, tool and arguments.</summary>
public sealed record Approval(string Fingerprint, string AgentId, string Tool, string QuestionId, DateTimeOffset At) {
	/// <summary>Null while waiting; true once allowed (used up by the next identical call); false when denied.</summary>
	public bool? Allowed { get; init; }
	public string? Note { get; init; }
}

/// <summary>
/// Sera's filter on what agents do, and her review of agent-written tools. A guardrail against mistakes, not a sandbox:
/// it sees tool calls, not what a PowerShell script does once it runs.
/// </summary>
public sealed class Guard(AgentManager manager) {
	readonly object gate = new();
	List<Approval>? approvals;
	Dictionary<string, string>? approvedTools;
	string ApprovalsPath => Path.Combine(manager.Home.Root, "approvals.json");
	string ApprovedToolsPath => Path.Combine(manager.Home.Root, "tools-approved.json");
	const int SubjectPreview = 3000;

	// ---- The filter ----

	/// <summary>Null to let the call through; otherwise the error the agent sees instead.</summary>
	public ToolOutput? Check(IToolContext ctx, ITool tool, JsonObject args) {
		var rules = manager.Config.Rules;
		// call_tool is checked as the call it makes, which comes back through here.
		if(rules.Count == 0 || ctx is not Agent agent || tool.Name == "call_tool") return null;
		var subject = Subject(args);
		var rule = rules.FirstOrDefault(r => Matches(r, agent, tool.Name, subject));
		if(rule == null || rule.Action == GuardAction.Allow) return null;
		var why = rule.Reason is { Length: > 0 } r ? r : $"a rule on {rule.Tool}" + (rule.Match != null ? $" matching /{rule.Match}/" : "");
		if(rule.Action == GuardAction.Deny) return ToolOutput.Error($"Not allowed: {why}. Sera's filter blocks this call; find another way, or ask whoever you report to.");

		var fingerprint = Fingerprint(agent.Info.Id, tool.Name, args);
		lock(gate) {
			var list = Approvals();
			var prior = list.LastOrDefault(a => a.Fingerprint == fingerprint);
			if(prior is { Allowed: true }) {
				list.Remove(prior);
				SaveApprovals();
				return null;
			}
			if(prior is { Allowed: null }) return ToolOutput.Error($"Still waiting for Sera to approve this {tool.Name} call (card {prior.QuestionId}). Carry on with other work; her answer arrives as a message.");
			if(prior is { Allowed: false }) return ToolOutput.Error($"Sera denied this {tool.Name} call{(prior.Note != null ? $": {prior.Note}" : "")}. Don't retry it as is.");
		}
		var preview = subject.Length > SubjectPreview ? subject[..SubjectPreview] + "\n…" : subject;
		var q = manager.AskSera(agent, $"**{agent.Info.Name}** wants to call `{tool.Name}`", ["Allow once", "Deny"],
			$"Rule: {why}\n\n```\n{(preview.Length > 0 ? preview : args.ToJsonString())}\n```", kind: "approval", reference: fingerprint);
		lock(gate) {
			Approvals().Add(new Approval(fingerprint, agent.Info.Id, tool.Name, q.Id, DateTimeOffset.UtcNow));
			SaveApprovals();
		}
		return ToolOutput.Error($"This call needs Sera's approval ({why}). I've asked her (card {q.Id}); her answer arrives as a message. " +
			"If she allows it, make exactly the same call again. Meanwhile carry on with other work, or mark your task waiting.");
	}

	static bool Matches(GuardRule r, Agent agent, string tool, string subject) {
		if(!Wildcard(r.Tool).IsMatch(tool)) return false;
		if(r.Agents is { Count: > 0 } kinds && !kinds.Contains(agent.Info.Kind)) return false;
		if(r.Projects is { Count: > 0 } projects && (agent.Info.ProjectId == null || !projects.Contains(agent.Info.ProjectId))) return false;
		if(r.Match is { Length: > 0 } m) {
			try { return Regex.IsMatch(subject, m, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
			catch(RegexMatchTimeoutException) { return true; }
		}
		return true;
	}

	static Regex Wildcard(string pattern) =>
		new("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	/// <summary>Every string in the arguments, one per line: what the agent wrote, unescaped.</summary>
	public static string Subject(JsonNode? node) {
		var sb = new StringBuilder();
		void Walk(JsonNode? n) {
			switch(n) {
				case JsonObject o: foreach(var (_, v) in o) Walk(v); break;
				case JsonArray a: foreach(var v in a) Walk(v); break;
				case JsonValue v when v.TryGetValue<string>(out var s): if(sb.Length > 0) sb.Append('\n'); sb.Append(s); break;
				case JsonValue v: if(sb.Length > 0) sb.Append('\n'); sb.Append(v.ToJsonString()); break;
			}
		}
		Walk(node);
		return sb.ToString();
	}

	static string Fingerprint(string agentId, string tool, JsonObject args) =>
		Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{agentId}\n{tool}\n{args.ToJsonString()}")))[..24];

	List<Approval> Approvals() => approvals ??= JsonIO.Load(ApprovalsPath, () => new List<Approval>());
	void SaveApprovals() {
		// Answered ones older than a week are history.
		approvals!.RemoveAll(a => a.Allowed != null && DateTimeOffset.UtcNow - a.At > TimeSpan.FromDays(7));
		JsonIO.Save(ApprovalsPath, approvals);
	}

	// ---- Review of agent-written tools ----

	public static string Hash(string script) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script)));

	Dictionary<string, string> ApprovedTools() {
		if(approvedTools != null) return approvedTools;
		if(File.Exists(ApprovedToolsPath)) return approvedTools = JsonIO.Load(ApprovedToolsPath, () => new Dictionary<string, string>());
		// Review was just turned on: tools that already exist are grandfathered in.
		approvedTools = new();
		foreach(var dir in ToolDirs().Where(Directory.Exists))
			foreach(var f in Directory.EnumerateFiles(dir, "*.ps1")) approvedTools[f] = Hash(File.ReadAllText(f));
		JsonIO.Save(ApprovedToolsPath, approvedTools);
		return approvedTools;
	}

	IEnumerable<string> ToolDirs() => [Path.Combine(manager.Home.Root, "tools"), .. manager.Projects.Select(ScriptToolSource.ProjectDir)];

	/// <summary>Whether this script may load: always, unless reviewTools is on and Sera hasn't approved this exact text.</summary>
	public bool ToolApproved(string path, string script) {
		if(!manager.Config.ReviewTools) return true;
		lock(gate) return ApprovedTools().TryGetValue(path, out var h) && h == Hash(script);
	}

	public void ApproveTool(string path, string script) {
		lock(gate) {
			ApprovedTools()[path] = Hash(script);
			JsonIO.Save(ApprovedToolsPath, approvedTools!);
		}
		manager.Publish(new { type = "tools" });
	}

	public SeraMessage RequestToolReview(Agent from, string name, string path, string script, string scope) =>
		manager.AskSera(from, $"**{from.Info.Name}** wrote a {(scope == "global" ? "global" : "project")} tool, `{name}`. Approve it?", ["Approve", "Reject"],
			$"`{path}`\n\n```powershell\n{script}\n```", kind: "tool_review", reference: $"{path}\n{Hash(script)}");

	// ---- Sera's answers ----

	/// <summary>Handle Sera's answer to a card this class put up, and tell the agent. False for other cards.</summary>
	public bool Answered(SeraMessage q, bool firstOption, string? note) {
		var asker = manager.Get(q.From);
		var noteText = note != null ? $" Her note: {note}" : "";
		switch(q.Kind) {
			case "approval": {
				Approval? a;
				lock(gate) {
					var list = Approvals();
					var i = list.FindIndex(x => x.QuestionId == q.Id);
					a = i < 0 ? null : list[i] = list[i] with { Allowed = firstOption, Note = note, At = DateTimeOffset.UtcNow };
					SaveApprovals();
				}
				asker?.Send(new Inbound("Sera", firstOption
					? $"Sera allowed your {a?.Tool ?? "pending"} call (card {q.Id}). Make exactly the same call again to run it.{noteText}"
					: $"Sera denied your {a?.Tool ?? "pending"} call (card {q.Id}).{noteText}", SendMode.Queued, "answer"));
				return true;
			}
			case "tool_review": {
				var parts = (q.Reference ?? "").Split('\n');
				if(parts.Length != 2) return false;
				var (path, hash) = (parts[0], parts[1]);
				var name = Path.GetFileNameWithoutExtension(path);
				var current = File.Exists(path) ? File.ReadAllText(path) : null;
				if(firstOption) {
					if(current != null && Hash(current) == hash) ApproveTool(path, current);
					asker?.Send(new Inbound("Sera", current != null && Hash(current) == hash
						? $"Sera approved your tool {name}; it's callable now (call_tool, or from PowerShell).{noteText}"
						: $"Sera approved the version of {name} she reviewed, but the file has changed since, so it still needs review.{noteText}", SendMode.Queued, "answer"));
				} else {
					if(current != null && Hash(current) == hash) File.Delete(path);
					manager.Publish(new { type = "tools" });
					asker?.Send(new Inbound("Sera", $"Sera rejected your tool {name}, so it's been removed.{noteText}", SendMode.Queued, "answer"));
				}
				return true;
			}
			default: return false;
		}
	}
}
