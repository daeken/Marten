using System.Text.Json.Nodes;
using Marten.Projects;
using Marten.Tools;

namespace Marten.Agents;

/// <summary>Who may message whom, and how. Task agents never message across projects.</summary>
public static class MessagingRules {
	/// <summary>Null when allowed; otherwise why not.</summary>
	public static string? Check(Agent from, Agent to, SendMode mode) {
		if(from == to) return "You can't message yourself.";
		var f = from.Info;
		var t = to.Info;
		var immediateOk = false;
		bool allowed;
		switch(f.Kind) {
			case AgentKind.Chat:
				allowed = t.Kind == AgentKind.Orchestrator;
				immediateOk = allowed;
				break;
			case AgentKind.Orchestrator:
				var ownTask = t.Kind == AgentKind.Task && t.ParentId == f.Id;
				allowed = ownTask || t.Kind is AgentKind.Orchestrator or AgentKind.Chat;
				immediateOk = ownTask;
				break;
			default:
				allowed = t.ProjectId != null && t.ProjectId == f.ProjectId;
				break;
		}
		if(!allowed)
			return f.Kind switch {
				AgentKind.Chat => "Chat agents can only message orchestrators.",
				AgentKind.Orchestrator => "Orchestrators can message their own task agents, other orchestrators and chat agents.",
				_ => "Task agents can only message agents in their own project.",
			};
		if(mode == SendMode.Immediate && !immediateOk) return "You can only queue messages to that agent.";
		if(t.Closed && t.ParentId != f.Id) return $"{to.Info.Name} is closed; only its orchestrator can wake it.";
		return null;
	}
}

static class AgentTools {
	public static Agent Self(IToolContext ctx) => ctx as Agent ?? throw new ToolException("This tool needs an agent.");

	/// <summary>Find an agent by id, or by name among those the caller can see (its project first).</summary>
	public static Agent Resolve(AgentManager m, Agent from, string who) {
		if(m.Get(who) is { } byId) return byId;
		var named = m.Agents.Where(a => string.Equals(a.Info.Name, who, StringComparison.OrdinalIgnoreCase)).ToList();
		var local = named.Where(a => a.Info.ProjectId == from.Info.ProjectId).ToList();
		var pick = local.Count > 0 ? local : named;
		return pick.Count switch {
			1 => pick[0],
			0 => throw new ToolException($"No agent called {who}. Use an agent id from $Marten.Agents()."),
			_ => throw new ToolException($"Several agents are called {who}: {string.Join(", ", pick.Select(a => a.Info.Id))}. Use an id."),
		};
	}
}

public sealed class SendMessageTool(Func<AgentManager> manager) : ITool {
	public string Name => "send_message";
	public string Description =>
		"Send a message to another agent, or to Sera with to \"sera\" (orchestrators only). Immediate lands at the recipient's " +
		"next tool boundary, as steering; queued waits until it is idle, and wakes it if it already is. " +
		"Chat agents may message orchestrators; orchestrators their own task agents (either mode), other orchestrators and " +
		"chat agents (queued); task agents anyone in their own project (queued).";
	public JsonObject Parameters => Schema.Object(
		("to", Schema.Str("Agent id or name, or \"sera\".")),
		("text", Schema.Str("The message.")),
		("mode", Schema.Enum("immediate or queued.", "immediate", "queued")));

	public Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		var m = manager();
		var from = AgentTools.Self(ctx);
		var to = args.ReqString("to");
		var text = args.ReqString("text");
		var mode = args.OptString("mode") == "immediate" ? SendMode.Immediate : SendMode.Queued;
		if(string.Equals(to, "sera", StringComparison.OrdinalIgnoreCase)) {
			if(from.Info.Kind != AgentKind.Orchestrator)
				throw new ToolException(from.Info.Kind == AgentKind.Chat
					? "Sera reads your replies directly; just answer her."
					: "Task agents can't message Sera; pass questions to your orchestrator.");
			m.MessageSera(from, text);
			return Task.FromResult(new ToolOutput("Sent to Sera's inbox."));
		}
		var target = AgentTools.Resolve(m, from, to);
		if(MessagingRules.Check(from, target, mode) is { } why) throw new ToolException(why);
		var wasClosed = target.Info.Closed;
		target.Send(Inbound.FromAgent(from, text, mode));
		return Task.FromResult(new ToolOutput($"{(mode == SendMode.Immediate ? "Delivered" : "Queued")} to {AgentManager.Label(target)}{(wasClosed ? ", which was closed and is now awake" : "")}."));
	}
}

