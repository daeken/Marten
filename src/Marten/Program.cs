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
var tools = new ToolRegistry()
	.Add(new ReadFileTool())
	.Add(new EditFileTool(new MemoryGuard(getManager)))
	.Add(new PowerShellTool(() => managerRef!.Config.CommandTimeoutSeconds, () => managerRef!.Config.StopGraceSeconds, new ChildProcessWatcher()))
	.Add(new SendMessageTool(getManager))
	.Add(new TaskAddTool(getManager))
	.Add(new TaskUpdateTool(getManager))
	.Add(new SpawnAgentTool(getManager))
	.Add(new CloseAgentTool(getManager))
	.Add(new CreateProjectTool(getManager))
	.Add(new ElideResultsTool())
	.Add(new SetModelTool(getManager));
var manager = managerRef = new AgentManager(home, config, backend, tools);
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
api.Map("/events", async (HttpContext ctx) => {
	if(!ctx.WebSockets.IsWebSocketRequest) {
		ctx.Response.StatusCode = 400;
		return;
	}
	using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
	await hub.Serve(socket, ctx.RequestAborted);
});

manager.LoadAll();
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
