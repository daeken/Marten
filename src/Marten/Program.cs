using Marten.Agents;
using Marten.Models;
using Marten.Models.Anthropic;
using Marten.Models.OpenAI;
using Marten.Projects;
using Marten.Storage;
using Marten.Tools;
using Marten.Web;
using Microsoft.Extensions.FileProviders;

var home = new Home();
var config = Config.Load(home);
AgentManager? managerRef = null;
var backups = new Backups(home, () => (managerRef?.Config ?? config).Backup);
if(args is ["backup", ..]) {
	var taken = await backups.TakeAsync(force: true);
	Console.WriteLine($"Snapshot: {taken!.Path} ({taken.Bytes / 1024} KB{(taken.Encrypted ? ", encrypted" : "")})");
	return;
}
if(args is ["client", "add" or "rotate", var clientName, ..]) {
	var m = new AgentManager(home, config, new BackendRouter(new ResponsesBackend(new ChatGptAuth(home, new HttpClient()), new HttpClient()), [], null), new ToolRegistry());
	var c = m.Outside.Register(clientName, rotate: args[1] == "rotate");
	Console.WriteLine($"Client {c.Name}: token in {m.Outside.TokenPath(c.Name)}");
	return;
}
if(args is ["restore", var archive, ..]) {
	var target = args.Length > 2 ? args[2] : home.Root.TrimEnd('/') + "-restored-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
	var passphrase = Environment.GetEnvironmentVariable("MARTEN_BACKUP_PASSPHRASE")
		?? (File.Exists(backups.PassphrasePath) ? File.ReadAllText(backups.PassphrasePath) : null);
	await Backups.RestoreAsync(Path.GetFullPath(archive), target, passphrase);
	Console.WriteLine($"Restored into {Path.GetFullPath(target)}. Run Marten on it with MARTEN_HOME={Path.GetFullPath(target)}, or move it to {home.Root} while Marten is stopped.");
	return;
}
var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Marten/0.1");
var auth = new ChatGptAuth(home, http);
var messagesBackends = MessagesProvider.All.Select(p => new MessagesBackend(p, home,
	name => managerRef?.Config.Models.GetValueOrDefault(name) ?? [])).ToList();
var ledger = new UsageLedger(home, () => managerRef?.Config ?? config);
var backend = new BackendRouter(new ResponsesBackend(auth, http), messagesBackends.Select(b => ((IModelBackend) b, b.Settings.Prefixes)).ToList(), ledger);
Func<AgentManager> getManager = () => managerRef!;
var childWatcher = new ChildProcessWatcher();
var scriptTools = new ScriptToolSource(getManager);
var mcp = new Marten.Mcp.McpHub(getManager);
var tools = new ToolRegistry()
	.Add(new ReadFileTool())
	.Add(new EditFileTool(new MemoryGuard(getManager)))
	.Add(new PowerShellTool(() => managerRef!.Config.CommandTimeoutSeconds, () => managerRef!.Config.StopGraceSeconds, childWatcher))
	.Add(new SendMessageTool(getManager))
	.Add(new TaskAddTool(getManager))
	.Add(new TaskUpdateTool(getManager))
	.Add(new SpawnAgentTool(getManager))
	.Add(new CloseAgentTool(getManager))
	.Add(new CreateProjectTool(getManager))
	.Add(new AskSeraTool(getManager))
	.Add(new ElideResultsTool())
	.Add(new ToolSearchTool())
	.Add(new CallToolTool())
	.Add(new SetModelTool(getManager))
	.Add(new ScheduleAddTool())
	.Add(new ScheduleListTool())
	.Add(new ScheduleRemoveTool())
	.Add(new ProcessStartTool())
	.Add(new ProcessListTool())
	.Add(new ProcessStopTool())
	.Add(new DefineToolTool(getManager, scriptTools))
	.Add(new RemoveToolTool(getManager, scriptTools))
	.AddSource(scriptTools)
	.AddSource(mcp);
var manager = managerRef = new AgentManager(home, config, backend, tools) { Launcher = new ProcessLauncher(), Mcp = mcp };
var hub = new EventHub();
manager.Events += hub.Broadcast;
var security = new Security(home, config) { ClientAuth = manager.Outside.Authenticate };

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{config.Port}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.ConfigureHttpJsonOptions(o => {
	o.SerializerOptions.PropertyNamingPolicy = JsonIO.Options.PropertyNamingPolicy;
	o.SerializerOptions.DefaultIgnoreCondition = JsonIO.Options.DefaultIgnoreCondition;
	foreach(var c in JsonIO.Options.Converters) o.SerializerOptions.Converters.Add(c);
});
var app = builder.Build();
app.Use(security.Middleware);
app.UseWebSockets();