public sealed class SpawnAgentTool(Func<AgentManager> manager) : ITool {
	public string Name => "spawn_agent";
	public string Description =>
		"Spawn a task agent in your project. It gets the brief as its first message (queued from you). " +
		"worktree: true gives it its own git worktree and branch (use it for concurrent writers); null uses the project default. " +
		"task, if given, goes on its task list as assigned by you, and you're woken when it turns done, waiting or blocked.";
	public JsonObject Parameters => Schema.Object(
		("name", Schema.Str("Short name, e.g. \"parser\" or \"ci-fixer\".")),
		("brief", Schema.Str("Self-contained instructions: goal, constraints, where to look, how to report back.")),
		("task", Schema.Str("Task title for its list, or null.", nullable: true)),
		("worktree", new JsonObject { ["type"] = new JsonArray("boolean", "null"), ["description"] = "Own git worktree, or null for the project default." }),
		("model", Schema.Str("Model override, or null for the default.", nullable: true)),
		("effort", Schema.Str("Effort override, or null for the default.", nullable: true)));
	public bool AvailableTo(IToolContext ctx) => ctx is Agent { Info.Kind: AgentKind.Orchestrator };

	public Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		var m = manager();
		var self = AgentTools.Self(ctx);
		bool? worktree = args["worktree"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
		Agent agent;
		try {
			agent = m.SpawnTask(self, args.ReqString("name"), worktree, args.OptString("model"), args.OptString("effort"));
		} catch(InvalidOperationException e) {
			throw new ToolException(e.Message);
		}
		if(args.OptString("task") is { Length: > 0 } task) agent.AddTask(task, self.Info.Id);
		agent.Send(Inbound.FromAgent(self, args.ReqString("brief"), SendMode.Queued));
		return Task.FromResult(new ToolOutput($"Spawned {AgentManager.Label(agent)} in {agent.Info.WorkingDirectory}."));
	}
}

public sealed class CloseAgentTool(Func<AgentManager> manager) : ITool {
	public string Name => "close_agent";
	public string Description =>
		"Close one of your finished task agents. It keeps its history; it is never nudged and its schedules are suspended. " +
		"Messaging it wakes it again.";
	public JsonObject Parameters => Schema.Object(("agent", Schema.Str("Agent id or name.")));
	public bool AvailableTo(IToolContext ctx) => ctx is Agent { Info.Kind: AgentKind.Orchestrator };

	public Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		var self = AgentTools.Self(ctx);
		var target = AgentTools.Resolve(manager(), self, args.ReqString("agent"));
		if(target.Info.ParentId != self.Info.Id) throw new ToolException($"{target.Info.Name} isn't one of your task agents.");
		target.SetClosed(true);
		return Task.FromResult(new ToolOutput($"Closed {AgentManager.Label(target)}."));
	}
}

public sealed class CreateProjectTool(Func<AgentManager> manager) : ITool {
	public string Name => "create_project";
	public string Description =>
		"Create a project bound to existing directories or repos, and spawn its orchestrator. The brief, if given, is queued " +
		"to the orchestrator as its first message from you.";
	public JsonObject Parameters => Schema.Object(
		("name", Schema.Str("Project name.")),
		("bindings", Schema.Array("Directories or repos the project works on; the first is the orchestrator's working directory.", Schema.Str("A path."))),
		("prompt", Schema.Str("Project prompt: goals, rules, context every agent in it should know. Null for none.", nullable: true)),
		("worktrees_by_default", new JsonObject { ["type"] = new JsonArray("boolean", "null"), ["description"] = "Spawn task agents into worktrees by default. Null means no." }),
		("brief", Schema.Str("First message to the orchestrator, or null.", nullable: true)));
	public bool AvailableTo(IToolContext ctx) => ctx is Agent { Info.Kind: AgentKind.Chat };

