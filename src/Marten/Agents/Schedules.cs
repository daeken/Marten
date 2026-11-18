using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Agents;

/// <summary>A PowerShell command its owner runs at a time (once) or on an interval, without the model.</summary>
public sealed record Schedule {
	public required int Id { get; init; }
	public required string Command { get; init; }
	public string? Description { get; init; }
	public required DateTimeOffset Created { get; init; }
	/// <summary>Interval runs; null for a one-shot.</summary>
	public int? EverySeconds { get; init; }
	public required DateTimeOffset NextRun { get; init; }
	public int? TimeoutSeconds { get; init; }
	public DateTimeOffset? LastRun { get; init; }
	/// <summary>How the last run went: "output delivered", "no output", "skipped: ...".</summary>
	public string? LastResult { get; init; }
	/// <summary>A one-shot that has run.</summary>
	public bool Done { get; init; }
}

public static partial class Durations {
	[GeneratedRegex(@"^\s*(\d+(?:\.\d+)?)\s*(s|sec|secs|seconds?|m|min|mins|minutes?|h|hr|hrs|hours?|d|days?)\s*$", RegexOptions.IgnoreCase)]
	private static partial Regex Pattern();

	/// <summary>"30s", "15m", "2h", "1d", or a bare number of seconds.</summary>
	public static TimeSpan Parse(string text) {
		if(double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs)) return TimeSpan.FromSeconds(secs);
		var m = Pattern().Match(text);
		if(!m.Success) throw new ToolException($"Can't read \"{text}\" as a duration; use e.g. 30s, 15m, 2h or 1d.");
		var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
		return char.ToLowerInvariant(m.Groups[2].Value[0]) switch {
			's' => TimeSpan.FromSeconds(n), 'm' => TimeSpan.FromMinutes(n), 'h' => TimeSpan.FromHours(n), _ => TimeSpan.FromDays(n),
		};
	}

	public static string Format(TimeSpan t) =>
		t.TotalDays >= 1 && t.TotalDays % 1 == 0 ? $"{t.TotalDays:0}d"
		: t.TotalHours >= 1 && t.TotalHours % 1 == 0 ? $"{t.TotalHours:0}h"
		: t.TotalMinutes >= 1 && t.TotalMinutes % 1 == 0 ? $"{t.TotalMinutes:0}m"
		: $"{t.TotalSeconds:0}s";
}

public sealed partial class Agent {
	List<Schedule>? schedules;
	string SchedulesPath => Path.Combine(Directory, "schedules.json");

	List<Schedule> LoadedSchedules() => schedules ??= JsonIO.Load(SchedulesPath, () => new List<Schedule>());

	public IReadOnlyList<Schedule> Schedules { get { lock(gate) return LoadedSchedules().ToList(); } }

	public Schedule AddSchedule(string command, string? description, DateTimeOffset nextRun, int? everySeconds, int? timeoutSeconds) {
		Schedule s;
		lock(gate) {
			var list = LoadedSchedules();
			s = new Schedule {
				Id = list.Count == 0 ? 1 : list.Max(x => x.Id) + 1, Command = command, Description = description,
				Created = DateTimeOffset.UtcNow, EverySeconds = everySeconds, NextRun = nextRun, TimeoutSeconds = timeoutSeconds,
			};
			list.Add(s);
			JsonIO.Save(SchedulesPath, list);
		}
		manager.Publish(new { type = "schedules", agentId = Info.Id });
		return s;
	}

	public bool RemoveSchedule(int id) {
		bool removed;
		lock(gate) {
			removed = LoadedSchedules().RemoveAll(s => s.Id == id) > 0;
			if(removed) JsonIO.Save(SchedulesPath, schedules!);
		}
		if(removed) manager.Publish(new { type = "schedules", agentId = Info.Id });
		return removed;
	}

	internal void UpdateSchedule(int id, Func<Schedule, Schedule> change) {
		lock(gate) {
			var list = LoadedSchedules();
			var i = list.FindIndex(s => s.Id == id);
			if(i < 0) return;
			list[i] = change(list[i]);
			JsonIO.Save(SchedulesPath, list);
		}
		manager.Publish(new { type = "schedules", agentId = Info.Id });
	}