var webRoot = FindWebRoot();
if(webRoot != null) {
	var files = new PhysicalFileProvider(webRoot);
	app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
	var types = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
	types.Mappings[".webmanifest"] = "application/manifest+json";
	app.UseStaticFiles(new StaticFileOptions {
		FileProvider = files, ContentTypeProvider = types,
		// The service worker and the page must revalidate, so a rebuilt UI reaches the installed app.
		OnPrepareResponse = r => {
			if(r.File.Name is "sw.js" or "index.html") r.Context.Response.Headers.CacheControl = "no-cache";
		},
	});
}

object AgentSummary(Agent a) => new {
	a.Info.Id, a.Info.Kind, a.Info.Name, a.Info.Created, a.Info.WorkingDirectory, a.Info.Model, a.Info.Effort,
	a.Status, a.StatusDetail, Pending = a.PendingCount, a.AbandonedRunspaces,
	a.Info.ProjectId, a.Info.ParentId, a.Info.Closed, a.Info.Worktree, a.NudgesStopped, a.Info.Paused, PausedEffective = a.IsPaused,
	OpenTasks = a.Tasks.Count(t => t.State != TaskState.Done),
};

object BackendSummary(MessagesBackend b) => new {
	b.Provider, b.Settings.Label, b.Connected, b.KeySource, b.Settings.EnvVar,
	SpentToday = ledger.SpentToday(b.Provider), Budget = manager.Config.Budgets.TryGetValue(b.Provider, out var cap) ? cap : (decimal?) null,
};

object ProjectSummary(Project p) => new { p.Info.Id, p.Info.Name, p.Info.Created, p.Info.Bindings, p.Info.WorktreesByDefault, p.Info.OrchestratorId, p.Info.Paused };

var api = app.MapGroup("/api");
api.MapGet("/status", () => new {
	auth = new { connected = auth.Connected, email = auth.Email, signingIn = auth.SigningIn, lastError = auth.LastError },
	backends = messagesBackends.Select(BackendSummary),
	config = new { manager.Config.Model, manager.Config.Effort, DefaultModel = manager.KnownDefaultModel },
	agents = manager.Agents.Select(AgentSummary),
	projects = manager.Projects.Select(ProjectSummary),
	inbox = manager.SeraInbox().Count,
	openQuestions = manager.SeraInbox().Count(m => m.Question && m.Answer == null),
	paused = manager.PausedAll,
	spentToday = ledger.SpentToday(),
	totalBudget = manager.Config.Budgets.TryGetValue("total", out var totalCap) ? totalCap : (decimal?) null,
});
api.MapPost("/pause", (PauseRequest req) => {
	manager.SetPausedAll(req.Paused);
	return Results.Ok();
});
api.MapPost("/agents/{id}/close", (string id, CloseRequest req) => {
	if(manager.Get(id) is not { } a) return Results.NotFound();
	a.SetClosed(req.Closed);
	return Results.Ok(AgentSummary(a));
});
api.MapPost("/agents/{id}/pause", (string id, PauseRequest req) => {
	if(manager.Get(id) is not { } a) return Results.NotFound();
	a.SetPaused(req.Paused);
	return Results.Ok(AgentSummary(a));
});
api.MapPost("/projects/{id}/pause", (string id, PauseRequest req) => {
	if(manager.GetProject(id) is not { } p) return Results.NotFound();
	manager.SetProjectPaused(p, req.Paused);
	return Results.Ok(ProjectSummary(p));
});
api.MapPost("/auth/login", () => new { url = auth.BeginLogin() });
api.MapPost("/auth/logout", () => { auth.Logout(); return Results.Ok(); });
api.MapPut("/backends/{name}/key", (string name, BackendKey req) => {
	if(messagesBackends.FirstOrDefault(b => b.Provider == name) is not { } b) return Results.NotFound();
	b.SetKey(req.Key);
	manager.Publish(new { type = "config" });
	return Results.Ok(BackendSummary(b));
});
object BackupSummary() => new {
	backups.Directory, backups.Encrypting, manager.Config.Backup.IntervalMinutes,
	Snapshots = backups.List().Take(20).Select(b => new { Name = Path.GetFileName(b.Path), b.Taken, b.Bytes, b.Encrypted }),
};
api.MapGet("/backups", BackupSummary);
api.MapPost("/backups", async (CancellationToken ct) => {
	await backups.TakeAsync(force: true, ct);
	return Results.Ok(BackupSummary());
});
api.MapPut("/backups/passphrase", (BackendKey req) => {
	backups.SetPassphrase(req.Key);
	return Results.Ok(BackupSummary());
});
// ---- Outside agents ----
object ClientSummary(OutsideClient c) => new {
	c.Name, c.Created, tokenPath = manager.Outside.TokenPath(c.Name),
	unread = manager.Outside.Inbox(c.Name, false).Count,
};
api.MapGet("/clients", () => manager.Outside.Clients.Select(ClientSummary));
api.MapPost("/clients", (ClientRequest req) => {
	try { return Results.Ok(ClientSummary(manager.Outside.Register(req.Name ?? "", req.Rotate ?? false))); }
	catch(ArgumentException e) { return Results.Problem(e.Message, statusCode: 400); }
});
api.MapDelete("/clients/{name}", (string name) => manager.Outside.Remove(name) ? Results.Ok() : Results.NotFound());