	public Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		var self = AgentTools.Self(ctx);
		var bindings = (args["bindings"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").ToList() ?? [];
		var worktrees = args["worktrees_by_default"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
		Project project;
		Agent orchestrator;
		try {
			(project, orchestrator) = manager().CreateProject(args.ReqString("name"), bindings, args.OptString("prompt"), worktrees);
		} catch(ArgumentException e) {
			throw new ToolException(e.Message);
		}
		if(args.OptString("brief") is { Length: > 0 } brief)
			orchestrator.Send(Inbound.FromAgent(self, brief, SendMode.Queued));
		return Task.FromResult(new ToolOutput($"Created project {project.Info.Name} ({project.Info.Id}) with orchestrator {orchestrator.Info.Id}."));
	}
}

public sealed class TaskAddTool(Func<AgentManager> manager) : ITool {
	public string Name => "task_add";
	public string Description =>
		"Add a task to your own list, or (orchestrators) to one of your task agents' lists. Tasks live outside your context. " +
		"Going idle with an open task gets you nudged.";
	public JsonObject Parameters => Schema.Object(
		("title", Schema.Str("What needs doing.")),
		("agent", Schema.Str("Whose list: null for yours; orchestrators may name one of their task agents.", nullable: true)));

	public Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		var self = AgentTools.Self(ctx);
		var target = args.OptString("agent") is { Length: > 0 } who ? AgentTools.Resolve(manager(), self, who) : self;
		if(target != self && target.Info.ParentId != self.Info.Id)
			throw new ToolException("You can only add tasks to your own list or your task agents' lists.");
		var task = target.AddTask(args.ReqString("title"), self.Info.Id);
		if(target != self) target.Send(Inbound.FromAgent(self, $"New task #{task.Id} on your list: {task.Title}", SendMode.Queued));
		return Task.FromResult(new ToolOutput($"Added task #{task.Id} to {(target == self ? "your list" : target.Info.Name + "'s list")}."));
	}
}

public sealed class TaskUpdateTool(Func<AgentManager> manager) : ITool {
	public string Name => "task_update";
	public string Description =>
		"Change a task's state on your list. waiting = on another agent or a schedule (no nudges, Sera isn't told); " +
		"blocked = on Sera; done = finished. Waiting and blocked need a reason. Whoever assigned the task is woken.";
	public JsonObject Parameters => Schema.Object(
		("id", Schema.Int("Task number.")),
		("state", Schema.Enum("New state.", "open", "waiting", "blocked", "done")),
		("reason", Schema.Str("Why it's waiting or blocked, or a short result when done. Null when reopening.", nullable: true)));

	public Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		var m = manager();
		var self = AgentTools.Self(ctx);
		var id = args.OptInt("id") ?? throw new ToolException("Missing required argument: id");
		var newState = Enum.Parse<TaskState>(args.ReqString("state"), ignoreCase: true);
		var reason = args.OptString("reason");
		if(newState is TaskState.Waiting or TaskState.Blocked && string.IsNullOrWhiteSpace(reason))
			throw new ToolException($"Say why the task is {newState.ToString().ToLowerInvariant()}.");
		var (before, after) = self.UpdateTask(id, newState, reason);
		var stateWord = newState.ToString().ToLowerInvariant();
		if(before.State != newState && newState != TaskState.Open && after.AssignedBy != self.Info.Id && m.Get(after.AssignedBy) is { } assigner)
			assigner.Send(Inbound.FromAgent(self, $"Task #{after.Id} \"{after.Title}\" is now {stateWord}{(reason != null ? $": {reason}" : "")}", SendMode.Queued));
		if(newState == TaskState.Blocked && before.State != TaskState.Blocked && self.Info.Kind == AgentKind.Orchestrator)
			m.MessageSera(self, $"Blocked on you: {after.Title}\n\n{reason}");
		return Task.FromResult(new ToolOutput($"Task #{id} is {stateWord}."));
	}
}

/// <summary>Rules for MEMORY.md: a size cap, cited entries removable only by orchestrators, and an audit log.</summary>
public sealed class MemoryGuard(Func<AgentManager> manager) : IEditGuard {
	public bool Applies(string path) => manager().ProjectByMemoryPath(path) != null;

	static bool IsCited(string line) => System.Text.RegularExpressions.Regex.IsMatch(line, @"^\s*-\s*[^()]*\(Sera,");

	public void Validate(IToolContext ctx, string path, string before, string after) {
		var m = manager();
		var cap = m.Config.MemoryCapBytes;
		var size = System.Text.Encoding.UTF8.GetByteCount(after);
		if(size > cap && size > System.Text.Encoding.UTF8.GetByteCount(before))
			throw new ToolException($"Project memory would be {size:N0} bytes, over its {cap:N0}-byte cap. Prune stale entries first.");
		var agent = ctx as Agent;
		var project = m.ProjectByMemoryPath(path)!;
		if(agent?.Info.ProjectId != project.Info.Id)
			throw new ToolException("Only agents in this project may edit its memory.");
		var (_, removed) = LineDiff.Of(before, after);
		var cited = removed.Where(IsCited).ToList();
		if(cited.Count > 0 && agent.Info.Kind != AgentKind.Orchestrator)
			throw new ToolException($"Only the orchestrator or Sera may remove cited entries:\n{string.Join("\n", cited)}");
	}

	public void Committed(IToolContext ctx, string path, string before, string after, string? reason) {
		var m = manager();
		var project = m.ProjectByMemoryPath(path)!;
		var (added, removed) = LineDiff.Of(before, after);
		project.LogMemoryEdit(new MemoryEdit(DateTimeOffset.UtcNow, ctx.AgentId, reason, added, removed));
		(ctx as Agent)?.SawMemory(after);
		m.Publish(new { type = "memory", projectId = project.Info.Id });
		var cited = removed.Where(IsCited).ToList();
		if(cited.Count > 0 && ctx is Agent agent)
			m.MessageSera(agent, $"Removed cited memory entr{(cited.Count == 1 ? "y" : "ies")}{(reason != null ? $" ({reason})" : "")}:\n{string.Join("\n", cited)}");
	}
}

public sealed class ElideResultsTool : ITool {
	public string Name => "elide_results";
	public string Description =>
		"Drop your last N tool results from your context at once, e.g. when one flooded it. Each is replaced by a note " +
		"with the path of its full text, which read_file can still open.";
	public JsonObject Parameters => Schema.Object(("count", Schema.Int("How many of your most recent results to elide.")));

	public Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		var count = args.OptInt("count") ?? throw new ToolException("Missing required argument: count");
		if(count < 1) throw new ToolException("count must be at least 1.");
		var n = AgentTools.Self(ctx).ElideLast(count);
		return Task.FromResult(new ToolOutput(n == 0 ? "Nothing to elide." : $"Elided {n} result{(n == 1 ? "" : "s")}."));
	}
}

public sealed class SetModelTool(Func<AgentManager> manager) : ITool {
	public string Name => "set_model";
	public string Description =>
		"Change the model or reasoning effort for yourself (only when Sera asks) or, as an orchestrator, for one of your task " +
		"agents. Null resets to the configured default. Takes effect at the next model call.";
	public JsonObject Parameters => Schema.Object(
		("agent", Schema.Str("Whose: null for yourself; orchestrators may name one of their task agents.", nullable: true)),
		("model", Schema.Str("Model id, or null for the default.", nullable: true)),
		("effort", Schema.Str("Effort (e.g. low, medium, high), or null for the default.", nullable: true)));