	/// <summary>Whether a message is still in the inbox, undelivered.</summary>
	internal bool Holds(Inbound message) { lock(gate) return inbox.Contains(message); }
}

/// <summary>
/// Runs schedules. A run is skipped while the previous run of the same schedule is still going or its
/// output still waits undelivered. Interval runs missed while Marten was down are skipped; a missed
/// one-shot runs at startup. A closed or paused owner's schedules are suspended: interval runs are
/// skipped and one-shots wait.
/// </summary>
public sealed class Scheduler(AgentManager manager) {
	readonly Dictionary<(string, int), Task> running = new();
	readonly Dictionary<(string, int), Inbound> waiting = new();
	public const int MinimumIntervalSeconds = 10;

	public void Start(CancellationToken ct) => _ = Task.Run(async () => {
		SkipMissedIntervals(DateTimeOffset.UtcNow);
		while(!ct.IsCancellationRequested) {
			try { Tick(DateTimeOffset.UtcNow); } catch { /* one bad schedule must not stop the rest */ }
			await Task.Delay(1000, ct).ContinueWith(_ => { });
		}
	});

	public void SkipMissedIntervals(DateTimeOffset now) {
		foreach(var a in manager.Agents)
			foreach(var s in a.Schedules.Where(s => s.EverySeconds != null && s.NextRun <= now))
				a.UpdateSchedule(s.Id, x => x with { NextRun = Next(x, now), LastResult = "skipped: missed while Marten was down" });
	}

	static DateTimeOffset Next(Schedule s, DateTimeOffset now) {
		var every = TimeSpan.FromSeconds(s.EverySeconds!.Value);
		var next = s.NextRun;
		if(next <= now) next += every * (Math.Floor((now - next) / every) + 1);
		return next;
	}

	bool Busy(Agent a, Schedule s) {
		var key = (a.Info.Id, s.Id);
		lock(running) {
			if(running.TryGetValue(key, out var t) && !t.IsCompleted) return true;
			if(waiting.TryGetValue(key, out var m) && a.Holds(m)) return true;
			waiting.Remove(key);
			return false;
		}
	}

	/// <summary>Start every due run. Returns the runs started, so tests can await them.</summary>
	public List<Task> Tick(DateTimeOffset now) {
		var started = new List<Task>();
		foreach(var a in manager.Agents) {
			foreach(var s in a.Schedules.Where(s => !s.Done && s.NextRun <= now)) {
				string? skip = a.Info.Closed ? "skipped: agent closed" : a.IsPaused ? "skipped: paused" : Busy(a, s) ? "skipped: previous run still going or undelivered" : null;
				if(skip != null) {
					// One-shots wait for the owner; interval runs are simply skipped.
					if(s.EverySeconds != null) a.UpdateSchedule(s.Id, x => x with { NextRun = Next(x, now), LastResult = skip });
					continue;
				}
				a.UpdateSchedule(s.Id, x => x with { LastRun = now, Done = x.EverySeconds == null, NextRun = x.EverySeconds == null ? x.NextRun : Next(x, now) });
				var run = Run(a, s);
				lock(running) running[(a.Info.Id, s.Id)] = run;
				started.Add(run);
			}
		}
		return started;
	}

	async Task Run(Agent a, Schedule s) {
		if(manager.Tools.Get("powershell") is not PowerShellTool ps) return;
		ToolOutput output;
		try {
			output = await ps.RunAsync(a, s.Command, TimeSpan.FromSeconds(s.TimeoutSeconds ?? manager.Config.CommandTimeoutSeconds), CancellationToken.None);
		} catch(Exception e) {
			output = ToolOutput.Error($"error: {e.Message}");
		}
		var text = output.Text.Trim();
		if(text.Length == 0 || (text == "[no output]" && !output.IsError)) {
			a.UpdateSchedule(s.Id, x => x with { LastResult = "no output" });
			return;
		}
		var (body, full) = a.Truncate($"schedule-{s.Id}-{DateTime.UtcNow:yyyyMMddHHmmss}", text);
		var label = $"Schedule #{s.Id}{(s.Description is { Length: > 0 } d ? $" ({d})" : "")}";
		var message = new Inbound(Agent.NudgeSender, $"{label}{(output.IsError ? " failed" : "")}:\n{body}", SendMode.Queued, "schedule");
		lock(running) waiting[(a.Info.Id, s.Id)] = message;
		a.Send(message);
		a.UpdateSchedule(s.Id, x => x with { LastResult = output.IsError ? "error delivered" : "output delivered" });
	}
}