var ext = api.MapGroup("/ext");
string? ClientOf(HttpContext ctx) => ctx.Items["client"] as string;
IResult NeedsClient() => Results.Problem("Use an outside client's token (secrets/clients/NAME.token) for /api/ext.", statusCode: 403);
object ExtAgent(Agent a) => new {
	a.Info.Id, a.Info.Name, a.Info.Kind, a.Info.ProjectId, a.Status, a.StatusDetail, Pending = a.PendingCount,
	a.Info.Closed, Paused = a.IsPaused, OpenTasks = a.Tasks.Count(t => t.State != TaskState.Done),
};
ext.MapGet("/whoami", (HttpContext ctx) => ClientOf(ctx) is { } c ? Results.Ok(new { client = c, replyAs = Outside.Prefix + c }) : NeedsClient());
ext.MapGet("/status", (HttpContext ctx) => ClientOf(ctx) is null ? NeedsClient() : Results.Ok(new {
	projects = manager.Projects.Select(p => new { p.Info.Id, p.Info.Name, p.Info.Bindings, p.Info.OrchestratorId, p.Info.Paused }),
	agents = manager.Agents.Where(a => !a.Info.Closed).Select(ExtAgent),
	paused = manager.PausedAll,
}));
ext.MapPost("/projects", (HttpContext ctx, CreateProject req) => {
	if(ClientOf(ctx) is not { } c) return NeedsClient();
	try {
		var (project, orchestrator) = manager.CreateProject(req.Name ?? "", req.Bindings ?? [], req.Prompt, req.WorktreesByDefault ?? false);
		if(Blank(req.Brief) is { } brief) manager.Outside.Deliver(c, orchestrator.Info.Id, brief, SendMode.Queued);
		return Results.Ok(new { project.Info.Id, project.Info.Name, project.Info.Bindings, orchestratorId = orchestrator.Info.Id });
	} catch(ArgumentException e) {
		return Results.Problem(e.Message, statusCode: 400);
	}
});
ext.MapPost("/messages", (HttpContext ctx, ExtMessage req) => {
	if(ClientOf(ctx) is not { } c) return NeedsClient();
	try {
		var a = manager.Outside.Deliver(c, req.To ?? "", req.Text ?? "", req.Mode ?? SendMode.Queued);
		return Results.Ok(new { deliveredTo = a.Info.Id, name = a.Info.Name, status = a.Status });
	} catch(ToolException e) {
		return Results.Problem(e.Message, statusCode: 400);
	}
});
ext.MapGet("/inbox", async (HttpContext ctx, bool? ack, int? wait) => {
	if(ClientOf(ctx) is not { } c) return NeedsClient();
	var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Clamp(wait ?? 0, 0, 120));
	var list = manager.Outside.Inbox(c, false);
	while(list.Count == 0 && DateTime.UtcNow < deadline && !ctx.RequestAborted.IsCancellationRequested) {
		try { await Task.Delay(500, ctx.RequestAborted); } catch(OperationCanceledException) { break; }
		list = manager.Outside.Inbox(c, false);
	}
	if(ack ?? true) manager.Outside.Inbox(c, true);
	return Results.Ok(list.Select(m => new { file = Path.GetFileName(m.File), headers = m.Headers, m.Text }));
});
ext.MapGet("/agents/{id}/transcript", (HttpContext ctx, string id, long? since, int? limit) => {
	if(ClientOf(ctx) is null) return NeedsClient();
	if(manager.Get(id) is not { } a) return Results.NotFound();
	static string? Cut(string? s, int n) => s == null || s.Length <= n ? s : s[..n] + $"… ({s.Length - n} more chars)";
	return Results.Ok(a.Transcript.Entries.Where(e => e.Seq > (since ?? 0) && e.Kind is not (EntryKind.Reasoning or EntryKind.Elide))
		.TakeLast(Math.Clamp(limit ?? 50, 1, 500))
		.Select(e => new { e.Seq, e.Ts, e.Kind, e.From, Text = Cut(e.Text, 4000), e.Name, Arguments = Cut(e.Arguments, 1000), e.IsError }));
});
ext.MapGet("/agents/{id}/tasks", (HttpContext ctx, string id) =>
	ClientOf(ctx) is null ? NeedsClient() : manager.Get(id) is { } a ? Results.Ok(a.Tasks) : Results.NotFound());

