using Marten.Agents;
using Marten.Models;
using Marten.Models.OpenAI;
using Marten.Projects;
using Marten.Storage;
using Marten.Tools;
using Marten.Web;
using Microsoft.Extensions.FileProviders;

var home = new Home();
var config = Config.Load(home);
var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Marten/0.1");
var auth = new ChatGptAuth(home, http);
var backend = new ResponsesBackend(auth, http);
AgentManager? managerRef = null;
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
var manager = managerRef = new AgentManager(home, config, backend, tools) { Launcher = new ProcessLauncher(childWatcher), Mcp = mcp };
var hub = new EventHub();
manager.Events += hub.Broadcast;
var security = new Security(home, config);

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
	app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
}

object AgentSummary(Agent a) => new {
	a.Info.Id, a.Info.Kind, a.Info.Name, a.Info.Created, a.Info.WorkingDirectory, a.Info.Model, a.Info.Effort,
	a.Status, a.StatusDetail, Pending = a.PendingCount, a.AbandonedRunspaces,
	a.Info.ProjectId, a.Info.ParentId, a.Info.Closed, a.Info.Worktree, a.NudgesStopped, a.Info.Paused, PausedEffective = a.IsPaused,
	OpenTasks = a.Tasks.Count(t => t.State != TaskState.Done),
};

object ProjectSummary(Project p) => new { p.Info.Id, p.Info.Name, p.Info.Created, p.Info.Bindings, p.Info.WorktreesByDefault, p.Info.OrchestratorId, p.Info.Paused };

var api = app.MapGroup("/api");
api.MapGet("/status", () => new {
	auth = new { connected = auth.Connected, email = auth.Email, signingIn = auth.SigningIn, lastError = auth.LastError },
	config = new { manager.Config.Model, manager.Config.Effort, DefaultModel = manager.KnownDefaultModel },
	agents = manager.Agents.Select(AgentSummary),
	projects = manager.Projects.Select(ProjectSummary),
	inbox = manager.SeraInbox().Count,
	openQuestions = manager.SeraInbox().Count(m => m.Question && m.Answer == null),
	paused = manager.PausedAll,
});
api.MapPost("/pause", (PauseRequest req) => {
	manager.SetPausedAll(req.Paused);
	return Results.Ok();
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
		return new { name, scope = d.Scope == "global" ? "global" : "project", path = f, script, description, error, modified = File.GetLastWriteTimeUtc(f) };
	})).OrderBy(t => t.name);
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
_ = Task.Run(() => mcp.RefreshAsync(false, app.Lifetime.ApplicationStopping));
app.Lifetime.ApplicationStopping.Register(() => mcp.StopAll().Wait(TimeSpan.FromSeconds(5)));
Console.WriteLine($"Marten home: {home.Root}");
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
