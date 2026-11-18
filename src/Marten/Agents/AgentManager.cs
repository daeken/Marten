using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using Marten.Models;
using Marten.Storage;
using Marten.Tools;

namespace Marten.Agents;

public sealed class AgentManager {
	public Home Home { get; }
	public Config Config { get; private set; }
	public IModelBackend Backend { get; }
	public ToolRegistry Tools { get; }
	readonly ConcurrentDictionary<string, Agent> agents = new();
	readonly SemaphoreSlim modelGate = new(1, 1);
	string? resolvedDefaultModel;

	/// <summary>Raised for every change the UI shows: entries, deltas, status, flags.</summary>
	public event Action<object>? Events;

	public AgentManager(Home home, Config config, IModelBackend backend, ToolRegistry tools) {
		Home = home;
		Config = config;
		Backend = backend;
		Tools = tools;
		WriteDefaultPrompts();
	}

	public IEnumerable<Agent> Agents => agents.Values.OrderBy(a => a.Info.Created);
	public Agent? Get(string id) => agents.GetValueOrDefault(id);

	public void Publish(object ev) => Events?.Invoke(ev);

	public void UpdateConfig(Func<Config, Config> change) {
		Config = change(Config);
		Config.Save(Home);
		if(Config.Model != null) resolvedDefaultModel = null;
		Publish(new { type = "config" });
	}

	public async Task<string> DefaultModel(CancellationToken ct) {
		if(Config.Model != null) return Config.Model;
		if(resolvedDefaultModel != null) return resolvedDefaultModel;
		await modelGate.WaitAsync(ct);
		try {
			resolvedDefaultModel ??= (await Backend.ListModelsAsync(ct)).FirstOrDefault()
				?? throw new InvalidOperationException("The backend lists no models. Set one in config.json.");
			return resolvedDefaultModel;
		} finally {
			modelGate.Release();
		}
	}

	/// <summary>Load every agent from disk, repair interrupted turns, and start their loops.</summary>
	public void LoadAll() {
		foreach(var dir in Directory.GetDirectories(Home.Agents)) {
			var infoPath = Path.Combine(dir, "agent.json");
			if(!File.Exists(infoPath)) continue;
			var info = JsonIO.Load<AgentInfo?>(infoPath, () => null);
			if(info == null) continue;
			var instructions = File.ReadAllText(Path.Combine(dir, "system.md"));
			var agent = new Agent(this, dir, info, instructions);
			agents[info.Id] = agent;
			agent.Recover();
			agent.Start();
		}
	}

	public Agent CreateChat(string? name, string? workingDirectory = null) {
		var id = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
		var info = new AgentInfo {
			Id = id, Kind = AgentKind.Chat, Name = string.IsNullOrWhiteSpace(name) ? "Chat" : name.Trim(),
			Created = DateTimeOffset.UtcNow,
			WorkingDirectory = workingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
		};
		var dir = Home.AgentDir(id);
		Directory.CreateDirectory(dir);
		// The system prompt is fixed at spawn so the cached prefix stays stable; compaction rebuilds it.
		var instructions = BuildInstructions(info);
		AtomicFile.Write(Path.Combine(dir, "system.md"), instructions);
		JsonIO.Save(Path.Combine(dir, "agent.json"), info);
		var agent = new Agent(this, dir, info, instructions);
		agents[id] = agent;
		agent.Start();
		Publish(new { type = "agents" });
		return agent;
	}

	string BuildInstructions(AgentInfo info) {
		var kind = info.Kind.ToString().ToLowerInvariant();
		return string.Join("\n\n",
			ReadPrompt("marten.md"),
			ReadPrompt($"{kind}.md"),
			$"""
			Environment:
			- Agent id: {info.Id} ({kind}), name "{info.Name}"
			- Working directory: {info.WorkingDirectory}
			- OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})
			- Marten home: {Home.Root}
			- Spawned: {info.Created:yyyy-MM-dd HH:mm} UTC
			""");
	}

	string ReadPrompt(string file) => File.ReadAllText(Path.Combine(Home.Prompts, file)).Trim();

	void WriteDefaultPrompts() {
		var asm = Assembly.GetExecutingAssembly();
		foreach(var resource in asm.GetManifestResourceNames().Where(n => n.Contains(".Prompts.") && n.EndsWith(".md"))) {
			var file = resource[(resource.IndexOf(".Prompts.") + ".Prompts.".Length)..];
			var path = Path.Combine(Home.Prompts, file);
			if(File.Exists(path)) continue;
			using var stream = asm.GetManifestResourceStream(resource)!;
			using var reader = new StreamReader(stream);
			AtomicFile.Write(path, reader.ReadToEnd());
		}
	}
}