api.MapGet("/usage", (int? days) => ledger.Recent(Math.Clamp(days ?? 7, 1, 90)));
api.MapGet("/models", async (CancellationToken ct) => {
	try {
		return Results.Ok(await backend.ListModelsAsync(ct));
	} catch(Exception e) when(e is NotConnectedException or InvalidOperationException or TransientModelException or RateLimitedException) {
		return Results.Problem(e.Message, statusCode: 502);
	}
});
api.MapPut("/config", (ConfigUpdate update) => {
	manager.UpdateConfig(c => c with { Model = Blank(update.Model), Effort = Blank(update.Effort) });
	return Results.Ok();
});
api.MapGet("/agents", () => manager.Agents.Select(AgentSummary));
api.MapPost("/agents", (CreateAgent req) => AgentSummary(manager.CreateChat(req.Name, Blank(req.WorkingDirectory))));
api.MapGet("/agents/{id}/transcript", (string id) =>
	manager.Get(id) is { } a ? Results.Ok(a.Transcript.Entries) : Results.NotFound());
api.MapPost("/agents/{id}/messages", (string id, SendMessage req) => {
	if(manager.Get(id) is not { } a) return Results.NotFound();
	if(string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest();
	a.Send(new Inbound("Sera", req.Text, req.Mode ?? SendMode.Queued));
	return Results.Accepted();
});
api.MapPut("/agents/{id}/settings", (string id, ConfigUpdate update) => {
	if(manager.Get(id) is not { } a) return Results.NotFound();
	a.UpdateSettings(Blank(update.Model), Blank(update.Effort));
	return Results.Ok(AgentSummary(a));
});
api.MapPost("/agents/{id}/interrupt", (string id) => {
	if(manager.Get(id) is not { } a) return Results.NotFound();
	a.Interrupt();
	return Results.Ok();
});
api.MapGet("/agents/{id}/notebook", (string id, int? limit) =>
	manager.Get(id) is { } a ? Results.Ok(a.Notebook().TakeLast(Math.Clamp(limit ?? 100, 1, 2000))) : Results.NotFound());
api.MapPost("/agents/{id}/notebook/{n:int}/rerun", async (string id, int n, CancellationToken ct) => {
	if(manager.Get(id) is not { } a) return Results.NotFound();
	try {
		var (run, sameOutput, sameState) = await a.RerunCell(n, ct);
		return Results.Ok(new { run, sameOutput, sameState });
	} catch(KeyNotFoundException) {
		return Results.NotFound();
	}
});
api.MapGet("/agents/{id}/notebook.ipynb", (string id) =>
	manager.Get(id) is { } a ? Results.File(System.Text.Encoding.UTF8.GetBytes(a.NotebookIpynb()), "application/x-ipynb+json", $"{a.Info.Name}-notebook.ipynb") : Results.NotFound());
api.MapGet("/agents/{id}/tasks", (string id) =>
	manager.Get(id) is { } a ? Results.Ok(a.Tasks) : Results.NotFound());
api.MapGet("/agents/{id}/schedules", (string id) =>
	manager.Get(id) is { } a ? Results.Ok(a.Schedules.Where(s => !s.Done)) : Results.NotFound());
api.MapDelete("/agents/{id}/schedules/{scheduleId:int}", (string id, int scheduleId) =>
	manager.Get(id) is { } a && a.RemoveSchedule(scheduleId) ? Results.Ok() : Results.NotFound());
object ProcessSummary(Agent a, BackgroundProcess p) => new {
	agentId = a.Info.Id, agentName = a.Info.Name, p.Id, p.Name, p.Command, p.WorkingDirectory, p.Pid, p.Started, p.Stopped, p.LogPath,
	state = p.Stopped != null ? "stopped" : Posix.Alive(p.Pid) ? "running" : "exited",
};
api.MapGet("/processes", () => manager.Agents.SelectMany(a => a.Processes.OrderByDescending(p => p.Started).Select(p => ProcessSummary(a, p))));
api.MapGet("/agents/{id}/processes", (string id) =>
	manager.Get(id) is { } a ? Results.Ok(a.Processes.Where(p => p.Stopped == null).Select(p => ProcessSummary(a, p))) : Results.NotFound());
api.MapPost("/agents/{id}/processes/{n:int}/stop", async (string id, int n) =>
	manager.Get(id) is { } a && await a.StopProcess(n) is { } p ? Results.Ok(ProcessSummary(a, p)) : Results.NotFound());
api.MapGet("/agents/{id}/processes/{n:int}/log", (string id, int n, int? lines) => {
	if(manager.Get(id)?.Processes.FirstOrDefault(p => p.Id == n) is not { } p) return Results.NotFound();
	if(!File.Exists(p.LogPath)) return Results.Text("");
	using var stream = new FileStream(p.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
	var take = Math.Min(stream.Length, 256 * 1024);
	stream.Seek(-take, SeekOrigin.End);
	var text = new StreamReader(stream).ReadToEnd();
	var all = text.Split('\n');
	return Results.Text(string.Join('\n', all.TakeLast(Math.Clamp(lines ?? 200, 1, 5000))));
});
api.MapGet("/tools", (string? projectId) => {
	var dirs = new List<(string Scope, string Dir)> { ("global", scriptTools.GlobalDir) };
	if(manager.GetProject(projectId) is { } p) dirs.Add((p.Info.Id, ScriptToolSource.ProjectDir(p)));
	return dirs.Where(d => Directory.Exists(d.Dir)).SelectMany(d => Directory.EnumerateFiles(d.Dir, "*.ps1").Select(f => {
		var name = Path.GetFileNameWithoutExtension(f);
		var script = File.ReadAllText(f);
		string? description = null, error = null;
		try { description = ScriptTool.Parse(name, script, f, d.Scope).Description; } catch(ToolException e) { error = e.Message; }
		return new { name, scope = d.Scope == "global" ? "global" : "project", path = f, script, description, error, modified = File.GetLastWriteTimeUtc(f),
			pendingReview = !manager.Guard.ToolApproved(f, script) };
	})).OrderBy(t => t.name);
});
api.MapPost("/tools/approve", (ToolApproval req) => {
	var dirs = new List<string> { scriptTools.GlobalDir };
	dirs.AddRange(manager.Projects.Select(ScriptToolSource.ProjectDir));
	var path = Path.GetFullPath(req.Path);
	if(!dirs.Any(d => Path.GetDirectoryName(path) == Path.GetFullPath(d)) || !path.EndsWith(".ps1") || !File.Exists(path)) return Results.NotFound();
	manager.Guard.ApproveTool(path, File.ReadAllText(path));
	return Results.Ok();
});
api.MapGet("/agents/{id}/images/{file}", (string id, string file) => {
	if(manager.Get(id) is not { } a || file.Contains('/') || file.Contains('\\') || file.StartsWith('.')) return Results.NotFound();
	var path = Path.Combine(a.Directory, "images", file);
	return File.Exists(path) && ImageFiles.MimeFor(path) is { } mime ? Results.File(path, mime) : Results.NotFound();
});
api.MapGet("/mcp", () => {
	var running = mcp.Running.ToList();
	return manager.Config.Mcp.Select(kv => new {
		name = kv.Key, kv.Value.Command, kv.Value.Args, kv.Value.Disabled,
		tools = mcp.Schemas.TryGetValue(kv.Key, out var s) ? s.Tools.Select(t => t["name"]?.GetValue<string>()).ToList() : [],
		readAt = mcp.Schemas.TryGetValue(kv.Key, out var s2) ? s2.ReadAt : (DateTimeOffset?) null,
		error = mcp.Schemas.TryGetValue(kv.Key, out var s3) ? s3.Error : null,
		running = running.Where(r => r.Server == kv.Key).Select(r => new { agentId = r.AgentId, agentName = manager.Get(r.AgentId)?.Info.Name, pid = r.Pid }),
	});
});
// config.json is Sera's to edit by hand; this picks up its MCP section and re-reads changed servers.
api.MapPost("/mcp/refresh", async () => {
	var fresh = Config.Load(home);
	manager.UpdateConfig(c => c with { Mcp = fresh.Mcp });
	await mcp.RefreshAsync(true, CancellationToken.None);
	return Results.Ok();
});
api.MapPost("/projects", (CreateProject req) => {
	try {
		var (project, orchestrator) = manager.CreateProject(req.Name ?? "", req.Bindings ?? [], req.Prompt, req.WorktreesByDefault ?? false);
		if(Blank(req.Brief) is { } brief) orchestrator.Send(new Inbound("Sera", brief, SendMode.Queued));
		return Results.Ok(ProjectSummary(project));
	} catch(ArgumentException e) {
		return Results.Problem(e.Message, statusCode: 400);
	}
});
api.MapGet("/projects/{id}/memory", (string id) =>
	manager.GetProject(id) is { } p ? Results.Ok(new { path = p.MemoryPath, text = p.Memory, log = p.MemoryLog(), prompt = p.Prompt }) : Results.NotFound());
api.MapGet("/inbox", () => manager.SeraInbox().AsEnumerable().Reverse());
api.MapPost("/questions/{id}/answer", (string id, AnswerRequest req) => {
	try {
		return Results.Ok(manager.AnswerQuestion(id, req.Choice, req.Text));
	} catch(KeyNotFoundException) {
		return Results.NotFound();
	} catch(Exception e) when(e is InvalidOperationException or ArgumentException) {
		return Results.Problem(e.Message, statusCode: 409);
	}
});
api.MapGet("/messages", (string? agentId, string? projectId, int? limit) =>
	manager.AgentMessages(agentId, projectId, Math.Clamp(limit ?? 500, 1, 5000)).AsEnumerable().Reverse());
api.Map("/events", async (HttpContext ctx) => {
	if(!ctx.WebSockets.IsWebSocketRequest) {
		ctx.Response.StatusCode = 400;
		return;
	}
	using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
	await hub.Serve(socket, ctx.RequestAborted);
});

manager.LoadAll();
new Scheduler(manager).Start(app.Lifetime.ApplicationStopping);
backups.Start(app.Lifetime.ApplicationStopping, Console.Error.WriteLine);
_ = Task.Run(() => mcp.RefreshAsync(false, app.Lifetime.ApplicationStopping));
app.Lifetime.ApplicationStopping.Register(() => mcp.StopAll().Wait(TimeSpan.FromSeconds(5)));
Console.WriteLine($"Marten home: {home.Root}");
Console.WriteLine($"Backups: {backups.Directory}{(backups.Encrypting ? " (encrypted)" : "")}");
Console.WriteLine(webRoot == null
	? "No built UI found (web/dist). Run `npm run build` in web/, or set MARTEN_WEB_ROOT."
	: $"Marten UI: http://127.0.0.1:{config.Port}/#token={security.Token}");
app.Run();

static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

static string? FindWebRoot() {
	if(Environment.GetEnvironmentVariable("MARTEN_WEB_ROOT") is { Length: > 0 } env) return Path.GetFullPath(env);
	for(var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent) {
		var candidate = Path.Combine(dir.FullName, "web", "dist");
		if(File.Exists(Path.Combine(candidate, "index.html"))) return candidate;
	}
	return null;
}

record CreateProject(string? Name, List<string>? Bindings, string? Prompt, bool? WorktreesByDefault, string? Brief);
record ConfigUpdate(string? Model, string? Effort);
record CreateAgent(string? Name, string? WorkingDirectory);
record SendMessage(string Text, SendMode? Mode);
record PauseRequest(bool Paused);
record AnswerRequest(int? Choice, string? Text);
record BackendKey(string? Key);
record CloseRequest(bool Closed);
record ToolApproval(string Path);
record ClientRequest(string? Name, bool? Rotate);
record ExtMessage(string? To, string? Text, SendMode? Mode);