public sealed class ScheduleAddTool : ITool {
	public string Name => "schedule_add";
	public string Description =>
		"Schedule a PowerShell command to run as you, without the model, once (at) or repeatedly (every). If it prints " +
		"anything, the output reaches you as a queued message and wakes you; no output means nothing happens. Use it to " +
		"poll CI, watch a log, or remind yourself. A run is skipped while the previous one's output is still undelivered.";
	public JsonObject Parameters => Schema.Object(
		("command", Schema.Str("PowerShell to run. Print only when there's something to act on.")),
		("at", Schema.Str("One-shot: an ISO 8601 time, or a delay from now like \"10m\". Null for an interval.", nullable: true)),
		("every", Schema.Str($"Interval like \"30s\", \"15m\", \"1h\" (at least {Scheduler.MinimumIntervalSeconds}s). Null for a one-shot.", nullable: true)),
		("description", Schema.Str("A few words on what it's for.", nullable: true)),
		("timeout_seconds", Schema.Int("Per-run timeout, or null for the default.", nullable: true)));

	public Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		var self = AgentTools.Self(ctx);
		var at = args.OptString("at");
		var every = args.OptString("every");
		if(string.IsNullOrWhiteSpace(at) == string.IsNullOrWhiteSpace(every)) throw new ToolException("Give exactly one of at or every.");
		var now = DateTimeOffset.UtcNow;
		DateTimeOffset next;
		int? everySeconds = null;
		if(!string.IsNullOrWhiteSpace(every)) {
			var span = Durations.Parse(every);
			if(span.TotalSeconds < Scheduler.MinimumIntervalSeconds) throw new ToolException($"The shortest interval is {Scheduler.MinimumIntervalSeconds}s.");
			everySeconds = (int) span.TotalSeconds;
			next = now + span;
		} else if(DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var when)) {
			next = when;
		} else {
			next = now + Durations.Parse(at!.TrimStart('+'));
		}
		var s = self.AddSchedule(args.ReqString("command"), args.OptString("description"), next, everySeconds, args.OptInt("timeout_seconds"));
		var when_ = everySeconds != null ? $"every {Durations.Format(TimeSpan.FromSeconds(everySeconds.Value))}, first at {next.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}" : $"once at {next.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}";
		return Task.FromResult(new ToolOutput($"Schedule #{s.Id} runs {when_}."));
	}
}

public sealed class ScheduleListTool : ITool {
	public string Name => "schedule_list";
	public string Description => "List your schedules with their next run and how the last run went.";
	public JsonObject Parameters => Schema.Object();

	public Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		var list = AgentTools.Self(ctx).Schedules.Where(s => !s.Done).ToList();
		if(list.Count == 0) return Task.FromResult(new ToolOutput("No schedules."));
		var lines = list.Select(s =>
			$"#{s.Id} {(s.EverySeconds is { } e ? $"every {Durations.Format(TimeSpan.FromSeconds(e))}" : "once")}, next {s.NextRun.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}" +
			$"{(s.Description != null ? $" — {s.Description}" : "")}{(s.LastResult != null ? $" (last: {s.LastResult})" : "")}\n    {s.Command.Split('\n')[0]}");
		return Task.FromResult(new ToolOutput(string.Join("\n", lines)));
	}
}

public sealed class ScheduleRemoveTool : ITool {
	public string Name => "schedule_remove";
	public string Description => "Remove one of your schedules.";
	public JsonObject Parameters => Schema.Object(("id", Schema.Int("Schedule number.")));

	public Task<ToolOutput> InvokeAsync(IToolContext ctx, JsonObject args, CancellationToken ct) {
		var id = args.OptInt("id") ?? throw new ToolException("Missing required argument: id");
		if(!AgentTools.Self(ctx).RemoveSchedule(id)) throw new ToolException($"You have no schedule {id}.");
		return Task.FromResult(new ToolOutput($"Removed schedule #{id}."));
	}
}