	public Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		var self = AgentTools.Self(ctx);
		var target = args.OptString("agent") is { Length: > 0 } who ? AgentTools.Resolve(manager(), self, who) : self;
		if(target != self && target.Info.ParentId != self.Info.Id)
			throw new ToolException("You can only change your own model or your task agents'.");
		var model = args.OptString("model") is { Length: > 0 } m ? m : null;
		var effort = args.OptString("effort") is { Length: > 0 } e ? e : null;
		target.UpdateSettings(model, effort);
		var cfg = manager().Config;
		return Task.FromResult(new ToolOutput($"{(target == self ? "You now use" : target.Info.Name + " now uses")} {model ?? cfg.Model + " (default)"} at {effort ?? cfg.Effort + " (default)"} effort."));
	}
}

public sealed class AskSeraTool(Func<AgentManager> manager) : ITool {
	public string Name => "ask_sera";
	public string Description =>
		"Put a question to Sera as a card she answers in the UI, with options to pick from and room for her own words. " +
		"Her answer reaches you later as a queued message; carry on with other work meanwhile, or mark the task that " +
		"needs it blocked. Use it for decisions only she can make, not for status.";
	public JsonObject Parameters => Schema.Object(
		("question", Schema.Str("The question, short and specific.")),
		("options", new JsonObject {
			["type"] = new JsonArray("array", "null"), ["items"] = Schema.Str("A choice, a few words."),
			["description"] = "2 to 6 choices, recommended first; null for a free-form answer.",
		}),
		("context", Schema.Str("What she needs to decide: background, tradeoffs, your recommendation. Null for none.", nullable: true)));
	public bool AvailableTo(IToolContext ctx) => ctx is Agent { Info.Kind: AgentKind.Chat or AgentKind.Orchestrator };

	public Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		var self = AgentTools.Self(ctx);
		var options = (args["options"] as JsonArray)?.Select(n => n?.GetValue<string>()?.Trim() ?? "").Where(s => s.Length > 0).ToList();
		if(options is { Count: 1 or > 6 }) throw new ToolException("Give 2 to 6 options, or null.");
		var q = manager().AskSera(self, args.ReqString("question"), options, args.OptString("context"));
		return Task.FromResult(new ToolOutput($"Asked (card {q.Id}). Sera's answer will arrive as a message."));
	}
}
